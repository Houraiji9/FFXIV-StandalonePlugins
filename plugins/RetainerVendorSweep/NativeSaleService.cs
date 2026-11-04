using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace RetainerVendorSweep;

// One character / one bell session. Only Inventory1..4 are sale sources. No market-board operations.
internal sealed unsafe class NativeSaleService : IVendorSession, IDisposable
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly VendorNativeCompatibility compatibility;
    private readonly Dictionary<nint, long> generations = [];
    private SaleRules rules = new();
    private VendorDelays delays = new();
    private Dictionary<(int, int), BagSlot> balance = [];
    private VendorItem? source;
    private long gilBefore, retainerGilBefore;
    private nint shopOwner;
    private bool disposed, stopping, ownBell, requestPending, salePending, confirmationAnswered, confirmationCancelled, confirmationClosed;
    private bool closeInventorySent, closeListSent, quitRetainerSent, nativeCallback, saleAccepted, saleAccounted;
    private long serial, openedAfter, saleOpenedAfter, deadline, nextActionAt, lastActionAt = long.MinValue / 2, lastActionFrame, stableAt;
    private string? pendingAction;
    private ulong cid, retainerId;
    private nint conversationOwner;
    private string retainerName = "";
    private Lease? listLease, menuLease, inventoryLease, confirmationLease;
    private Step step;
    private Response response;
    private bool responseObserved = true;
    private VendorSessionState ending;
    private readonly record struct Lease(nint Pointer, long Generation, nint Receiver, ulong Kind);
    private sealed record BagSlot(VendorItem Item, byte Flags, ushort Spiritbond, ushort Condition, uint Glamour,
        ulong Materia, ushort LastMateria, ulong Grades, ushort Stains);
    private enum Step { Bell, List, Menu, Inventory, Scan, Sale, Interval, ReturnMenu, ReturnList, Closing }
    private enum Response { None, BellList, Menu, Inventory, ReturnMenu, ReturnList }

    public VendorSessionState State { get; private set; }
    public bool Blocking => ownBell || requestPending || salePending || State is VendorSessionState.Running or VendorSessionState.Stopping;
    public string Detail { get; private set; } = "尚未开始本角色出售。";
    public long SoldItems { get; private set; }
    public long Gil { get; private set; }
    public Func<string?>? CheckConflict { get; set; }

    public NativeSaleService(IFramework framework, IPlayerState player, IObjectTable objects, ITargetManager targets,
        ICondition condition, IGameGui gameGui, IDataManager data, IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.framework = framework; this.player = player; this.objects = objects; this.targets = targets;
        this.condition = condition; this.gameGui = gameGui; this.data = data; this.lifecycle = lifecycle; this.log = log;
        compatibility = new(data);
        lifecycle.RegisterListener(AddonEvent.PostSetup, OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnLifecycle);
    }

    public string? CheckSupport() => compatibility.Check();
    private static long Now => Environment.TickCount64;
    private bool SessionMatches => player.IsLoaded && player.ContentId == cid && objects.LocalPlayer != null;
    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
    private static bool Visible(AtkUnitBase* addon) => addon != null && addon->IsVisible;
    private bool Visible(string name) => Visible(Addon(name));
    private AtkUnitBase* RetainerInventory
    {
        get
        {
            var agent = AgentRetainer.Instance(); var manager = RaptureAtkUnitManager.Instance();
            return agent == null || manager == null || agent->AddonId == 0 ? null : manager->GetAddonById((ushort)agent->AddonId);
        }
    }
    private bool RetainerMatches => RetainerManager.Instance() is var r && r != null && retainerId != 0
        && r->LastSelectedRetainerId == retainerId && condition[ConditionFlag.OccupiedSummoningBell];

    public void Begin(ulong character, SaleRules selectedRules, VendorDelays selectedDelays)
    {
        if(disposed || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("出售必须在有效游戏框架线程启动。");
        if(Blocking) throw new InvalidOperationException("上次出售或其窗口尚未清理。");

        // A new attempt must never expose the preceding character's terminal counters. Keep the
        // guards above outside the rejected-preflight boundary: an existing owned request must not
        // be reset, and a caller on the wrong thread cannot mutate this framework-owned state.
        State = VendorSessionState.Idle; SoldItems = Gil = 0;
        cid = character;
        Detail = "本角色尚未开始出售，正在检查启动条件。";
        retainerId = 0; conversationOwner = shopOwner = 0; retainerName = "";
        listLease = menuLease = inventoryLease = confirmationLease = null;
        stopping = ownBell = requestPending = salePending = saleAccepted = saleAccounted = false;
        closeInventorySent = closeListSent = quitRetainerSent = false;
        confirmationAnswered = confirmationCancelled = confirmationClosed = false;
        response = Response.None; responseObserved = true;
        source = null; pendingAction = null; openedAfter = saleOpenedAfter = stableAt = nextActionAt = deadline = 0;
        gilBefore = retainerGilBefore = 0; ending = VendorSessionState.Stopped; step = Step.Bell;
        balance = []; rules = new(); delays = new();

        SaleRules preparedRules;
        VendorDelays preparedDelays;
        Dictionary<(int, int), BagSlot> preparedBalance;
        bool hasCandidates;
        try
        {
            // Only reads and managed preparation belong here. No bell interaction, callback,
            // queued native intent or Running state may be introduced into this catch boundary.
            if(CheckSupport() is { } unsupported) throw new InvalidOperationException(unsupported);
            ArgumentNullException.ThrowIfNull(selectedRules);
            ArgumentNullException.ThrowIfNull(selectedDelays);
            if(selectedRules.Validate() is { } invalid) throw new InvalidOperationException(invalid);
            preparedRules = selectedRules.Freeze(); preparedDelays = selectedDelays.Freeze();
            if(cid == 0 || !SessionMatches) throw new InvalidOperationException("出售角色与当前角色不符。");
            if(!UiReleased()) throw new InvalidOperationException("请先结束当前交互，再开始出售。");
            preparedBalance = ReadBags();
            hasCandidates = preparedBalance.Values.Any(x => preparedRules.Matches(x.Item));
        }
        catch(Exception ex)
        {
            Detail = $"本角色尚未开始出售：{ex.Message}";
            throw new VendorBeginRejectedException(Detail, ex);
        }

        // Publish the new session only after all read-only preparation succeeded. Tick is the
        // sole place that can subsequently send an action; unknown errors outside preflight retain
        // the coordinator's conservative stop/drain semantics.
        rules = preparedRules; delays = preparedDelays; balance = preparedBalance;
        ending = VendorSessionState.Completed; Sent();
        Detail = hasCandidates ? "准备在雇员铃按 NPC 价格出售本角色背包中的匹配物品。"
            : "本角色背包没有符合出售规则且未受保护的物品。";
        State = hasCandidates ? VendorSessionState.Running : VendorSessionState.Completed;
    }

    public void Stop()
    {
        if(stopping && ending != VendorSessionState.Completed) return;
        stopping = true; pendingAction = null; nextActionAt = 0;
        ending = State == VendorSessionState.Failed ? VendorSessionState.Failed : VendorSessionState.Stopped;
        if(!Blocking) { State = ending; Detail = "出售已停止。"; return; }
        if(State != VendorSessionState.Failed) State = VendorSessionState.Stopping;
        Detail = "已撤销后续出售；仅等待在途结果或取消自己的确认窗并退出雇员界面。";
        Sent();
    }

    public void Tick()
    {
        if(disposed || !Blocking || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return;
        try
        {
            if(CheckSupport() is { } unsupported) { Fail(unsupported + " 请手动关闭剩余界面。"); return; }
            if(!SessionMatches)
            {
                State = ending = VendorSessionState.Failed; stopping = true; pendingAction = null;
                ownBell = requestPending = salePending = false;
                Detail = "当前角色会话已结束；旧请求停止跟踪，未操作新角色。已发送出售请核对实际结果。"; return;
            }
            if(Now >= deadline)
            {
                if(!stopping) Fail("雇员出售响应超时，后续操作已撤销，未知结果不会重试。");
                else Detail = "已停止，仍等待本次请求或界面清理；请核对物品与金币。未观察到响应不能当作已撤销。";
            }
            if(salePending) { ObserveSale(); return; }
            if(stopping) { Cleanup(); return; }
            if(ForeignPopup()) { Fail("出售期间出现了其他交互窗口，拒绝继续。"); return; }
            if(!BagsEqual(balance, ReadBags())) { Fail("出售步骤之间背包发生非预期变化，已停止。"); return; }
            if(step is not (Step.Bell or Step.List or Step.ReturnList or Step.Closing) && !(step == Step.Menu && requestPending) && !RetainerMatches)
            { Fail("当前雇员身份变化，拒绝继续。"); return; }
            switch(step)
            {
                case Step.Bell: OpenBell(); break;
                case Step.List: SelectRetainer(); break;
                case Step.Menu: OpenInventory(); break;
                case Step.Inventory: WaitInventory(); break;
                case Step.Scan: SelectAndSell(); break;
                case Step.Interval: if(Gate("item-interval", delays.ItemIntervalMs)) { step = Step.Scan; Sent(); } break;
                case Step.ReturnMenu: QuitRetainer(); break;
                case Step.ReturnList: WaitReturnList(); break;
                case Step.Closing: Cleanup(); break;
            }
        }
        catch(Exception ex)
        {
            log.Error(ex, "Retainer vendor check failed; no sale retry is authorized.");
            Fail($"出售检查失败（{ex.GetType().Name}），请核对物品、金币及雇员界面。");
        }
    }

    private void OpenBell()
    {
        if(!UiReleased()) { Fail("开始出售前出现了其他交互。"); return; }
        var bellName = data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ExtractText();
        var local = objects.LocalPlayer!;
        var bell = objects.Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject)
            .Where(x => x.IsTargetable && x.Name.TextValue == bellName)
            .OrderBy(x => Vector3.Distance(local.Position, x.Position)).FirstOrDefault();
        if(bell == null || Vector3.Distance(local.Position, bell.Position) >= 4.6f)
        { Fail("附近没有可交互的雇员铃；不会自动导航或传送。"); return; }
        // Changing target and interacting are separate actions and cannot share a dispatch frame.
        if(targets.Target?.Address != bell.Address)
        { if(Gate("target-bell", delays.BellMs)) targets.Target = bell; return; }
        if(!Gate("interact-bell", delays.BellMs)) return;
        ownBell = true; Expect(Response.BellList); openedAfter = serial;
        step = Step.List; Sent();
        Invoke(() => TargetSystem.Instance()->InteractWithObject((NativeObject*)bell.Address, false));
        Detail = "已交互雇员铃，等待本次列表。";
    }

    private void SelectRetainer()
    {
        var addon = Addon("RetainerList"); var agent = AgentRetainerList.Instance();
        if(!Visible(addon) || agent == null || !condition[ConditionFlag.OccupiedSummoningBell]) return;
        if(!LeaseWindow(addon, (nint)agent, 0, ref listLease, openedAfter)) return;
        Observe(Response.BellList);
        conversationOwner = (nint)agent->OpenerEventInterface;
        if(conversationOwner == 0) return;
        var manager = RetainerManager.Instance();
        if(manager == null || !manager->IsReady) return;
        var chosen = -1; ulong selectedId = 0; string selectedName = "";
        for(uint i = 0; i < Math.Min(10, (int)agent->RetainerCount); i++)
        {
            var r = manager->GetRetainerBySortedIndex(i);
            if(r == null || r->RetainerId == 0 || !r->Available) continue;
            var index = (int)i; var name = r->NameString;
            if(addon->AtkValues == null || addon->AtkValuesCount <= 11 + index * 10 || !ReadBool(addon->AtkValues[11 + index * 10])) continue;
            if(ReadText(addon->AtkValues[3 + index * 10]) != name || agent->Retainers[index].Name.ToString() != name) continue;
            chosen = index; selectedId = r->RetainerId; selectedName = name; break;
        }
        if(chosen < 0) { Fail("本角色没有可用雇员，无法通过铃出售背包物品。"); return; }
        if(!Gate("select-retainer", delays.SelectRetainerMs)) return;
        retainerId = selectedId; retainerName = selectedName;
        openedAfter = serial; menuLease = null; Expect(Response.Menu); step = Step.Menu; Sent();
        var values = stackalloc AtkValue[4];
        values[0] = Int(2); values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)chosen };
        values[2] = default; values[3] = default;
        Invoke(() => addon->FireCallback(4, values, true));
        Detail = "已选择可用雇员，等待物品管理菜单。";
    }

    private void OpenInventory()
    {
        if(!RetainerMatches) return;
        var menu = Addon("SelectString");
        if(!Visible(menu)) { AdvanceTalk(); return; }
        if(!menu->IsReady) return;
        var entry = FindMenuEntry((AddonSelectString*)menu, 2378);
        if(entry < 0) { Fail("本次雇员菜单没有唯一的物品管理选项。"); return; }
        if(!LeaseConversation(menu, ref menuLease)) return;
        Observe(Response.Menu);
        if(!Gate("open-inventory", delays.OpenInventoryMs)) return;
        openedAfter = serial; inventoryLease = null; Expect(Response.Inventory); step = Step.Inventory; Sent();
        Callback(menu, entry);
    }

    private void WaitInventory()
    {
        if(AdvanceTalk()) return;
        var agent = AgentRetainer.Instance(); var addon = RetainerInventory;
        if(agent == null || !agent->IsAgentActive() || !Visible(addon)) return;
        if(!LeaseWindow(addon, (nint)agent, 0, ref inventoryLease, openedAfter)) return;
        if(agent->ShopEventHandler == null || !agent->ShopEventHandler->IsTradingWithRetainer) return;
        shopOwner = (nint)agent->ShopEventHandler;
        Observe(Response.Inventory); step = Step.Scan; Sent();
    }

    private void SelectAndSell()
    {
        if(!InventoryOwned()) { Fail("本次雇员库存界面已关闭或被替换。"); return; }
        if(Visible("SelectString") || Visible("Talk") || Visible("RetainerList")) return;
        var bags = ReadBags();
        if(!BagsEqual(balance, bags)) { Fail("出售前背包内容发生变化。"); return; }
        var candidate = bags.Values.Select(x => x.Item).Where(rules.Matches).OrderBy(x => x.Container).ThenBy(x => x.Slot).FirstOrDefault();
        if(candidate == null) { stopping = true; step = Step.Closing; Cleanup(); return; }
        var handler = CurrentShop();
        var proxy = (ShopEventHandler.YesNoProxy*)compatibility.SaleConfirmationReceiver;
        // WaitingForSellConfirm is left set by the native YesNo callback and reset when loading the
        // next item. It cannot be used as an idle predicate after our previous prompt has closed.
        if(handler == null || handler->StartingSell || handler->StartingBuy || handler->WaitingForTransactionToFinish || proxy->AddonId != 0)
        { Fail("雇员出售处理器已有其他在途操作，拒绝接管。"); return; }
        var manager = InventoryManager.Instance();
        var gil = (long)manager->GetGil();
        if(!SaleResultRules.TryExpectedGil(candidate.UnitPrice, candidate.Quantity, gil, out var expected) || expected > 999_999_999)
        { Fail("本次整堆出售会超过角色金币上限，停止且不会部分出售。"); return; }
        if(!Gate("sell-stack", delays.ContextMs)) return;
        // Re-read on the dispatch frame; protection rules and all source attributes are checked again.
        if(!InventoryOwned() || !BagsEqual(balance, ReadBags()) || !rules.Matches(candidate))
        { Fail("直售提交前物品、保护规则或界面归属变化。"); return; }
        source = candidate; gilBefore = gil; retainerGilBefore = manager->GetRetainerGil();
        salePending = true; saleAccepted = saleAccounted = false;
        confirmationLease = null; confirmationAnswered = confirmationCancelled = confirmationClosed = false;
        saleOpenedAfter = serial; step = Step.Sale; Sent();
        Invoke(() => AgentRetainer.Instance()->HandleCallback((uint)candidate.Slot, (InventoryType)candidate.Container, 0, 5));
        handler = CurrentShop();
        // HandleCallback sets this source and StartingSell synchronously, before asynchronously loading Item.
        saleAccepted = HandlerMatches(handler) && handler->StartingSell;
        if(!saleAccepted) { Fail("游戏未给出本次整堆出售的接收证明；结果不明，不会重试。"); return; }
        Detail = $"已提交物品 #{candidate.ItemId} × {candidate.Quantity}，等待原生报价与出售结果。";
    }

    private void ObserveSale()
    {
        if(!InventoryOwned() || source == null || !saleAccepted)
        { Fail("在途出售的角色、雇员、处理器或接收证明丢失；请核对结果，不会重试。"); return; }
        var handler = CurrentShop();
        if(!HandlerMatches(handler)) { Fail("在途出售被其他物品操作替换，拒绝继续或确认。"); return; }
        if(Visible("InputNumeric") || Visible("ContextMenu") || Visible("Trade"))
        { Fail("整堆直售期间出现不属于本协议的窗口；请手动核对。"); return; }
        if(handler->StartingSell) return;
        if(handler->SellPrice <= 0 || handler->SellPrice != source.UnitPrice)
        { Fail("原生出售报价与冻结物品价格不符，拒绝确认，请手动取消。"); return; }
        var after = ReadBags();
        var manager = InventoryManager.Instance();
        if(!OtherSlotsEqual(balance, after, source) || manager->GetRetainerGil() != retainerGilBefore)
        { Fail("出售期间其他背包格或雇员金币发生变化，结果不明确，不会继续。"); return; }
        var current = after.GetValueOrDefault((source.Container, source.Slot))?.Item;
        var result = SaleResultRules.ObserveSale(source, current, gilBefore, manager->GetGil(), source.Quantity);
        if(result == SaleCheck.Invalid) { Fail("出售数量或角色金币增量不符，已停止且不重试。"); return; }
        if(Visible("SelectYesno"))
        {
            // A successful quantity/gil pair cannot authorize clicking a late or unrelated prompt.
            if(result == SaleCheck.Complete)
            {
                if(confirmationAnswered && !confirmationCancelled && confirmationLease is { } answered && Matches(Addon("SelectYesno"), answered))
                { Detail = "出售数量与金币已核实，等待已提交的原确认窗关闭。"; return; }
                Fail("出售结果已经变化但确认窗归属不明，请手动核对。"); return;
            }
            ProcessConfirmation(stopping); return;
        }
        if(result == SaleCheck.Complete)
        {
            if(!saleAccounted)
            {
                saleAccounted = true; balance = after;
                SoldItems = checked(SoldItems + source.Quantity); Gil = checked(Gil + (long)source.UnitPrice * source.Quantity);
                Sent();
            }
            var activeProxy = (ShopEventHandler.YesNoProxy*)compatibility.SaleConfirmationReceiver;
            if(handler->StartingBuy || handler->WaitingForTransactionToFinish || activeProxy->AddonId != 0)
            { Detail = "出售数量与金币已核实，等待本次原生事务释放；不会重复出售。"; return; }
            salePending = false;
            Detail = $"已核实物品 #{source.ItemId} 出售 {source.Quantity} 个，角色金币增加 {(long)source.UnitPrice * source.Quantity}。";
            source = null; step = Step.Interval; Sent(); return;
        }
        var proxy = (ShopEventHandler.YesNoProxy*)compatibility.SaleConfirmationReceiver;
        if(VendorNativeRules.CancelResponseComplete(confirmationCancelled, confirmationClosed, handler->StartingSell,
           handler->WaitingForTransactionToFinish, proxy->AddonId, BagsEqual(balance, after), manager->GetGil() == gilBefore))
        {
            salePending = false; source = null; step = Step.Closing; Sent();
            Detail = "已取消自己的出售确认，物品和金币未改变。";
        }
    }

    private void ProcessConfirmation(bool cancel)
    {
        if(confirmationAnswered) return;
        var addon = Addon("SelectYesno"); var handler = CurrentShop();
        if(!Visible(addon) || !addon->IsReady || !HandlerMatches(handler) || !handler->WaitingForSellConfirm || handler->TransactionType != 2) return;
        var expected = compatibility.SaleConfirmationReceiver;
        if(!LeaseWindow(addon, expected, 0, ref confirmationLease, saleOpenedAfter)) return;
        var proxy = (ShopEventHandler.YesNoProxy*)expected;
        if(proxy->Handler != handler || proxy->AddonId != addon->Id || addon->AtkValues == null || addon->AtkValuesCount == 0 || ReadText(addon->AtkValues[0]).Length == 0)
        { Fail("出售确认的处理器、窗口编号或提示不符，拒绝自动确认。"); return; }
        if(!BagsEqual(balance, ReadBags()) || InventoryManager.Instance()->GetGil() != gilBefore || source == null || !rules.Matches(source))
        { Fail("确认前源物品、角色金币或保护判定变化，拒绝出售。"); return; }
        if(!Gate(cancel ? "cancel-sale-confirmation" : "confirm-sale", cancel ? delays.CloseMs : delays.ConfirmMs)) return;
        confirmationAnswered = true; confirmationCancelled = cancel; Sent();
        // Exact inspected YesNoProxy: first Int == 0 executes TransactionType 2; any other value cancels.
        Callback(addon, cancel ? 1 : 0);
        Detail = cancel ? "已取消本次出售确认，等待自己的窗口关闭。" : "已确认本次整堆直售，等待物品减少和角色金币增加。";
    }

    private ShopEventHandler* CurrentShop()
    {
        var agent = AgentRetainer.Instance();
        return agent != null && (nint)agent->ShopEventHandler == shopOwner && shopOwner != 0 ? agent->ShopEventHandler : null;
    }
    private bool HandlerMatches(ShopEventHandler* handler) => handler != null && source != null && handler->IsTradingWithRetainer
        && handler->SellInventoryType == (InventoryType)source.Container && handler->SellInventorySlot == source.Slot
        && handler->TransactionItemCount == source.Quantity
        && handler->TransactionItemId == source.ItemId + (source.Hq ? 1_000_000u : 0u);

    private void CloseInventory()
    {
        if(closeInventorySent) return;
        if(!InventoryOwned()) { Fail("无法确认关闭的是本次雇员仓库。"); return; }
        if(!Gate("close-inventory", delays.CloseMs)) return;
        closeInventorySent = true; openedAfter = serial; menuLease = null;
        Expect(Response.ReturnMenu); step = Step.ReturnMenu; Sent();
        Invoke(() => AgentRetainer.Instance()->Hide());
    }
    private void QuitRetainer()
    {
        if(Visible(RetainerInventory)) return;
        var menu = Addon("SelectString");
        if(!Visible(menu)) { AdvanceTalk(); return; }
        if(!menu->IsReady || quitRetainerSent) return;
        var entry = FindMenuEntry((AddonSelectString*)menu, 2383);
        if(entry < 0) { Fail("雇员菜单没有唯一退出选项，请手动结束交互。"); return; }
        if(!LeaseConversation(menu, ref menuLease)) return;
        Observe(Response.Menu, Response.ReturnMenu);
        if(!Gate("quit-retainer", delays.CloseMs)) return;
        quitRetainerSent = true; step = Step.ReturnList; openedAfter = serial;
        Expect(Response.ReturnList); Sent(); Callback(menu, entry);
    }
    private void WaitReturnList()
    {
        var addon = Addon("RetainerList");
        if(!Visible(addon)) { AdvanceTalk(); return; }
        var agent = AgentRetainerList.Instance();
        if(agent == null) return;
        if(listLease is { } old && !Matches(addon, old)) listLease = null;
        if(!LeaseWindow(addon, (nint)agent, 0, ref listLease, openedAfter)) return;
        conversationOwner = (nint)agent->OpenerEventInterface;
        if(conversationOwner == 0) return;
        Observe(Response.ReturnList); menuLease = inventoryLease = null; retainerId = 0;
        stopping = true; step = Step.Closing; Sent();
    }

    private void Cleanup()
    {
        if(!ownBell) { if(!salePending && !requestPending) Finish(); return; }
        if(salePending) { ObserveSale(); return; }
        if(ForeignPopup()) { Detail = "已停止；请手动关闭额外确认或菜单，不代确认其他窗口。"; return; }
        if(Visible(RetainerInventory))
        {
            if(inventoryLease == null && response == Response.Inventory && !responseObserved && RetainerMatches)
            {
                var agent = AgentRetainer.Instance();
                if(agent != null && LeaseWindow(RetainerInventory, (nint)agent, 0, ref inventoryLease, openedAfter))
                { shopOwner = (nint)agent->ShopEventHandler; Observe(Response.Inventory); }
            }
            if(InventoryOwned()) CloseInventory();
            else Detail = "当前仓库归属不符；请手动结束雇员交互。";
            return;
        }
        if(response == Response.Inventory && !responseObserved) { Detail = "等待已发送的仓库打开响应后关闭，不发新的出售。"; return; }
        if(Visible("SelectString")) { QuitRetainer(); return; }
        var list = Addon("RetainerList");
        if(Visible(list))
        {
            if(!responseObserved && response is not (Response.BellList or Response.ReturnList)) return;
            if(closeListSent) return;
            var agent = AgentRetainerList.Instance();
            if(agent == null) return;
            if(listLease is { } old && !Matches(list, old))
            {
                if(response != Response.ReturnList || responseObserved) { Detail = "原列表已被替换，请手动关闭当前列表。"; return; }
                listLease = null;
            }
            if(!LeaseWindow(list, (nint)agent, 0, ref listLease, openedAfter)) return;
            Observe(Response.BellList, Response.ReturnList);
            var opener = (nint)agent->OpenerEventInterface;
            if(opener == 0) return;
            if(response == Response.ReturnList) conversationOwner = opener;
            if(conversationOwner != 0 && conversationOwner != opener) { Detail = "列表会话已改变，请手动结束交互。"; return; }
            if(!Gate("close-bell", delays.CloseMs)) return;
            closeListSent = true; requestPending = false; step = Step.Closing; Sent(); Callback(list, -1); return;
        }
        if(Visible("Talk")) { AdvanceTalk(); return; }
        if(!UiReleased()) { stableAt = 0; return; }
        if(!responseObserved) { Detail = "后续出售已停止，仍等待已发请求的响应；不能把无窗口当作已撤销。"; return; }
        if(stableAt == 0) { stableAt = Now; return; }
        if(Now - stableAt < 2_000) return;
        ownBell = requestPending = false; Finish();
    }

    private void Finish()
    {
        State = ending; pendingAction = null; stopping = false;
        if(State == VendorSessionState.Completed) Detail = $"本角色出售完成：{SoldItems} 个，角色金币增加 {Gil}；雇员交互已释放。";
        else if(State == VendorSessionState.Stopped) Detail = "出售已停止，自己的雇员交互已释放。";
    }
    private void Fail(string text)
    {
        stopping = true; pendingAction = null; nextActionAt = 0;
        ending = State = VendorSessionState.Failed; Detail = text;
    }
    private bool Gate(string action, int delay)
    {
        if(disposed || CheckSupport() != null || !SessionMatches || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return false;
        if(!stopping && CheckConflict?.Invoke() is { } conflict) { Fail(conflict); return false; }
        if(pendingAction != action)
        {
            pendingAction = action; nextActionAt = Math.Max(Now + Math.Max(100, delay), lastActionAt + 100);
            deadline += nextActionAt - Now;
        }
        if(Now < nextActionAt || Now - lastActionAt < 100 || lastActionFrame == framework.LastUpdateUTC.Ticks) return false;
        pendingAction = null; lastActionAt = Now; lastActionFrame = framework.LastUpdateUTC.Ticks; return true;
    }
    private void Sent() { deadline = Now + delays.TimeoutSeconds * 1000L; stableAt = 0; }
    private void Expect(Response expected) { response = expected; responseObserved = false; requestPending = true; }
    private void Observe(params Response[] expected)
    { if(expected.Contains(response)) { responseObserved = true; requestPending = false; } }
    private void Invoke(System.Action action) { nativeCallback = true; try { action(); } finally { nativeCallback = false; } }
    private void Callback(AtkUnitBase* addon, int value)
    { var values = stackalloc AtkValue[1]; values[0] = Int(value); Invoke(() => addon->FireCallback(1, values, true)); }
    private static AtkValue Int(int value) => new() { Type = AtkValueType.Int, Int = value };

    private bool LeaseWindow(AtkUnitBase* addon, nint receiver, ulong kind, ref Lease? lease, long after)
    {
        if(addon == null || !addon->IsReady || !addon->IsVisible) return false;
        if(lease is { } existing)
        { if(!Matches(addon, existing)) { Fail("本服务的交互窗口被替换，请手动结束交互。"); return false; } return true; }
        var pointer = (nint)addon;
        if(!generations.TryGetValue(pointer, out var generation) || generation <= after) return false;
        if(!ReadReceiver(addon, out var actual, out var actualKind)) return false;
        if(actual != receiver || actualKind != kind) { Fail("窗口原生接收者或事件种类不符。"); return false; }
        lease = new(pointer, generation, receiver, kind); return true;
    }
    private bool LeaseConversation(AtkUnitBase* addon, ref Lease? lease)
    {
        if(!RetainerMatches || !ownBell || !SessionMatches || !ReadReceiver(addon, out var receiver, out var kind)) return false;
        if(kind != 0) { Fail("雇员菜单事件种类不符。"); return false; }
        return LeaseWindow(addon, receiver, 0, ref lease, openedAfter);
    }
    private bool Matches(AtkUnitBase* addon, Lease lease) => addon != null && (nint)addon == lease.Pointer
        && generations.GetValueOrDefault(lease.Pointer) == lease.Generation
        && ReadReceiver(addon, out var receiver, out var kind) && receiver == lease.Receiver && kind == lease.Kind;
    private static bool ReadReceiver(AtkUnitBase* addon, out nint receiver, out ulong kind)
    {
        receiver = 0; kind = 0; var module = RaptureAtkModule.Instance();
        if(addon == null || module == null || !module->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false)) return false;
        receiver = (nint)entry.AgentInterface; kind = entry.EventKind; return receiver != 0;
    }
    private bool InventoryOwned() => RetainerMatches && inventoryLease is { } lease && Visible(RetainerInventory)
        && Matches(RetainerInventory, lease) && AgentRetainer.Instance()->IsAgentActive();

    private Dictionary<(int, int), BagSlot> ReadBags()
    {
        var result = new Dictionary<(int, int), BagSlot>(); var manager = InventoryManager.Instance();
        if(manager == null) throw new InvalidOperationException("Inventory manager unavailable.");
        for(var id = 0; id < 4; id++)
        {
            var container = manager->GetInventoryContainer((InventoryType)id);
            if(container == null || !container->IsLoaded || container->Items == null || container->Size != 35)
                throw new InvalidOperationException("Player bags not fully loaded.");
            for(var i = 0; i < container->Size; i++)
            {
                var item = container->Items + i;
                if(item->IsSymbolic || item->Quantity < 0) throw new InvalidOperationException("Unexpected symbolic or negative bag slot.");
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var row = item->ItemId == 0 ? null : data.GetExcelSheet<Item>().GetRowOrDefault(item->ItemId);
                var materiaCount = 0; ulong materia = 0, grades = 0;
                for(var m = 0; m < 5; m++)
                {
                    if(item->Materia[m] != 0) materiaCount++;
                    if(m < 4) materia |= (ulong)item->Materia[m] << (m * 16);
                    grades |= (ulong)item->MateriaGrades[m] << (m * 8);
                }
                var validPrice = VendorNativeRules.TryUnitPrice(row?.PriceLow ?? 0, hq, materiaCount, out var price);
                var sellable = row.HasValue && validPrice && item->Quantity > 0 && item->Quantity <= row.Value.StackSize
                    && (item->Flags & (InventoryItem.ItemFlags.Collectable | InventoryItem.ItemFlags.Relic)) == 0;
                var value = new VendorItem(id, i, item->ItemId, hq, item->Quantity, row?.ItemUICategory.RowId ?? 0, price, sellable);
                result[(id, i)] = new(value, (byte)item->Flags, item->SpiritbondOrCollectability, item->Condition, item->GlamourId,
                    materia, item->Materia[4], grades, (ushort)(item->Stains[0] | item->Stains[1] << 8));
            }
        }
        return result;
    }
    private static bool BagsEqual(Dictionary<(int, int), BagSlot> a, Dictionary<(int, int), BagSlot> b)
        => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var y) && x.Value == y);
    private static bool OtherSlotsEqual(Dictionary<(int, int), BagSlot> a, Dictionary<(int, int), BagSlot> b, VendorItem current)
        => a.Count == b.Count && a.All(x => x.Key == (current.Container, current.Slot) || (b.TryGetValue(x.Key, out var y) && x.Value == y));
    private string Text(uint row) => data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText().Trim() ?? "";
    private static string ReadText(AtkValue value) => value.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString
        ? SeString.Parse(value.String).TextValue.Trim() : "";
    private static bool ReadBool(AtkValue value) => value.Type == AtkValueType.Bool && value.Byte != 0;
    private int FindMenuEntry(AddonSelectString* addon, uint row)
    {
        var expected = Text(row); var found = -1; var menu = addon->PopupMenu.PopupMenu;
        if(expected.Length == 0 || menu.EntryCount is < 1 or > 32) return -1;
        for(var i = 0; i < menu.EntryCount; i++)
        {
            if(SeString.Parse(menu.EntryNames[i].Value).TextValue.Trim() != expected) continue;
            if(found != -1) return -1; found = i;
        }
        return found;
    }
    private bool ForeignPopup() => new[] { "SelectYesno", "InputNumeric", "ContextMenu", "Trade", "RetainerSell", "RetainerSellList" }.Any(Visible);
    private bool UiReleased() => !condition[ConditionFlag.OccupiedSummoningBell] && !condition[ConditionFlag.Crafting]
        && !condition[ConditionFlag.PreparingToCraft] && !condition[ConditionFlag.OccupiedInEvent]
        && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.LoggingOut]
        && !new[] { "RetainerList", "SelectString", "InputNumeric", "SelectYesno", "ContextMenu", "Talk", "Synthesis", "SynthesisSimple", "Trade", "RetainerSell", "RetainerSellList" }.Any(Visible)
        && !Visible(RetainerInventory);

    private bool AdvanceTalk()
    {
        var talk = Addon("Talk");
        if(!Visible(talk)) return false;
        if(!RetainerMatches || !ownBell || retainerName.Length == 0 || !talk->IsReady) return true;
        var speaker = ((AddonTalk*)talk)->AtkTextNode220;
        if(speaker == null || SeString.Parse(speaker->NodeText.StringPtr).TextValue.Trim() != retainerName)
        { Detail = "当前说话者不属于本次雇员，请手动结束交互。"; return true; }
        var pointer = (nint)talk;
        if(!generations.TryGetValue(pointer, out var generation) || generation <= openedAfter) return true;
        var target = talk->WindowCollisionNode;
        if(target == null || !target->AtkResNode.IsVisible()) return true;
        if(!Gate("advance-talk", delays.TalkMs)) return true;
        if(!RetainerMatches || generations.GetValueOrDefault(pointer) != generation || SeString.Parse(speaker->NodeText.StringPtr).TextValue.Trim() != retainerName) return true;
        var ev = new AtkEvent { Node = (AtkResNode*)target, Target = (AtkEventTarget*)target,
            Listener = (AtkEventListener*)talk, State = new AtkEventState { EventType = AtkEventType.MouseClick } };
        var eventData = new AtkEventData(); nativeCallback = true;
        try { talk->ReceiveEvent(AtkEventType.MouseClick, 0, &ev, &eventData); }
        finally { nativeCallback = false; }
        return true;
    }
    private void OnLifecycle(AddonEvent type, AddonArgs args)
    {
        var pointer = (nint)args.GetAddonPointer<AtkUnitBase>();
        if(type == AddonEvent.PostSetup || (type == AddonEvent.PostRefresh && args.AddonName == "Talk")) generations[pointer] = ++serial;
        else if(type == AddonEvent.PreFinalize)
        {
            if(confirmationLease is { } lease && lease.Pointer == pointer && generations.GetValueOrDefault(pointer) == lease.Generation)
                confirmationClosed = true;
            generations.Remove(pointer);
        }
        if(!Blocking || nativeCallback) return;
        if(type == AddonEvent.PostSetup && (args.AddonName is "InputNumeric" or "ContextMenu" or "Trade" || args.AddonName == "SelectYesno" && !salePending))
            Fail("出售期间出现了非本次操作的交互窗口，已撤销后续出售。");
    }
    public void Dispose()
    {
        if(disposed) return;
        var manual = Blocking; disposed = true; stopping = true; pendingAction = null;
        lifecycle.UnregisterListener(OnLifecycle);
        if(manual) log.Warning("Vendor sweep unloaded. No further sale or UI actions will run. Manually close owned dialogs and verify any already submitted sale; no retry is authorized.");
    }
}
