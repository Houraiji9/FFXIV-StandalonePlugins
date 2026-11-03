using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace BatchTrade;

/// <summary>Independent sender-only Dalamud plugin. All automation starts from an explicit UI action.</summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IDataManager data;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private volatile bool windowOpen;
    private volatile bool disposed;
    private int pendingUiActions, stopGeneration;
    private long nextTick;
    private string? dispatchError;

    private enum Stage { Idle, Requesting, OpeningMenu, SelectingMenu, WaitingItem, Ready, AwaitFinal, Settlement, BetweenBatches }
    [Serializable]
    public sealed class Config : IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        public uint ItemId;
        public bool HighQuality;
        public int TotalQuantity = 1;
        public int TimeoutSeconds = 90;
    }
    private readonly record struct Identity(uint Entity, string Name, ushort World);
    private readonly record struct StartRequest(uint ItemId, bool HighQuality, int TotalQuantity, int TimeoutSeconds, Identity? Target);
    private sealed record UiSnapshot(bool Running, bool FinalSubmitted, int Goal, int Delivered,
        string RecipientLabel, Identity? Target, uint[] ItemIds, IReadOnlyDictionary<ItemKey, long> Totals, string Status);
    private UiSnapshot uiSnapshot = new(false, false, 0, 0, "", null, [], new Dictionary<ItemKey, long>(),
        "请选择物品、品质和总数量，然后选中接收玩家。");
    private Config config = null!;
    private Stage stage;
    private ItemKey selected;
    private Identity recipient;
    private Identity sender;
    private int goal, delivered, filling, filled, batchQuantity, activeTimeoutSeconds;
    private long deadline, nextAction;
    private bool completionMessage, numericSubmitted, finalSubmitted, settlementWindowStarted, hqWarningSubmitted;
    private nint numericPointer, confirmationPointer, contextPointer;
    private ulong numericEventKind, confirmationEventKind;
    private long settlementDecrease;
    private uint initialGil;
    private List<PlannedSlot> batch = [];
    private Dictionary<ItemKey, long> baseline = [];
    private string status = "请选择物品、品质和总数量，然后选中接收玩家。";
    private bool Running => stage != Stage.Idle;
    private static long Now => Environment.TickCount64;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IChatGui chat, IObjectTable objects, ITargetManager targets, IDataManager data,
        IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.chat = chat;
        this.objects = objects;
        this.targets = targets;
        this.data = data;
        this.lifecycle = lifecycle;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Config ?? new Config();
        config.TotalQuantity = Math.Clamp(config.TotalQuantity, 1, 10_000_000);
        config.TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 15, 600);
        commands.AddHandler("/batchtrade", new CommandInfo(OnCommand) { HelpMessage = "打开批量物品交易设置；/batchtrade stop 停止任务。" });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        framework.Update += Tick;
        chat.LogMessage += OnLogMessage;
        lifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnNumericLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "InputNumeric", OnNumericLifecycle);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnConfirmationLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnConfirmationLifecycle);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop("插件已卸载。", true);
        commands.RemoveHandler("/batchtrade");
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        framework.Update -= Tick;
        chat.LogMessage -= OnLogMessage;
        lifecycle.UnregisterListener(OnNumericLifecycle);
        lifecycle.UnregisterListener(OnConfirmationLifecycle);
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) QueueStop();
        OpenWindow();
    }

    private void OpenWindow()
    {
        if (disposed) return;
        windowOpen = true;
        QueueOnFramework(() => { }); // Publish a fresh snapshot on the game thread.
    }

    private void QueueStop()
    {
        // A stop also invalidates a Start that has been queued but has not executed yet.
        Interlocked.Increment(ref stopGeneration);
        QueueOnFramework(() => Stop("已手动停止。未确认的交易不计入进度。", true));
    }

    private void QueueStart(UiSnapshot snapshot)
    {
        var request = new StartRequest(config.ItemId, config.HighQuality, config.TotalQuantity, config.TimeoutSeconds, snapshot.Target);
        var generation = Volatile.Read(ref stopGeneration);
        QueueOnFramework(() =>
        {
            if (generation == Volatile.Read(ref stopGeneration)) Start(request);
        });
    }

    private void QueueOnFramework(System.Action action)
    {
        if (disposed) return;
        Interlocked.Increment(ref pendingUiActions);
        try
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    // The host may execute immediately while its framework is being destroyed.
                    if (disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
                    action();
                    RefreshUiSnapshot();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "BatchTrade UI action failed on the framework thread.");
                    if (!disposed)
                    {
                        Stop($"操作失败，已停止：{ex.GetType().Name}。请核对当前交易。", true);
                        PublishStateOnly();
                    }
                }
                finally { Interlocked.Decrement(ref pendingUiActions); }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref pendingUiActions);
            Volatile.Write(ref dispatchError, "操作未能提交到游戏线程，请重新打开界面。");
            log.Error(ex, "BatchTrade could not schedule a UI action.");
        }
    }

    private void SaveConfig() => pluginInterface.SavePluginConfig(config);

    private void Draw()
    {
        if (!windowOpen || disposed) return;
        ImGui.SetNextWindowSize(new Vector2(590, 470), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 340), new Vector2(float.MaxValue, float.MaxValue));
        var open = windowOpen;
        if (ImGui.Begin("批量物品交易###BatchTrade", ref open)) ConfigUI();
        ImGui.End();
        windowOpen = open;
    }

    private void ConfigUI()
    {
        var snapshot = Volatile.Read(ref uiSnapshot);
        var pending = Volatile.Read(ref pendingUiActions) != 0;
        ImGui.TextWrapped("只发送选中的物品；不接受对方放入的物品或金币。每笔最多 5 格，对方需逐笔手动确认。");
        ImGui.Separator();
        ImGui.BeginDisabled(snapshot.Running || pending);
        if (ImGui.Button("刷新背包物品")) QueueOnFramework(() => { });
        var selectedName = ItemName(config.ItemId);
        if (ImGui.BeginCombo("交易物品", config.ItemId == 0 ? "选择背包内物品" : selectedName))
        {
            foreach (var id in snapshot.ItemIds)
            {
                if (ImGui.Selectable($"{ItemName(id)}  (#{id})", id == config.ItemId))
                { config.ItemId = id; SaveConfig(); }
            }
            ImGui.EndCombo();
        }
        if (ImGui.Checkbox("只交易高品质 HQ（未勾选为 NQ）", ref config.HighQuality)) SaveConfig();
        ImGui.InputInt("交易总数量", ref config.TotalQuantity);
        if (ImGui.IsItemDeactivatedAfterEdit())
        { config.TotalQuantity = Math.Clamp(config.TotalQuantity, 1, 10_000_000); SaveConfig(); }
        ImGui.InputInt("每笔等待超时（秒）", ref config.TimeoutSeconds);
        if (ImGui.IsItemDeactivatedAfterEdit())
        { config.TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 15, 600); SaveConfig(); }
        var target = snapshot.Target;
        ImGui.TextUnformatted(target is null ? "当前目标：请在游戏中选中一名玩家" : $"当前目标：{target.Value.Name}（世界 #{target.Value.World}）");
        var count = snapshot.Totals.GetValueOrDefault(new(config.ItemId, config.HighQuality));
        ImGui.TextUnformatted($"该品质背包可用数量：{count}");
        if (ImGui.Button("开始批量交易")) QueueStart(snapshot);
        ImGui.EndDisabled();
        if (snapshot.Running || pending)
        {
            ImGui.SameLine();
            var stopLabel = !snapshot.Running ? "取消等待中的开始操作" :
                snapshot.FinalSubmitted ? "停止后续交易（本笔已提交）" : "停止并取消当前交易";
            if (ImGui.Button(stopLabel)) QueueStop();
            if (snapshot.Running)
            {
                ImGui.TextUnformatted(snapshot.RecipientLabel);
                ImGui.ProgressBar(snapshot.Goal > 0 ? (float)snapshot.Delivered / snapshot.Goal : 0, new Vector2(-1, 0), $"已完成 {snapshot.Delivered} / {snapshot.Goal}");
            }
        }
        if (pending) ImGui.TextUnformatted("正在等待游戏线程处理操作……");
        ImGui.TextWrapped(Volatile.Read(ref dispatchError) ?? snapshot.Status);
    }

    // Only the framework thread reads game state; Draw receives an immutable, atomically published view.
    private void RefreshUiSnapshot()
    {
        var inventory = ReadInventory();
        var target = CurrentTarget();
        var ids = inventory.Select(x => x.Item.Id).Distinct().Order().ToArray();
        Volatile.Write(ref uiSnapshot, CreateSnapshot(target, ids, TradeRules.Totals(inventory)));
        Volatile.Write(ref dispatchError, null);
    }

    private UiSnapshot CreateSnapshot(Identity? target, uint[] ids, IReadOnlyDictionary<ItemKey, long> totals) =>
        new(Running, stage == Stage.Settlement, goal, delivered,
            Running ? $"锁定接收方：{recipient.Name}（世界 #{recipient.World}）" : "", target, ids, totals, status);

    private void PublishStateOnly()
    {
        var previous = Volatile.Read(ref uiSnapshot);
        Volatile.Write(ref uiSnapshot, CreateSnapshot(previous.Target, previous.ItemIds, previous.Totals));
    }
    private string ItemName(uint id) => id == 0 ? "未选择" : data.GetExcelSheet<Item>()?.GetRowOrDefault(id)?.Name.ToString() ?? $"物品 #{id}";
    private static Identity Describe(IPlayerCharacter player) => new(player.EntityId, player.Name.TextValue, (ushort)player.HomeWorld.RowId);
    private Identity? CurrentTarget() => targets.Target is IPlayerCharacter player ? Describe(player) : null;

    private void Start(StartRequest request)
    {
        if (Running) return;
        if (objects.LocalPlayer is not IPlayerCharacter local || CurrentTarget() is not { } target)
        { status = "请先登录，并选中接收物品的玩家。"; return; }
        if (target != request.Target) { status = "当前目标与点击开始时显示的玩家不同，请核对后重新开始。"; return; }
        if (target == Describe(local)) { status = "不能向自己交易。"; return; }
        var manager = InventoryManager.Instance();
        if (manager == null || manager->TradeLocalState > TradeState.NotTrading || Visible(Trade) || Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
        { status = "请先结束已有交易并关闭确认、数量和右键菜单窗口。"; return; }
        if (request.ItemId == 0 || request.TotalQuantity <= 0 || data.GetExcelSheet<Item>()?.GetRowOrDefault(request.ItemId) is not { } item || item.IsUntradable)
        { status = "请选择可交易物品并填写大于 0 的总数量。"; return; }
        selected = new(request.ItemId, request.HighQuality);
        baseline = TradeRules.Totals(ReadInventory());
        if (baseline.GetValueOrDefault(selected) < request.TotalQuantity)
        { status = "该品质物品数量不足，请调整总数量或品质。"; return; }
        sender = Describe(local); recipient = target; goal = request.TotalQuantity; delivered = 0;
        activeTimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 15, 600);
        initialGil = manager->GetGil();
        PrepareBatch();
    }

    private bool CheckIdentity()
    {
        if (objects.LocalPlayer is not IPlayerCharacter local || Describe(local) != sender) return false;
        if (objects.SearchByEntityId(recipient.Entity) is not IPlayerCharacter other || Describe(other) != recipient) return false;
        return Vector3.DistanceSquared(local.Position, other.Position) < 16f;
    }

    private void PrepareBatch()
    {
        if (!CheckIdentity()) { Stop("接收方身份变化、离开范围或角色已切换，已停止。", true); return; }
        var manager = InventoryManager.Instance();
        if (manager == null || manager->TradeLocalState > TradeState.NotTrading || Visible(Trade) ||
            Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
        { Stop("下一笔开始前出现了其他交互或交易，已停止，不会重新发起。", false); return; }
        var inventory = ReadInventory();
        if (!TradeRules.SameInventory(baseline, TradeRules.Totals(inventory), selected))
        { Stop("背包在两笔交易之间发生了变化，已停止。", true); return; }
        batch = TradeRules.Plan(inventory, selected, goal - delivered);
        if (batch.Count == 0) { Stop("没有足够的可用物品，已停止。", true); return; }
        batchQuantity = batch.Sum(x => x.Quantity); filling = 0; filled = 0;
        completionMessage = finalSubmitted = numericSubmitted = settlementWindowStarted = false;
        settlementDecrease = 0; confirmationPointer = 0;
        deadline = Now + activeTimeoutSeconds * 1000L;
        stage = Stage.Requesting;
        status = $"正在向 {recipient.Name} 发起交易，本笔 {batchQuantity} 个、{batch.Count} 格。";
        manager->SendTradeRequest(recipient.Entity); // Once only; never resend after timeout.
    }

    private void Tick(IFramework _)
    {
        if (disposed || Now < nextTick) return;
        nextTick = Now + 100;
        try
        {
            if (Running && Now >= nextAction) Advance();
            if (windowOpen || Running) RefreshUiSnapshot();
        }
        catch (Exception ex)
        {
            log.Error(ex, "BatchTrade stopped after an unexpected error.");
            Stop($"交易检查失败，已停止：{ex.GetType().Name}。请检查当前交易窗口。", true);
            PublishStateOnly();
        }
    }

    private void Advance()
    {
        if (!CheckIdentity()) { Stop("目标丢失、距离过远、身份变化或已掉线，已停止。", true); return; }
        if (Now >= deadline) { Stop("等待超时，已停止。请核对背包后重新开始。", true); return; }
        var manager = InventoryManager.Instance();
        if (manager == null) { Stop("背包服务不可用，已停止。", false); return; }
        if (manager->GetGil() != initialGil) { Stop("金币发生变化，已停止。", true); return; }
        if (stage == Stage.BetweenBatches) { PrepareBatch(); return; }
        if (stage == Stage.Settlement) { Settle(); return; }
        if (stage == Stage.Requesting)
        {
            if (!Visible(Trade)) return;
            if (manager->TradePartnerEntityId != recipient.Entity) { Stop("交易对象与锁定玩家不符，已停止。", false); return; }
            if (!OffersMatch(0)) { Stop("交易窗口已有未授权内容，已停止。", true); return; }
            stage = Stage.OpeningMenu;
        }
        if (!Visible(Trade)) { Stop("交易已取消或窗口意外关闭；未计入进度，也不会自动重发。", false); return; }
        if (manager->TradePartnerEntityId != recipient.Entity) { Stop("交易对象发生变化，已停止。", false); return; }
        if (!RemoteEmpty()) { Stop("对方放入了物品或金币，已停止；本任务只支持单向赠送。", true); return; }
        if (!TradeRules.SameInventory(baseline, TradeRules.Totals(ReadInventory()), selected))
        { Stop("交易完成信号前背包已变化，已停止。请核对实际结果。", true); return; }
        switch (stage)
        {
            case Stage.OpeningMenu: OpenItemMenu(); break;
            case Stage.SelectingMenu: SelectTradeOption(); break;
            case Stage.WaitingItem: WaitForItem(); break;
            case Stage.Ready:
                if (!OffersMatch(batchQuantity)) { Stop("本方交易物品或数量被修改，已停止。", true); return; }
                if (manager->TradeIsSyncPending) return;
                if (Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
                { Stop("出现了无关弹窗，已停止。", true); return; }
                if (manager->TradeLocalState != TradeState.SelectingTradeGoods) return;
                var confirmButton = Trade->GetComponentButtonById(33);
                if (confirmButton == null || confirmButton->OwnerNode == null) { Stop("当前交易界面缺少预确认按钮，已停止。", true); return; }
                if (!confirmButton->IsEnabled) return;
                stage = Stage.AwaitFinal; status = "物品已核对，等待对方确认。"; nextAction = Now + 200;
                confirmationPointer = 0;
                if (!ClickTradeButton(Trade, confirmButton))
                    Stop("未找到交易按钮唯一有效的本地点击事件，已停止。", true);
                break;
            case Stage.AwaitFinal:
                if (!OffersMatch(batchQuantity)) { Stop("确认期间交易内容被修改，已停止。", true); return; }
                if (manager->TradeLocalState == TradeState.SelectingTradeGoods)
                { Stop("交易确认已被撤销，已停止。", true); return; }
                if (finalSubmitted || !Visible(SelectYesno)) return;
                if (manager->TradeIsSyncPending || manager->TradeLocalState != TradeState.WaitingForConfirmation) return;
                // Bind the newly observed popup to this request/phase and exact game-provided text.
                var finalPrompt = ((AddonSelectYesno*)SelectYesno)->PromptText;
                if ((nint)SelectYesno != confirmationPointer || finalPrompt == null || finalPrompt->NodeText.ToString() != AddonText(102223) ||
                    !TryGetTradeCallback(SelectYesno, out var finalKind) || finalKind != confirmationEventKind)
                { Stop("无法确认弹窗属于当前交易，已停止。请手动核对。", true); return; }
                finalSubmitted = true; stage = Stage.Settlement;
                SelectYesno->FireCallbackInt(0);
                status = "已确认本方交易，等待交易完成回执与库存核对。";
                break;
        }
    }

    private void OpenItemMenu()
    {
        if (!OffersMatch(filled)) { Stop("填充前交易内容不匹配，已停止。", true); return; }
        if (Visible(ContextAddon) || Visible(InputNumeric) || Visible(SelectYesno))
        { Stop("已有其他菜单或弹窗，已停止。", true); return; }
        var planned = batch[filling];
        var source = InventoryManager.Instance()->GetInventorySlot((InventoryType)planned.Source.Container, planned.Source.Slot);
        if (source == null || Key(source) != selected || source->Quantity != planned.Source.Quantity || source->SpiritbondOrCollectability != 0)
        { Stop("待交易的背包格变化或物品已绑定，已停止。", true); return; }
        for (byte i = 0; i < 5; i++)
            if (source->GetMateriaId(i) != 0) { Stop("本插件不自动交易已镶嵌魔晶石的装备，请手动处理。", true); return; }
        var context = AgentInventoryContext.Instance();
        var agents = AgentModule.Instance();
        var inventoryAgent = agents == null ? null : agents->GetAgentByInternalId(AgentId.Inventory);
        if (context == null || inventoryAgent == null)
        { Stop("背包物品菜单服务不可用，已停止。", true); return; }
        stage = Stage.SelectingMenu; nextAction = Now + 200;
        context->OpenForItemSlot((InventoryType)planned.Source.Container, planned.Source.Slot, 0, inventoryAgent->AddonId);
    }

    private void SelectTradeOption()
    {
        if (!OffersMatch(filled)) { Stop("菜单期间交易内容变化，已停止。", true); return; }
        if (!Visible(ContextAddon)) return;
        var agent = AgentInventoryContext.Instance();
        var planned = batch[filling];
        if (agent == null || agent->TargetInventoryId != (InventoryType)planned.Source.Container || agent->TargetInventorySlotId != planned.Source.Slot)
        { Stop("右键菜单不属于计划的物品格，已停止。", true); return; }
        var index = -1;
        for (var i = 0; i < Math.Min(agent->ContextItemCount, 32); i++)
        {
            var at = agent->ContexItemStartIndex + i;
            if (at < 0 || at >= agent->EventParams.Length) continue;
            var value = agent->EventParams[at];
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString)) continue;
            var label = value.String.ToString().Trim();
            if (label == AddonText(95) && !agent->IsContextItemDisabled(i))
            { if (index != -1) { Stop("存在多个交易菜单项，已停止。", true); return; } index = i; }
        }
        if (index < 0) { Stop("物品菜单没有可用的交易选项，已停止。", true); return; }
        // Existing game's ContextMenu callback contract; no guessed native member function.
        var args = stackalloc AtkValue[5];
        args[0].Type = AtkValueType.Int; args[0].Int = 0;
        args[1].Type = AtkValueType.Int; args[1].Int = index;
        args[2].Type = AtkValueType.UInt; args[2].UInt = 0;
        args[3].Type = AtkValueType.Int; args[3].Int = 0;
        args[4].Type = AtkValueType.Int; args[4].Int = 0;
        numericSubmitted = false; numericPointer = 0; confirmationPointer = 0; hqWarningSubmitted = false;
        contextPointer = (nint)ContextAddon;
        stage = Stage.WaitingItem; nextAction = Now + 200;
        ContextAddon->FireCallback(5, args);
    }

    private void WaitForItem()
    {
        var amount = batch[filling].Quantity;
        if (Visible(SelectYesno))
        {
            if (hqWarningSubmitted) return;
            if (!selected.HighQuality || (nint)SelectYesno != confirmationPointer ||
                !TradeConfirmationTextMatches(SelectYesno, 102434) || !TryGetTradeCallback(SelectYesno, out var hqKind) || hqKind != confirmationEventKind ||
                !(OffersMatch(filled) || OffersMatch(filled + amount)))
            { Stop("出现了非本任务的HQ交易提示，已停止。", true); return; }
            hqWarningSubmitted = true;
            SelectYesno->FireCallbackInt(0);
            nextAction = Now + 200; return;
        }
        if (OffersMatch(filled + amount))
        {
            filled += amount; filling++;
            if (Visible(ContextAddon))
            {
                if ((nint)ContextAddon != contextPointer) { Stop("物品菜单被替换，已停止。", true); return; }
                ContextAddon->Close(false);
            }
            stage = filling == batch.Count ? Stage.Ready : Stage.OpeningMenu;
            status = $"本笔已放入 {filled} / {batchQuantity} 个（{filling} / {batch.Count} 格）。";
            nextAction = Now + 250; return;
        }
        if (!OffersMatch(filled)) { Stop("放入的物品或数量与计划不符，已停止。", true); return; }
        if (!Visible(InputNumeric)) return;
        if ((nint)InputNumeric != numericPointer) { Stop("数量弹窗不是本次交易菜单创建的窗口，已停止。", true); return; }
        if (numericSubmitted) return;
        var numeric = InputNumeric;
        var agent = AgentInventoryContext.Instance();
        var planned = batch[filling];
        if (agent == null || agent->TargetInventoryId != (InventoryType)planned.Source.Container || agent->TargetInventorySlotId != planned.Source.Slot ||
            numeric->AtkValues == null || numeric->AtkValuesCount < 7 || numeric->AtkValues[2].Type != AtkValueType.UInt || numeric->AtkValues[3].Type != AtkValueType.UInt ||
            numeric->AtkValues[2].UInt > amount || numeric->AtkValues[3].UInt != planned.Source.Quantity || !IsTradeQuantityPrompt(numeric) ||
            !TryGetTradeCallback(numeric, out var currentKind) || currentKind != numericEventKind)
        { Stop("数量弹窗与当前物品格不匹配，已停止。", true); return; }
        numericPointer = (nint)numeric; numericSubmitted = true;
        numeric->FireCallbackInt(amount); nextAction = Now + 250;
    }

    private void OnLogMessage(ILogMessage message)
    {
        // ILogMessage is callback-scoped; retain only this scalar, never the object.
        var id = message.LogMessageId;
        if (stage != Stage.Settlement || !finalSubmitted) return;
        if (id is 10921 or 10922 or 10923) { Stop("收到非预期的金币或收取物品回执，已停止。", false); return; }
        // Local game LogMessage 10920 is an item-sent receipt, not an entire transaction-complete event.
        if (id != 10920) return;
        completionMessage = true;
        StartSettlementWindow();
        status = "收到送出物品回执，正在核对整笔库存与交易结束状态。";
    }

    private void Settle()
    {
        var current = TradeRules.Totals(ReadInventory());
        var change = TradeRules.ClassifySettlement(baseline, current, selected, batchQuantity);
        var decrease = baseline.GetValueOrDefault(selected) - current.GetValueOrDefault(selected);
        if (decrease < settlementDecrease) { Stop("结算期间库存变化被反向修改，已停止。", false); return; }
        settlementDecrease = decrease;
        var manager = InventoryManager.Instance();
        if (Visible(Trade) && manager->TradePartnerEntityId != recipient.Entity)
        { Stop("结算期间出现了其他交易，已停止；不会关闭该窗口。", false); return; }
        if (Visible(Trade) && !OffersMatch(batchQuantity) && change == InventoryChange.Unchanged)
        { Stop("结算前交易内容发生变化，已停止。", true); return; }
        var ended = !Visible(Trade) || manager->TradeLocalState <= TradeState.NotTrading;
        if (change != InventoryChange.Unchanged || ended) StartSettlementWindow();
        var result = CompletionGate.Evaluate(completionMessage, change, ended);
        if (result == CompletionResult.Invalid)
        { Stop("完成回执后的库存变化与本笔计划不一致，已停止，请核对实际结果。", false); return; }
        if (result != CompletionResult.Complete) return;
        delivered += batchQuantity; baseline = current;
        if (delivered == goal) { stage = Stage.Idle; status = $"批量交易已完成：{delivered} 个 {ItemName(selected.Id)} {(selected.HighQuality ? "HQ" : "NQ")}。"; return; }
        stage = Stage.BetweenBatches; nextAction = Now + 700; deadline = Now + 10_000;
        status = $"已完成 {delivered} / {goal} 个，准备下一笔。";
    }

    private void StartSettlementWindow()
    {
        if (settlementWindowStarted) return;
        settlementWindowStarted = true;
        deadline = Math.Min(deadline, Now + 5_000);
    }

    private string AddonText(uint row) => data.GetExcelSheet<Addon>()?.GetRowOrDefault(row)?.Text.ToString() ?? string.Empty;
    private bool IsTradeQuantityPrompt(AtkUnitBase* addon) =>
        addon != null && addon->AtkValues != null && addon->AtkValuesCount > 6 &&
        addon->AtkValues[6].Type is AtkValueType.String or AtkValueType.ManagedString &&
        addon->AtkValues[6].String.ToString() == AddonText(206);

    private bool TradeConfirmationTextMatches(AtkUnitBase* addon, uint row) => addon != null &&
        ((AddonSelectYesno*)addon)->PromptText != null && ((AddonSelectYesno*)addon)->PromptText->NodeText.ToString() == AddonText(row);

    // Read the game's actual callback receiver. A numeric/YesNo dialog owned by InventoryContext,
    // Shop or another agent must never be submitted even if it reuses the same prompt/pointer.
    // Source: pinned CN FFXIVClientStructs AtkModule.AddonCallbackMapping / AddonCallbackEntry.
    private static bool TryGetTradeCallback(AtkUnitBase* addon, out ulong eventKind)
    {
        eventKind = 0;
        var module = RaptureAtkModule.Instance();
        var agent = AgentTrade.Instance();
        if (addon == null || module == null || agent == null ||
            !module->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false) || entry.AgentInterface != (AgentInterface*)agent) return false;
        eventKind = entry.EventKind;
        return true;
    }

    private void OnConfirmationLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Running) return;
        var addon = args.GetAddonPointer<AtkUnitBase>();
        if (type == AddonEvent.PreFinalize)
        {
            if ((nint)addon == confirmationPointer &&
                (stage == Stage.AwaitFinal && !finalSubmitted || stage == Stage.WaitingItem && !hqWarningSubmitted))
                Stop("交易确认弹窗被取消，已停止。", true);
            return;
        }
        var expected = stage == Stage.AwaitFinal ? 102223u : stage == Stage.WaitingItem && selected.HighQuality ? 102434u : 0u;
        if (expected == 0 || confirmationPointer != 0 || !TradeConfirmationTextMatches(addon, expected) ||
            !TryGetTradeCallback(addon, out var kind))
        { Stop("出现了非预期或被替换的确认弹窗，已停止。", true); return; }
        confirmationPointer = (nint)addon;
        confirmationEventKind = kind;
    }

    private void OnNumericLifecycle(AddonEvent type, AddonArgs args)
    {
        if (stage != Stage.WaitingItem) return;
        var addon = args.GetAddonPointer<AtkUnitBase>();
        if (type == AddonEvent.PreFinalize)
        {
            if ((nint)addon == numericPointer && !numericSubmitted)
                Stop("交易数量弹窗被取消，已停止。", true);
            return;
        }
        if (numericPointer != 0 || numericSubmitted || !IsTradeQuantityPrompt(addon) || !TryGetTradeCallback(addon, out var kind))
        { Stop("出现了非预期或替换的数量弹窗，已停止。", true); return; }
        numericPointer = (nint)addon;
        numericEventKind = kind;
    }

    private void Stop(string reason, bool cancelOwnTrade)
    {
        var previousStage = stage;
        var wasRunning = Running; stage = Stage.Idle;
        // After final submission, an asynchronously appearing Trade may already be another request,
        // even when its recipient is the same. Never close/cancel windows during reconciliation.
        if (cancelOwnTrade && wasRunning && previousStage is not (Stage.Settlement or Stage.BetweenBatches) &&
            InventoryManager.Instance() is var m && m != null && m->TradePartnerEntityId == recipient.Entity && Visible(Trade))
            CancelTrade(Trade);
        status = reason + (goal > 0 ? $" 已确认完成 {delivered} / {goal} 个。" : "");
    }

    private static AtkUnitBase* GetAddon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : manager->GetAddonByName(name);
    }
    private static AtkUnitBase* Trade => GetAddon("Trade");
    private static AtkUnitBase* SelectYesno => GetAddon("SelectYesno");
    private static AtkUnitBase* InputNumeric => GetAddon("InputNumeric");
    private static AtkUnitBase* ContextAddon => GetAddon("ContextMenu");

    private static bool ClickTradeButton(AtkUnitBase* trade, AtkComponentButton* button)
    {
        if (trade == null || button == null || button->OwnerNode == null || !button->IsEnabled ||
            (button->OwnerNode->NodeFlags & NodeFlags.Visible) == 0) return false;
        // Use this button's real registered event, never a guessed Trade callback parameter.
        // FFCS AtkEventListener.ReceiveEvent is the public VFunc 2 with optional null event data.
        // The former Omen Click helper also passed null event data (confirmed with static IL).
        AtkEvent* candidate = null;
        var seen = new HashSet<nint>();
        var entry = button->OwnerNode->AtkEventManager.Event;
        for (var count = 0; entry != null; count++, entry = entry->NextEvent)
        {
            if (count >= 64 || !seen.Add((nint)entry)) return false;
            if (entry->State.EventType != AtkEventType.ButtonClick) continue;
            if (entry->Listener != (AtkEventListener*)trade ||
                (entry->State.StateFlags & AtkEventStateFlags.IsGlobalEvent) != 0 || candidate != null) return false;
            candidate = entry;
        }
        if (candidate == null) return false;
        var click = new AtkEvent
        {
            Node = candidate->Node,
            Target = candidate->Target,
            Listener = candidate->Listener,
            Param = candidate->Param,
            State = new AtkEventState { EventType = AtkEventType.ButtonClick }
        };
        click.Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)click.Param), &click);
        return true;
    }

    private static void CancelTrade(AtkUnitBase* addon)
    {
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int; values[0].Int = 1;
        values[1].Type = AtkValueType.Int; values[1].Int = 0;
        // Preserve the original Trade.Callback(1, 0) contract: its wrapper sets close = true.
        addon->FireCallback(2, values, true);
    }

    private static bool Visible(AtkUnitBase* addon) => addon != null && addon->IsVisible;
    private bool RemoteEmpty()
    {
        var manager = InventoryManager.Instance();
        return manager != null && TradeRules.EmptyRemote(ReadOffer(false), manager->TradeItemsRemote[5].Quantity);
    }
    private bool OffersMatch(int expected)
    {
        var manager = InventoryManager.Instance();
        return manager != null && RemoteEmpty() && TradeRules.ValidOffer(ReadOffer(true), selected, expected, manager->TradeItemsLocal[5].Quantity);
    }
    private static ItemKey Key(InventoryItem* item) => new(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
    private static List<BagSlot> ReadOffer(bool local)
    {
        var manager = InventoryManager.Instance(); var result = new List<BagSlot>(5);
        if (manager == null) return result;
        for (var i = 0; i < 5; i++)
        {
            var item = local ? manager->TradeItemsLocal[i] : manager->TradeItemsRemote[i];
            result.Add(new(0, i, new(item.ItemId, (item.Flags & InventoryItem.ItemFlags.HighQuality) != 0), item.Quantity));
        }
        return result;
    }
    private static List<BagSlot> ReadInventory()
    {
        var manager = InventoryManager.Instance(); var result = new List<BagSlot>();
        if (manager == null) return result;
        for (var bag = 0; bag < 4; bag++)
        {
            var container = manager->GetInventoryContainer((InventoryType)bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId != 0 && item->Quantity > 0)
                    result.Add(new(bag, i, Key(item), item->Quantity));
            }
        }
        return result;
    }
}
