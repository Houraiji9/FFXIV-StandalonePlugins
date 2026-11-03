using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace RetainerCraftBridge;

// Owns only its bell interaction and withdrawals. No Artisan/AutoRetainer internals or mutable IPC are used.
internal sealed unsafe class MaterialRestockService : IMaterialPreparation
{
    private const string SupportedVersion = "2026.10.06.0000.0000";
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, long> generations = [];
    private readonly HashSet<ulong> visited = [];
    private Dictionary<uint, long> required = [];
    private Dictionary<MaterialKey, long> balance = [];
    private Dictionary<MaterialKey, long>? transferBefore;
    private MaterialDelays active = new();
    private bool disposed, stopping, ownBell, requestPending, transferPending, numericSubmitted;
    private bool closeInventorySent, closeListSent, quitRetainerSent;
    private MaterialResponse response;
    private bool responseObserved = true;
    private bool nativeCallback;
    private long serial, openedAfter, deadline, nextActionAt, lastActionAt = long.MinValue / 2, lastActionFrame, stableAt;
    private string? pendingAction;
    private ulong cid, retainerId;
    private nint conversationOwner;
    private string retainerName = "";
    private uint bellEntity;
    private nint bellAddress;
    private Lease? listLease, menuLease, inventoryLease, numericLease, talkLease;
    private Source source;
    private int amount;
    private Step step;
    private MaterialPreparationState ending;
    private readonly record struct Lease(nint Pointer, long Generation, nint Receiver, ulong Kind);
    private readonly record struct Source(int Container, int Slot, MaterialKey Key, int Quantity);
    private enum Step { Bell, List, Menu, Inventory, Scan, Quantity, Transfer, ReturnMenu, ReturnList, Closing }

    public MaterialDelays Delays { get; set; } = new();
    public MaterialPreparationState State { get; private set; }
    public bool Blocking => ownBell || requestPending || transferPending || State is MaterialPreparationState.Running or MaterialPreparationState.Cancelling;
    public string Detail { get; private set; } = "尚未开始补料。";

    public MaterialRestockService(IFramework framework, IPlayerState player, IObjectTable objects, ITargetManager targets,
        ICondition condition, IGameGui gameGui, IDataManager data, IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.framework = framework; this.player = player; this.objects = objects; this.targets = targets;
        this.condition = condition; this.gameGui = gameGui; this.data = data; this.lifecycle = lifecycle; this.log = log;
        lifecycle.RegisterListener(AddonEvent.PostSetup, OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, OnLifecycle);
        // Talk is a persistent addon; a new line is delivered through Refresh, not necessarily Setup.
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnLifecycle);
    }

    private static long Now => Environment.TickCount64;
    private bool VersionSupported => data.GameData.Repositories.TryGetValue("ffxiv", out var repository)
        && string.Equals(repository.Version?.Trim(), SupportedVersion, StringComparison.Ordinal)
        && sizeof(InventoryManager) == 0x3730 && sizeof(AgentRetainer) == 0x68D0 && sizeof(RetainerManager) == 0x310;
    private bool SessionMatches => player.IsLoaded && player.ContentId == cid && objects.LocalPlayer != null;
    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
    private static bool Visible(AtkUnitBase* addon) => addon != null && addon->IsVisible;
    private bool Visible(string name) => Visible(Addon(name));
    private AtkUnitBase* RetainerInventory
    {
        get
        {
            var agent = AgentRetainer.Instance();
            var manager = RaptureAtkUnitManager.Instance();
            return agent == null || manager == null || agent->AddonId == 0 ? null : manager->GetAddonById((ushort)agent->AddonId);
        }
    }
    private bool RetainerMatches => RetainerManager.Instance() is var r && r != null && retainerId != 0
        && r->LastSelectedRetainerId == retainerId && condition[ConditionFlag.OccupiedSummoningBell];

    public void Begin(ushort recipe, int count, ulong character)
    {
        if(disposed || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("补料必须在有效的游戏框架线程启动。");
        if(Blocking) throw new InvalidOperationException("上次补料或其窗口尚未清理。");
        if(!VersionSupported) throw new InvalidOperationException($"自动取料仅适配客户端 {SupportedVersion}。");
        cid = character;
        if(cid == 0 || !SessionMatches) throw new InvalidOperationException("补料角色与当前角色不符。");
        if(!UiReleased()) throw new InvalidOperationException("请先结束当前交互，再开始补料。");
        var row = data.GetExcelSheet<Recipe>().GetRowOrDefault(recipe)
            ?? throw new InvalidOperationException("补料配方不存在。");
        var ingredients = new List<MaterialIngredient>();
        for(var i = 0; i < row.Ingredient.Count; i++)
            ingredients.Add(new(row.Ingredient[i].RowId, row.AmountIngredient[i]));
        required = MaterialRules.Requirements(ingredients, count);
        active = (Delays ?? new()).Freeze();
        balance = ReadPlayerInventory(out _);
        visited.Clear(); retainerId = 0; conversationOwner = 0;
        listLease = menuLease = inventoryLease = numericLease = talkLease = null;
        stopping = ownBell = requestPending = transferPending = numericSubmitted = false;
        closeInventorySent = closeListSent = quitRetainerSent = false;
        response = MaterialResponse.None; responseObserved = true;
        transferBefore = null; pendingAction = null; stableAt = 0;
        ending = MaterialPreparationState.Completed;
        State = MaterialPreparationState.Running;
        step = Step.Bell; deadline = Now + active.TimeoutSeconds * 1000L;
        if(MaterialRules.Missing(required, balance).Count == 0)
        { State = MaterialPreparationState.Completed; Detail = "本时段所需材料已在背包中，无需取料。"; return; }
        foreach(var pair in required.Where(x => MaterialRules.Crystal(x.Key)))
            if(pair.Value > StackLimit(pair.Key)) { Fail($"晶体 #{pair.Key} 需要 {pair.Value} 个，超过角色可持有上限；不会降低制作次数。"); return; }
        Detail = "准备从雇员取出本时段全部缺少材料。";
    }

    public void Cancel()
    {
        // Revoke intent before any native/service access. Repeated calls preserve pending response evidence.
        stopping = true; pendingAction = null; nextActionAt = 0;
        if(State == MaterialPreparationState.Failed) ending = MaterialPreparationState.Failed;
        else ending = MaterialPreparationState.Cancelled;
        if(!Blocking) { State = ending; Detail = "补料已停止。"; return; }
        if(State != MaterialPreparationState.Failed) State = MaterialPreparationState.Cancelling;
        Detail = "已撤销后续取料，等待本次取出结果并关闭自己打开的界面。";
        deadline = Now + active.TimeoutSeconds * 1000L;
    }

    public void Tick()
    {
        if(disposed || !Blocking || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return;
        try
        {
            if(!VersionSupported) { stopping = true; State = MaterialPreparationState.Failed; Detail = "客户端版本不再匹配；已停止全部原生操作，请手动关闭雇员界面。"; return; }
            if(MaterialRules.CleanupDecision(response, responseObserved, false, false, transferPending, SessionMatches)
                == MaterialCleanupDecision.SessionInvalidated)
            {
                stopping = true; State = MaterialPreparationState.Failed; pendingAction = null;
                ownBell = requestPending = transferPending = false;
                Detail = "补料角色已退出或改变，旧会话失效；没有操作新角色。"; return;
            }
            if(Now >= deadline)
            {
                if(!stopping) Fail("补料响应超时，后续操作已撤销。");
                else Detail = "补料已停止，仍等待自己的交互结束。请手动关闭数量窗、雇员仓库及雇员列表；结果不明时不会继续制作或收取。";
            }
            if(stopping) { Cleanup(); return; }
            if(Visible("SelectYesno") || Visible("ContextMenu")) { Fail("取料期间出现其他菜单或确认窗；不会代为确认。"); return; }
            if(step != Step.Bell && !ownBell) { Fail("雇员铃交互所有权丢失。"); return; }
            if(transferPending) { ObserveTransfer(); return; }
            if(Visible("InputNumeric") && step != Step.Quantity)
            {
                if(numericSubmitted && numericLease is { } submitted && Matches(Addon("InputNumeric"), submitted))
                { Detail = "取出已核实，等待已提交的数量窗口消失。"; return; }
                Fail("取料阶段出现了非本次数量窗口，拒绝继续。"); return;
            }
            if(!BalancesEqual(balance, ReadPlayerInventory(out _))) { Fail("取料步骤之间背包发生非预期变化，已停止。"); return; }
            if(step is not (Step.Bell or Step.List or Step.ReturnList or Step.Closing) && !(step == Step.Menu && requestPending) && !RetainerMatches)
            { Fail("当前雇员身份变化，拒绝继续取料或关闭其他雇员界面。"); return; }
            switch(step)
            {
                case Step.Bell: OpenBell(); break;
                case Step.List: SelectRetainer(); break;
                case Step.Menu: OpenInventory(); break;
                case Step.Inventory: WaitInventory(); break;
                case Step.Scan: FindAndWithdraw(); break;
                case Step.Quantity: SubmitQuantity(false); break;
                case Step.ReturnMenu: QuitRetainer(); break;
                case Step.ReturnList: WaitReturnList(); break;
                case Step.Closing: Cleanup(); break;
            }
        }
        catch(Exception ex)
        {
            log.Error(ex, "Material preparation stopped; no retry is authorized.");
            Fail($"补料检查失败（{ex.GetType().Name}），请核对雇员界面与材料数量。");
        }
    }

    private void OpenBell()
    {
        if(!UiReleased()) { Fail("开始补料前出现了其他交互。"); return; }
        var bellName = data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ExtractText();
        var local = objects.LocalPlayer!;
        var bell = objects.Where(x => x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject)
            .Where(x => x.IsTargetable && string.Equals(x.Name.TextValue, bellName, StringComparison.Ordinal))
            .OrderBy(x => Vector3.Distance(local.Position, x.Position)).FirstOrDefault();
        // The ordinary bell interaction radius is at least 4.6 m in the inspected Artisan path.
        if(bell == null || Vector3.Distance(local.Position, bell.Position) >= 4.6f)
        { Fail("附近没有可交互的雇员铃，请先移动到铃旁；不会自动导航。"); return; }
        if(!Gate("bell", active.BellMs)) return;
        bellAddress = bell.Address; bellEntity = bell.EntityId;
        ownBell = true; ExpectResponse(MaterialResponse.BellList); openedAfter = serial;
        targets.Target = bell;
        step = Step.List; Sent();
        TargetSystem.Instance()->InteractWithObject((NativeObject*)bell.Address, false);
        Detail = "已交互雇员铃，等待本次雇员列表。";
    }

    private void SelectRetainer()
    {
        var addon = Addon("RetainerList");
        if(!Visible(addon)) return;
        var agent = AgentRetainerList.Instance();
        if(agent == null || !condition[ConditionFlag.OccupiedSummoningBell]) return;
        if(!LeaseWindow(addon, (nint)agent, 0, ref listLease, openedAfter)) return;
        ObserveResponse(MaterialResponse.BellList, MaterialResponse.ReturnList);
        if(conversationOwner == 0) conversationOwner = (nint)agent->OpenerEventInterface;
        if(conversationOwner == 0 || (nint)agent->OpenerEventInterface != conversationOwner)
        { Fail("雇员列表的会话接收者变化，拒绝继续。"); return; }
        if(MaterialRules.Missing(required, balance).Count == 0) { stopping = true; step = Step.Closing; return; }
        var manager = RetainerManager.Instance();
        if(manager == null || !manager->IsReady) return;
        var chosen = -1;
        ulong selectedId = 0; string selectedName = "";
        // Both the native sorted row and rendered list must identify the same retainer.
        for(uint i = 0; i < Math.Min(10, (int)agent->RetainerCount); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if(retainer == null || retainer->RetainerId == 0 || visited.Contains(retainer->RetainerId) || !retainer->Available) continue;
            var name = retainer->NameString;
            var index = (int)i;
            if(addon->AtkValuesCount <= 11 + index * 10 || !ReadBool(addon->AtkValues[11 + index * 10])) continue;
            if(ReadText(addon->AtkValues[3 + index * 10]) != name || agent->Retainers[index].Name.ToString() != name) continue;
            chosen = index; selectedId = retainer->RetainerId; selectedName = name; break;
        }
        if(chosen < 0)
        { Fail("全部可用雇员检查后材料仍不足：" + MissingText()); return; }
        if(!Gate("select-retainer", active.SelectRetainerMs)) return;
        retainerId = selectedId; retainerName = selectedName; visited.Add(selectedId);
        quitRetainerSent = closeInventorySent = false;
        openedAfter = serial; menuLease = talkLease = null; ExpectResponse(MaterialResponse.RetainerMenu);
        step = Step.Menu; Sent();
        var values = stackalloc AtkValue[4];
        values[0] = Int(2); values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)chosen };
        values[2] = default; values[3] = default;
        Invoke(() => addon->FireCallback(4, values, true));
        Detail = $"检查第 {visited.Count} 名雇员的实际库存。";
    }

    private void OpenInventory()
    {
        if(!RetainerMatches) return;
        var menu = Addon("SelectString");
        if(!Visible(menu)) { AdvanceTalk(); return; }
        if(!menu->IsReady) return;
        var entry = FindMenuEntry((AddonSelectString*)menu, 2378);
        if(entry < 0) { Fail("本次雇员菜单没有唯一的“物品管理”选项。"); return; }
        if(!LeaseConversation(menu, ref menuLease)) return;
        ObserveResponse(MaterialResponse.RetainerMenu);
        if(!Gate("open-inventory", active.OpenInventoryMs)) return;
        openedAfter = serial; inventoryLease = null; ExpectResponse(MaterialResponse.Inventory);
        step = Step.Inventory; Sent();
        Callback(menu, entry);
    }

    private void WaitInventory()
    {
        if(AdvanceTalk()) return;
        var agent = AgentRetainer.Instance();
        var addon = RetainerInventory;
        if(agent == null || !agent->IsAgentActive() || !Visible(addon)) return;
        if(!LeaseWindow(addon, (nint)agent, 0, ref inventoryLease, openedAfter)) return;
        if(!RetainerInventoryLoaded()) return;
        ObserveResponse(MaterialResponse.Inventory); step = Step.Scan; Sent();
    }

    private void FindAndWithdraw()
    {
        if(!InventoryOwned()) { Fail("本次雇员仓库已关闭或被替换。"); return; }
        if(Visible("SelectString") || Visible("Talk") || Visible("RetainerList")) return;
        var missing = MaterialRules.Missing(required, balance);
        if(missing.Count == 0) { stopping = true; step = Step.Closing; return; }
        var manager = InventoryManager.Instance();
        var found = false;
        foreach(var type in RetainerContainers)
        {
            var container = manager->GetInventoryContainer((InventoryType)type);
            if(container == null || container->Items == null || !container->IsLoaded || container->Size < 0 || container->Size > 200) return;
            for(var i = 0; i < container->Size; i++)
            {
                var item = container->Items + i;
                if(item->IsSymbolic || item->Quantity <= 0 || !missing.TryGetValue(item->ItemId, out var deficit)) continue;
                var key = Key(item);
                var current = ReadPlayerInventory(out var ordinary);
                var capacity = MaterialRules.Capacity(key, StackLimit(key.ItemId), ordinary, current);
                if(capacity <= 0) { Fail($"背包或晶体容量不足，无法取出材料 #{key.ItemId}；请整理背包后重新开始。"); return; }
                source = new(type, i, key, item->Quantity);
                amount = checked((int)Math.Min(item->Quantity, Math.Min(deficit, capacity)));
                found = true; break;
            }
            if(found) break;
        }
        if(!found) { CloseInventory(); return; }
        if(!Gate("withdraw", active.WithdrawMs)) return;
        if(!SourceUnchanged() || !InventoryOwned()) { Fail("取出前源物品或仓库归属变化。"); return; }
        transferBefore = new(balance); numericLease = null; numericSubmitted = false;
        openedAfter = serial;
        if(source.Quantity > 1) ExpectResponse(MaterialResponse.Quantity);
        else { response = MaterialResponse.None; responseObserved = true; requestPending = true; }
        step = source.Quantity > 1 ? Step.Quantity : Step.Transfer;
        transferPending = source.Quantity == 1; Sent();
        // Published FFCS contract: callback 3 = Retrieve Quantity. The inspected native branch directly
        // withdraws a one-item source, otherwise opens AgentRetainer's numeric callback kind 1 / Addon 914.
        Invoke(() => AgentRetainer.Instance()->HandleCallback((uint)source.Slot, (InventoryType)source.Container, 0, 3));
        Detail = $"准备取出材料 #{source.Key.ItemId} × {amount}。";
    }

    private void SubmitQuantity(bool cancel)
    {
        var addon = Addon("InputNumeric");
        if(!Visible(addon)) return;
        if(response != MaterialResponse.Quantity && numericLease == null)
        { Detail = "当前数量窗口不是本服务请求的响应，请手动关闭。"; return; }
        if(numericSubmitted) { Detail = "数量响应已提交，等待原窗口关闭；不会重复提交。"; return; }
        var agent = AgentRetainer.Instance();
        if(agent == null || !RetainerMatches || !InventoryOwned()) { Fail("数量窗口期间雇员或仓库归属变化。"); return; }
        if(!LeaseWindow(addon, (nint)agent, 1, ref numericLease, openedAfter)) return;
        if(agent->SellItemInventoryType != (InventoryType)source.Container || agent->SellItemInventorySlot != source.Slot || !SourceUnchanged())
        { Fail("数量窗口不再对应冻结的雇员物品格。"); return; }
        if(addon->AtkValues == null || addon->AtkValuesCount < 7 || ReadText(addon->AtkValues[6]) != Text(914))
        { Fail("数量窗口不是本版本的雇员取出提示。"); return; }
        var min = ReadInt(addon->AtkValues[2]); var max = ReadInt(addon->AtkValues[3]);
        if(!cancel && (min < 0 || max < min || amount < Math.Max(1, min) || amount > max || amount > source.Quantity))
        { Fail("雇员数量窗口范围与计划不符。"); return; }
        ObserveResponse(MaterialResponse.Quantity);
        if(!Gate(cancel ? "cancel-quantity" : "submit-quantity", cancel ? active.CloseUiMs : active.SubmitQuantityMs)) return;
        requestPending = false; numericSubmitted = true;
        if(!cancel) { transferPending = true; step = Step.Transfer; }
        Sent(); Callback(addon, cancel ? -1 : amount);
    }

    private void ObserveTransfer()
    {
        if(!RetainerMatches || !InventoryOwned())
        { Fail("取出结果尚未核实，雇员或仓库已改变；请核对实际材料，不会自动重试。"); return; }
        var item = RawSource();
        var remaining = item == null || item->IsSymbolic ? -1 : item->Quantity;
        if(remaining > 0 && Key(item) != source.Key) remaining = -1;
        var after = ReadPlayerInventory(out _);
        var result = MaterialRules.Observe(transferBefore!, after, source.Key, amount, source.Quantity, remaining);
        if(result == MaterialTransferResult.Invalid) { Fail("本次取出的源格或背包增量异常，请手动核对；不会重试。"); return; }
        if(result != MaterialTransferResult.Complete) return;
        transferPending = requestPending = false; transferBefore = null;
        balance = after; step = Step.Scan; Sent();
        Detail = $"已核实材料 #{source.Key.ItemId} 增加 {amount} 个。";
    }

    private void CloseInventory()
    {
        if(closeInventorySent) { Detail = "仓库关闭请求已发送，等待原窗口消失；不会重复请求。"; return; }
        if(!InventoryOwned()) { Fail("无法确认关闭的是本次雇员仓库。"); return; }
        if(!Gate("close-inventory", active.CloseUiMs)) return;
        closeInventorySent = true;
        openedAfter = serial; menuLease = talkLease = null; ExpectResponse(MaterialResponse.ReturnMenu);
        step = Step.ReturnMenu; Sent();
        Invoke(() => AgentRetainer.Instance()->Hide());
    }

    private void QuitRetainer()
    {
        if(Visible(RetainerInventory)) return;
        var menu = Addon("SelectString");
        if(!Visible(menu)) { AdvanceTalk(); return; }
        if(!menu->IsReady) return;
        if(quitRetainerSent) { Detail = "雇员退出请求已发送，等待列表；不会重复选择。"; return; }
        var entry = FindMenuEntry((AddonSelectString*)menu, 2383);
        if(entry < 0) { Fail("雇员菜单没有唯一退出选项，请手动结束交互。"); return; }
        if(!LeaseConversation(menu, ref menuLease)) return;
        ObserveResponse(MaterialResponse.RetainerMenu, MaterialResponse.ReturnMenu);
        if(!Gate("quit-retainer", active.CloseUiMs)) return;
        quitRetainerSent = true;
        step = Step.ReturnList; openedAfter = serial; ExpectResponse(MaterialResponse.ReturnList); talkLease = null;
        Sent(); Callback(menu, entry);
    }

    private void WaitReturnList()
    {
        var addon = Addon("RetainerList");
        if(!Visible(addon)) { AdvanceTalk(); return; }
        var agent = AgentRetainerList.Instance();
        if(agent == null) return;
        if(listLease is { } old && !Matches(addon, old)) listLease = null;
        if(!LeaseWindow(addon, (nint)agent, 0, ref listLease, openedAfter)) return;
        // The script may allocate a new list-opening task after dismissing a retainer.
        // This list's fresh lease owns its current opener; opener pointers are not global identities.
        conversationOwner = (nint)agent->OpenerEventInterface;
        if(conversationOwner == 0) return;
        ObserveResponse(MaterialResponse.ReturnList); menuLease = inventoryLease = talkLease = null;
        retainerId = 0; step = Step.List; Sent();
    }

    private void Cleanup()
    {
        if(!ownBell)
        {
            if(!transferPending && !requestPending) Finish();
            return;
        }
        if(transferPending)
        {
            if(!InventoryOwned())
            {
                State = ending = MaterialPreparationState.Failed;
                Detail = "取出已发送但结果未能完整核实，停止后续操作且不恢复收取。请核对材料；退出当前角色可使旧会话失效。";
                return;
            }
            ObserveTransfer(); return;
        }
        if(Visible("SelectYesno") || Visible("ContextMenu"))
        { Detail = "已停止取料。请先手动关闭额外确认或菜单；不会代确认不属于本服务的窗口。"; return; }
        if(Visible("InputNumeric")) { SubmitQuantity(true); return; }
        // HandleCallback may have requested InputNumeric before its asynchronous Setup. Do not hide
        // the inventory or submit another withdrawal while that response is outstanding.
        if(response == MaterialResponse.Quantity && !responseObserved)
        {
            Detail = "已撤销后续取料，等待已发送请求的数量窗口后取消；窗口尚未出现，不能把静默当成撤销成功。";
            return;
        }
        if(Visible(RetainerInventory))
        {
            // Cancellation can arrive after the menu callback but before Inventory's Setup. Bind
            // only that already-requested fresh response, then close it without ever scanning items.
            if(inventoryLease == null && step == Step.Inventory && requestPending && RetainerMatches)
            {
                var agent = AgentRetainer.Instance();
                if(agent != null && LeaseWindow(RetainerInventory, (nint)agent, 0, ref inventoryLease, openedAfter))
                    ObserveResponse(MaterialResponse.Inventory);
            }
            if(InventoryOwned()) CloseInventory();
            else Detail = "已停止取料，当前仓库归属不符；请手动关闭雇员界面。";
            return;
        }
        if(response == MaterialResponse.Inventory && !responseObserved)
        { Detail = "仓库打开请求已经发送，等待原响应出现后关闭；不再发出新操作。"; return; }
        if(Visible("SelectString")) { QuitRetainer(); return; }
        var list = Addon("RetainerList");
        if(Visible(list))
        {
            if(!responseObserved && response is not (MaterialResponse.BellList or MaterialResponse.ReturnList))
            { Detail = "已发送的雇员请求仍待响应；旧列表不能证明请求已撤销。"; return; }
            if(closeListSent) { Detail = "雇员铃关闭请求已发送，等待交互释放；不会重复请求。"; return; }
            var agent = AgentRetainerList.Instance();
            if(agent == null) return;
            if(listLease is { } old && !Matches(list, old))
            {
                if(step != Step.ReturnList || !requestPending)
                { Detail = "原雇员列表已被替换；请手动关闭当前列表。"; return; }
                listLease = null;
            }
            if(!LeaseWindow(list, (nint)agent, 0, ref listLease, openedAfter)) return;
            ObserveResponse(MaterialResponse.BellList, MaterialResponse.ReturnList);
            var opener = (nint)agent->OpenerEventInterface;
            if(opener == 0) return;
            if(step == Step.ReturnList) conversationOwner = opener;
            if(conversationOwner != 0 && opener != conversationOwner)
            { Detail = "已停止取料，雇员列表会话变化；请手动关闭该列表。"; return; }
            if(!Gate("close-bell", active.CloseUiMs)) return;
            closeListSent = true;
            requestPending = false; step = Step.Closing; Sent(); Callback(list, -1); return;
        }
        if(Visible("Talk")) { AdvanceTalk(); return; }
        if(!UiReleased()) { stableAt = 0; return; }
        if(stableAt == 0) { stableAt = Now; return; }
        if(MaterialRules.CleanupDecision(response, responseObserved, true, Now - stableAt >= 2_000,
            transferPending, SessionMatches) != MaterialCleanupDecision.Released)
        {
            if(!responseObserved)
                Detail = "停止已生效：后续取料意图已撤销。已发游戏请求尚未观察到响应，保持等待且不恢复收取；退出角色可使旧会话失效。";
            return;
        }
        ownBell = requestPending = false;
        if(ending == MaterialPreparationState.Completed && MaterialRules.Missing(required, ReadPlayerInventory(out _)).Count != 0)
        { ending = MaterialPreparationState.Failed; Detail = "结束补料时材料数量不足，不启动制作。"; }
        Finish();
    }

    private void Finish()
    {
        State = ending; pendingAction = null; stopping = false;
        if(State == MaterialPreparationState.Completed) Detail = "本时段全部材料已到位，雇员交互已释放。";
        else if(State == MaterialPreparationState.Cancelled) Detail = "补料已取消，自己的雇员交互已释放。";
    }

    private void Fail(string detail)
    {
        stopping = true; pendingAction = null; nextActionAt = 0;
        ending = State = MaterialPreparationState.Failed; Detail = detail;
        log.Warning("Material preparation: {Detail}", detail);
    }

    private bool Gate(string action, int delay)
    {
        if(!VersionSupported || !SessionMatches || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading || disposed) return false;
        if(pendingAction != action)
        {
            pendingAction = action; nextActionAt = Math.Max(Now + Math.Max(100, delay), lastActionAt + 100);
            deadline += nextActionAt - Now; // Configured pauses do not consume the response timeout.
        }
        if(Now < nextActionAt || Now - lastActionAt < 100 || lastActionFrame == framework.LastUpdateUTC.Ticks) return false;
        pendingAction = null; lastActionAt = Now; lastActionFrame = framework.LastUpdateUTC.Ticks;
        return true;
    }

    private void Sent() { deadline = Now + active.TimeoutSeconds * 1000L; stableAt = 0; }
    private void ExpectResponse(MaterialResponse expected) { response = expected; responseObserved = false; requestPending = true; }
    private void ObserveResponse(params MaterialResponse[] expected)
    {
        if(!expected.Contains(response)) return;
        responseObserved = true; requestPending = false;
    }
    private void Invoke(System.Action action) { nativeCallback = true; try { action(); } finally { nativeCallback = false; } }
    private void Callback(AtkUnitBase* addon, int value)
    {
        var values = stackalloc AtkValue[1]; values[0] = Int(value);
        nativeCallback = true; try { addon->FireCallback(1, values, true); } finally { nativeCallback = false; }
    }
    private static AtkValue Int(int value) => new() { Type = AtkValueType.Int, Int = value };

    private bool LeaseWindow(AtkUnitBase* addon, nint receiver, ulong kind, ref Lease? lease, long after)
    {
        if(addon == null || !addon->IsReady || !addon->IsVisible) return false;
        var ptr = (nint)addon;
        if(lease is { } existing)
        {
            if(!Matches(addon, existing)) { Fail("本服务的交互窗口被替换，请手动结束当前界面。"); return false; }
            return true;
        }
        if(!generations.TryGetValue(ptr, out var generation) || generation <= after) return false;
        if(!ReadReceiver(addon, out var actualReceiver, out var actualKind)) return false;
        if(actualReceiver != receiver || actualKind != kind)
        { Fail("窗口回调接收者或事件种类与本次取料流程不符。"); return false; }
        lease = new(ptr, generation, receiver, kind); return true;
    }
    private bool LeaseConversation(AtkUnitBase* addon, ref Lease? lease)
    {
        if(!RetainerMatches || !ownBell || !SessionMatches) return false;
        // SelectString's native receiver is a dynamically allocated scene callback task. It is NOT
        // AgentRetainer or the RetainerList opener. Freeze the real receiver of the fresh menu created
        // after our action, then validate that exact pointer/kind/generation on every later frame.
        // The caller additionally requires one exact inventory/quit menu entry before any callback.
        if(!ReadReceiver(addon, out var receiver, out var kind)) return false;
        if(kind != 0) { Fail("雇员菜单的原生事件种类不符，请手动结束交互。"); return false; }
        return LeaseWindow(addon, receiver, 0, ref lease, openedAfter);
    }
    private bool Matches(AtkUnitBase* addon, Lease lease) => addon != null && (nint)addon == lease.Pointer
        && generations.GetValueOrDefault(lease.Pointer) == lease.Generation
        && ReadReceiver(addon, out var receiver, out var kind) && receiver == lease.Receiver && kind == lease.Kind;
    private static bool ReadReceiver(AtkUnitBase* addon, out nint receiver, out ulong kind)
    {
        receiver = 0; kind = 0;
        var module = RaptureAtkModule.Instance();
        if(addon == null || module == null || !module->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false)) return false;
        receiver = (nint)entry.AgentInterface; kind = entry.EventKind; return receiver != 0;
    }

    private bool InventoryOwned() => RetainerMatches && inventoryLease is { } lease && Visible(RetainerInventory)
        && Matches(RetainerInventory, lease) && AgentRetainer.Instance()->IsAgentActive();
    private bool SourceUnchanged() => RawSource() is var item && item != null && !item->IsSymbolic
        && item->Quantity == source.Quantity && Key(item) == source.Key;
    private InventoryItem* RawSource()
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer((InventoryType)source.Container);
        return container == null || container->Items == null || !container->IsLoaded || source.Slot < 0 || source.Slot >= container->Size
            ? null : container->Items + source.Slot;
    }
    private static readonly int[] RetainerContainers = [10000, 10001, 10002, 10003, 10004, 10005, 10006, 12001];
    private static bool RetainerInventoryLoaded()
    {
        var manager = InventoryManager.Instance();
        if(manager == null) return false;
        foreach(var id in RetainerContainers)
        {
            var container = manager->GetInventoryContainer((InventoryType)id);
            if(container == null || container->Items == null || !container->IsLoaded) return false;
        }
        return true;
    }
    private Dictionary<MaterialKey, long> ReadPlayerInventory(out List<MaterialBagSlot> ordinary)
    {
        ordinary = [];
        var all = new List<MaterialBagSlot>();
        var manager = InventoryManager.Instance();
        if(manager == null) throw new InvalidOperationException("Inventory manager unavailable.");
        foreach(var id in new[] { 0, 1, 2, 3, 2001 })
        {
            var container = manager->GetInventoryContainer((InventoryType)id);
            if(container == null || container->Items == null || !container->IsLoaded || container->Size < 0 || container->Size > 200)
                throw new InvalidOperationException("Player inventory is not loaded.");
            for(var i = 0; i < container->Size; i++)
            {
                var item = container->Items + i;
                if(item->IsSymbolic || item->Quantity < 0) throw new InvalidOperationException("Player inventory contains an unexpected symbolic slot.");
                var slot = new MaterialBagSlot(Key(item), item->Quantity);
                all.Add(slot); if(id != 2001) ordinary.Add(slot);
            }
        }
        return MaterialRules.Totals(all);
    }
    private static MaterialKey Key(InventoryItem* item) => new(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
    private int StackLimit(uint item) => checked((int)(data.GetExcelSheet<Item>().GetRowOrDefault(item)?.StackSize ?? 0));
    private static bool BalancesEqual(IReadOnlyDictionary<MaterialKey, long> before, IReadOnlyDictionary<MaterialKey, long> after)
        => before.Keys.Concat(after.Keys).Distinct().All(x => before.GetValueOrDefault(x) == after.GetValueOrDefault(x));
    private string MissingText() => string.Join("，", MaterialRules.Missing(required, balance).Select(x => $"#{x.Key} 缺 {x.Value}"));
    private string Text(uint row) => data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText().Trim() ?? "";
    private static string ReadText(AtkValue value) => value.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString
        ? SeString.Parse(value.String).TextValue.Trim() : "";
    private static bool ReadBool(AtkValue value) => value.Type == AtkValueType.Bool && value.Byte != 0;
    private static int ReadInt(AtkValue value) => value.Type switch
    { AtkValueType.Int => value.Int, AtkValueType.UInt when value.UInt <= int.MaxValue => (int)value.UInt, _ => -1 };
    private int FindMenuEntry(AddonSelectString* addon, uint textRow)
    {
        var expected = Text(textRow); var result = -1;
        var menu = addon->PopupMenu.PopupMenu;
        if(expected.Length == 0 || menu.EntryCount is < 1 or > 32) return -1;
        for(var i = 0; i < menu.EntryCount; i++)
        {
            var text = SeString.Parse(menu.EntryNames[i].Value).TextValue.Trim();
            if(text != expected) continue;
            if(result != -1) return -1;
            result = i;
        }
        return result;
    }
    private bool UiReleased() => !condition[ConditionFlag.OccupiedSummoningBell] && !condition[ConditionFlag.Crafting]
        && !condition[ConditionFlag.PreparingToCraft] && !condition[ConditionFlag.OccupiedInEvent]
        && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.LoggingOut]
        && !new[] { "RetainerList", "SelectString", "InputNumeric", "SelectYesno", "ContextMenu", "Talk", "Synthesis", "SynthesisSimple" }.Any(Visible)
        && !Visible(RetainerInventory);

    private bool AdvanceTalk()
    {
        var talk = Addon("Talk");
        if(!Visible(talk)) return false;
        if(!RetainerMatches || !ownBell || retainerName.Length == 0 || !talk->IsReady) return true;
        var native = (AddonTalk*)talk;
        var speaker = native->AtkTextNode220;
        if(speaker == null || SeString.Parse(speaker->NodeText.StringPtr).TextValue.Trim() != retainerName)
        { Detail = "已停止推进对话：当前说话者不是本次雇员，请手动结束交互。"; return true; }
        var pointer = (nint)talk;
        if(!generations.TryGetValue(pointer, out var generation) || generation <= openedAfter) return true;
        // Talk routes through the UI module's dialogue events, not AddonCallbackMapping. Its receiver
        // here is the visible addon itself; a persistent Talk line is identified by its refresh epoch.
        talkLease = new(pointer, generation, pointer, 0);
        var target = talk->WindowCollisionNode;
        if(target == null || !target->AtkResNode.IsVisible()) return true;
        if(!Gate("advance-talk", active.CloseUiMs)) return true;
        if(!RetainerMatches || generations.GetValueOrDefault(pointer) != generation
            || SeString.Parse(speaker->NodeText.StringPtr).TextValue.Trim() != retainerName) return true;
        // The inspected MouseClick handler ignores eventParam, checks an actual ResNode target and
        // normal left-mouse data, then either finishes text animation or advances this line.
        // No magic event target (such as 132), private offset, or synthetic menu choice is used.
        var ev = new AtkEvent { Node = (AtkResNode*)target, Target = (AtkEventTarget*)target,
            Listener = (AtkEventListener*)talk, State = new AtkEventState { EventType = AtkEventType.MouseClick } };
        var eventData = new AtkEventData();
        nativeCallback = true;
        try { talk->ReceiveEvent(AtkEventType.MouseClick, 0, &ev, &eventData); }
        finally { nativeCallback = false; }
        Detail = "推进本次雇员的对话，等待菜单或列表。";
        return true;
    }
    private void OnLifecycle(AddonEvent type, AddonArgs args)
    {
        var ptr = (nint)args.GetAddonPointer<AtkUnitBase>();
        if(type == AddonEvent.PostSetup || (type == AddonEvent.PostRefresh && args.AddonName == "Talk")) generations[ptr] = ++serial;
        else if(type == AddonEvent.PreFinalize) generations.Remove(ptr);
        if(!Blocking || nativeCallback) return;
        if(type == AddonEvent.PostSetup && args.AddonName is "SelectYesno" or "ContextMenu")
            Fail("补料期间出现了其他交互窗口；已撤销后续取料。");
        if(type == AddonEvent.PreFinalize && numericLease is { } lease && lease.Pointer == ptr && !numericSubmitted && !stopping)
            Fail("本次数量窗口被关闭，停止补料且不重试。");
    }

    public void Dispose()
    {
        if(disposed) return;
        var manual = Blocking;
        disposed = true; stopping = true; pendingAction = null;
        lifecycle.UnregisterListener(OnLifecycle);
        if(manual) log.Warning("Material preparation unloaded: no further native actions will run. Manually close remaining retainer dialogs and check any pending withdrawal.");
    }
}
