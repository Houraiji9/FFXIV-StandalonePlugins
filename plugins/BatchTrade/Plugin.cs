using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
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
    private readonly string? gameVersion;
    private bool Compatible => GameCompatibility.IsSupported(gameVersion, sizeof(InventoryManager));
    private volatile bool windowOpen;
    private volatile bool disposed;
    private int pendingUiActions, stopGeneration;
    private long nextTick;
    private string? dispatchError;

    private enum Stage { Idle, Starting, SendingRequest, Requesting, OpeningMenu, SelectingMenu, WaitingItem, Ready, AwaitFinal, Settlement, BetweenBatches }
    [Serializable]
    public sealed class Config : IPluginConfiguration
    {
        public int Version { get; set; } = 3;
        public uint ItemId;
        public bool HighQuality;
        public int TotalQuantity = 1;
        public int TimeoutSeconds = 90;
        public int SlotQuantityCap = 999;
        public int BatchQuantityCap = 999; // Legacy JSON field, read only while migrating versions before 3.
        public int StartupDelayMs = 1000;
        public int RequestDelayMs = 800;
        public int OpenMenuDelayMs = 500;
        public int SelectMenuDelayMs = 500;
        public int QuantityDelayMs = 800;
        public int CloseMenuDelayMs = 500;
        public int ProposeDelayMs = 800;
        public int FinalConfirmDelayMs = 1000;
        public int CancelDelayMs = 300;
        public int BetweenTradesMs = 1500;
    }
    private readonly record struct Identity(uint Entity, string Name, ushort World);
    private readonly record struct StartRequest(uint ItemId, bool HighQuality, int TotalQuantity, int TimeoutSeconds, int SlotQuantityCap, TradeDelays Delays, Identity? Target);
    private sealed record UiSnapshot(bool Running, bool Cancelling, bool FinalSubmitted, int Goal, int Delivered,
        string RecipientLabel, Identity? Target, uint[] ItemIds, IReadOnlyDictionary<ItemKey, long> Totals, string Status, string DelayLabel, int DelayRemainingMs, string LastDiagnostic);
    private UiSnapshot uiSnapshot = new(false, false, false, 0, 0, "", null, [], new Dictionary<ItemKey, long>(),
        "请选择物品、品质和总数量，然后选中接收玩家。", "", 0, "");
    private Config config = null!;
    private Stage stage;
    private ItemKey selected;
    private Identity recipient;
    private Identity sender;
    private int goal, delivered, filling, filled, batchQuantity, activeTimeoutSeconds;
    private readonly TradeClock clock = new();
    private int activeSlotCap, activeStackLimit;
    private TradeDelays activeDelays;
    private long updateFrame, tradeGeneration, menuGeneration, expectedMenuGeneration, numericMenuGeneration;
    private nint ownedTradePointer;
    private long ownedTradeGeneration;
    private bool ownedTradeClosed;
    private sealed record CancelRequest(nint TradePointer, long Generation, Identity Sender, Identity Recipient);
    private CancelRequest? pendingCancel;
    private bool creatingTradePopup;
    private long numericBornAt, confirmationBornAt;
    private nint numericReceiver, confirmationReceiver;
    private bool numericMappingBound, confirmationMappingBound;
    private bool completionMessage, numericSubmitted, finalSubmitted, settlementWindowStarted;
    private bool finalOfferVerified;
    private string settlementTrigger = "";
    private nint numericPointer, confirmationPointer, contextPointer;
    private ulong numericEventKind, confirmationEventKind;
    private long settlementDecrease;
    private uint initialGil;
    private List<PlannedSlot> batch = [];
    private Dictionary<ItemKey, long> baseline = [];
    private string lastDiagnostic = "";
    private bool diagnosticRecorded;
    private TradeOfferSnapshot? lastOfferView;
    private OfferCheck lastOfferCheck;
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
        try { gameVersion = data.GameData.Repositories.TryGetValue("ffxiv", out var repository) ? repository.Version?.Trim() : null; }
        catch { gameVersion = null; }
        config = pluginInterface.GetPluginConfig() as Config ?? new Config();
        config.TotalQuantity = Math.Clamp(config.TotalQuantity, 1, 10_000_000);
        config.TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 15, 600);
        var migratedSlotCap = TradeRules.MigrateSlotQuantityCap(config.Version, config.SlotQuantityCap, config.BatchQuantityCap);
        var saveMigratedConfig = config.Version < 3 || config.SlotQuantityCap != migratedSlotCap;
        config.SlotQuantityCap = migratedSlotCap;
        config.Version = 3;
        if (saveMigratedConfig) SaveConfig();
        config.StartupDelayMs = Math.Clamp(config.StartupDelayMs, 0, 600_000);
        config.BetweenTradesMs = Math.Clamp(config.BetweenTradesMs, 0, 600_000);
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
        lifecycle.RegisterListener(AddonEvent.PostSetup, "Trade", OnTradeLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "Trade", OnTradeLifecycle);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "ContextMenu", OnContextLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "ContextMenu", OnContextLifecycle);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            Stop("插件已卸载。", true);
            ExecutePendingCancel(); // Unloading never bypasses the configured delay or native send limiter.
            if (pendingCancel != null)
                log.Warning("BatchTrade unloaded before its safe cancellation time; manually cancel any remaining trade window.");
        }
        catch (Exception ex) { log.Error(ex, "BatchTrade unload could not cancel the remaining trade; manual cancellation may be needed."); }
        finally
        {
            pendingCancel = null; clock.CancelPending();
            commands.RemoveHandler("/batchtrade");
            pluginInterface.UiBuilder.Draw -= Draw;
            pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
            pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
            framework.Update -= Tick;
            chat.LogMessage -= OnLogMessage;
            lifecycle.UnregisterListener(OnNumericLifecycle);
            lifecycle.UnregisterListener(OnConfirmationLifecycle);
            lifecycle.UnregisterListener(OnTradeLifecycle);
            lifecycle.UnregisterListener(OnContextLifecycle);
        }
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
        var request = new StartRequest(config.ItemId, config.HighQuality, config.TotalQuantity, config.TimeoutSeconds, config.SlotQuantityCap,
            new TradeDelays(config.StartupDelayMs, config.RequestDelayMs, config.OpenMenuDelayMs, config.SelectMenuDelayMs,
                config.QuantityDelayMs, config.CloseMenuDelayMs, config.ProposeDelayMs,
                config.FinalConfirmDelayMs, config.CancelDelayMs, config.BetweenTradesMs).Normalize(), snapshot.Target);
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
        ImGui.SetNextWindowSize(new Vector2(660, 650), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 340), new Vector2(float.MaxValue, float.MaxValue));
        var open = windowOpen;
        if (ImGui.Begin("批量物品交易###BatchTrade", ref open)) ConfigUI();
        ImGui.End();
        windowOpen = open;
    }

    private void ConfigUI()
    {
        var snapshot = Volatile.Read(ref uiSnapshot);
        var pending = Volatile.Read(ref pendingUiActions) != 0 || snapshot.Cancelling;
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
        EditBoundedInt("每格数量上限（1–999）", ref config.SlotQuantityCap, 1, 999);
        ImGui.TextWrapped("每笔最多 5 格，按数量从大到小选择背包堆。每个源背包格每笔只使用一次；不会自动拆分背包堆叠。");
        if (ImGui.CollapsingHeader("各步骤延迟（毫秒）"))
        {
            EditBoundedInt("启动延迟", ref config.StartupDelayMs, 0, 600_000);
            EditBoundedInt("发起交易前", ref config.RequestDelayMs, 100, 30_000);
            EditBoundedInt("打开物品菜单前", ref config.OpenMenuDelayMs, 100, 30_000);
            EditBoundedInt("选择交易菜单项前", ref config.SelectMenuDelayMs, 100, 30_000);
            EditBoundedInt("提交物品数量前", ref config.QuantityDelayMs, 100, 30_000);
            EditBoundedInt("关闭自身菜单／下一格前", ref config.CloseMenuDelayMs, 100, 30_000);
            EditBoundedInt("提出交易条件前", ref config.ProposeDelayMs, 100, 30_000);
            EditBoundedInt("最终确认交易前", ref config.FinalConfirmDelayMs, 100, 30_000);
            EditBoundedInt("取消当前交易前", ref config.CancelDelayMs, 100, 30_000);
            EditBoundedInt("两笔交易间隔", ref config.BetweenTradesMs, 0, 600_000);
        }
        ImGui.TextWrapped("每个动作前独立等待，至少间隔 100 毫秒；这些延迟不占用服务器／对方响应超时。停止立即撤销后续动作，取消窗口也遵守发送间隔。");
        var target = snapshot.Target;
        ImGui.TextUnformatted(target is null ? "当前目标：请在游戏中选中一名玩家" : $"当前目标：{target.Value.Name}（世界 #{target.Value.World}）");
        var count = snapshot.Totals.GetValueOrDefault(new(config.ItemId, config.HighQuality));
        ImGui.TextUnformatted($"该品质背包可用数量：{count}");
        if (ImGui.Button("开始批量交易")) QueueStart(snapshot);
        ImGui.EndDisabled();
        if (snapshot.Running || pending && !snapshot.Cancelling)
        {
            ImGui.SameLine();
            var stopLabel = !snapshot.Running ? "取消等待中的开始操作" :
                snapshot.FinalSubmitted ? "停止后续交易（本笔结果核对中）" : "停止并取消当前交易";
            if (ImGui.Button(stopLabel)) QueueStop();
            if (snapshot.Running)
            {
                ImGui.TextUnformatted(snapshot.RecipientLabel);
                ImGui.ProgressBar(snapshot.Goal > 0 ? (float)snapshot.Delivered / snapshot.Goal : 0, new Vector2(-1, 0), $"已完成 {snapshot.Delivered} / {snapshot.Goal}");
            }
        }
        if (snapshot.DelayRemainingMs > 0) ImGui.TextUnformatted($"{snapshot.DelayLabel}：{snapshot.DelayRemainingMs / 1000.0:F1} 秒");
        if (snapshot.Cancelling) ImGui.TextUnformatted("已撤销后续操作，等待安全间隔后取消当前交易。");
        else if (pending) ImGui.TextUnformatted("正在等待游戏线程处理操作……");
        ImGui.TextWrapped(Volatile.Read(ref dispatchError) ?? snapshot.Status);
        if (!string.IsNullOrEmpty(snapshot.LastDiagnostic))
        {
            if (ImGui.Button("复制最后诊断")) ImGui.SetClipboardText(snapshot.LastDiagnostic);
            if (ImGui.CollapsingHeader("最后诊断（不含玩家身份）")) ImGui.TextWrapped(snapshot.LastDiagnostic);
        }
    }

    private void EditBoundedInt(string label, ref int value, int minimum, int maximum)
    {
        ImGui.InputInt(label, ref value);
        if (!ImGui.IsItemDeactivatedAfterEdit()) return;
        value = Math.Clamp(value, minimum, maximum);
        SaveConfig();
    }

    // Only the framework thread reads game state; Draw receives an immutable, atomically published view.
    private void RefreshUiSnapshot()
    {
        if (!Compatible)
        {
            status = $"当前客户端版本 {gameVersion ?? "未知"} 不受支持；本版仅适配 {GameCompatibility.RequiredVersion}，请更新插件后使用。";
            Volatile.Write(ref uiSnapshot, CreateSnapshot(null, [], new Dictionary<ItemKey, long>()));
            return;
        }
        var inventory = ReadInventory();
        var target = CurrentTarget();
        var ids = inventory.Select(x => x.Item.Id).Distinct().Order().ToArray();
        Volatile.Write(ref uiSnapshot, CreateSnapshot(target, ids, TradeRules.Totals(inventory)));
        Volatile.Write(ref dispatchError, null);
    }

    private UiSnapshot CreateSnapshot(Identity? target, uint[] ids, IReadOnlyDictionary<ItemKey, long> totals) =>
        new(Running, pendingCancel != null, stage == Stage.Settlement, goal, delivered,
            Running ? $"锁定接收方：{recipient.Name}（世界 #{recipient.World}）" : "", target, ids, totals, status,
            pendingCancel != null ? "取消交易倒计时" : stage == Stage.Starting ? "启动倒计时" :
                stage == Stage.BetweenBatches ? "下一笔交易倒计时" : StepName(clock.PendingAction),
            (Running && stage != Stage.Settlement || pendingCancel != null) ? clock.DelayRemaining(Now) : 0, lastDiagnostic);

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
        if (Running || pendingCancel != null || !Compatible) return;
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
        diagnosticRecorded = false; lastOfferView = null; lastDiagnostic = "";
        sender = Describe(local); recipient = target; goal = request.TotalQuantity; delivered = 0;
        activeTimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 15, 600);
        activeSlotCap = Math.Clamp(request.SlotQuantityCap, 1, 999);
        activeStackLimit = (int)Math.Clamp((long)item.StackSize, 1, int.MaxValue);
        activeDelays = request.Delays;
        initialGil = manager->GetGil();
        stage = Stage.Starting;
        clock.BeginDelay(Now, activeDelays.Startup);
        status = $"已锁定接收方，启动延迟后开始；每格最多 {activeSlotCap} 个，每笔最多 5 格。";
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
        batch = TradeRules.Plan(inventory, selected, goal - delivered, activeSlotCap, activeStackLimit);
        if (batch.Count == 0) { Stop("没有足够的可用物品，已停止。", true); return; }
        batchQuantity = batch.Sum(x => x.Quantity); filling = 0; filled = 0;
        completionMessage = finalSubmitted = numericSubmitted = settlementWindowStarted = finalOfferVerified = false;
        settlementTrigger = "";
        settlementDecrease = 0; confirmationPointer = 0;
        ownedTradePointer = 0; ownedTradeClosed = false;
        clock.BeginBatch(Now, activeTimeoutSeconds * 1000);
        stage = Stage.SendingRequest;
        status = $"准备发起交易，本笔 {batchQuantity} 个、{batch.Count} 格。";
    }

    private void Tick(IFramework _)
    {
        updateFrame++;
        if (disposed || Now < nextTick) return;
        if (!Compatible) { if (windowOpen) RefreshUiSnapshot(); return; }
        nextTick = Now + 100;
        try
        {
            if (pendingCancel != null) ExecutePendingCancel();
            else if (Running) Advance();
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
        if (clock.TimedOut(Now))
        {
            Stop(stage == Stage.Settlement ? "本笔结果核对超时，缺少完整完成证据；不会计入进度或自动重发，请核对实际结果。" :
                "等待超时，已停止。请核对背包后重新开始。", stage != Stage.Settlement);
            return;
        }
        var manager = InventoryManager.Instance();
        if (manager == null) { Stop("背包服务不可用，已停止。", false); return; }
        if (manager->GetGil() != initialGil) { Stop("金币发生变化，已停止。", true); return; }
        if (stage is Stage.Starting or Stage.BetweenBatches)
        {
            if (manager->TradeLocalState > TradeState.NotTrading || Visible(Trade) || Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
            { Stop("等待开始下一笔时出现了其他交易或交互，请核对后重新开始。", false); return; }
            if (!TradeRules.SameInventory(baseline, TradeRules.Totals(ReadInventory()), selected))
            { Stop("等待期间背包发生了变化，已停止。", false); return; }
            if (clock.CanAct(Now)) PrepareBatch();
            return;
        }
        if (stage == Stage.SendingRequest)
        {
            if (manager->TradeLocalState > TradeState.NotTrading || Visible(Trade) || Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
            { Stop("发起交易前出现了其他交互，已停止。", false); return; }
            if (!TradeRules.SameInventory(baseline, TradeRules.Totals(ReadInventory()), selected))
            { Stop("发起交易前背包发生了变化，已停止。", false); return; }
            if (!CanSend(TradeActionKind.Request, activeDelays.Request)) return;
            stage = Stage.Requesting;
            status = $"正在向 {recipient.Name} 发起交易，本笔 {batchQuantity} 个、{batch.Count} 格。";
            manager->SendTradeRequest(recipient.Entity);
            return;
        }
        if (stage == Stage.Settlement) { Settle(); return; }
        if (stage == Stage.Requesting)
        {
            if (!TradeRules.SameInventory(baseline, TradeRules.Totals(ReadInventory()), selected))
            { Stop("等待接受交易时背包发生了变化，已停止。", true); return; }
            if (!Visible(Trade)) return;
            if (manager->TradePartnerEntityId != recipient.Entity) { Stop("交易对象与锁定玩家不符，已停止。", false); return; }
            if (ownedTradePointer == 0) { ownedTradePointer = (nint)Trade; ownedTradeGeneration = tradeGeneration; }
            if ((nint)Trade != ownedTradePointer || ownedTradeGeneration != tradeGeneration)
            { Stop("原交易窗口已被替换，已停止；不会操作新窗口。", false); return; }
            if (!RequireOffers(0, "交易窗口已有未授权内容")) return;
            stage = Stage.OpeningMenu;
        }
        if (CanReconcileFinal() && (!Visible(Trade) || manager->TradeLocalState <= TradeState.NotTrading))
        { BeginReadOnlySettlement("OriginalTradeEnded"); Settle(); return; }
        if (ownedTradePointer != 0 && ((nint)Trade != ownedTradePointer || tradeGeneration != ownedTradeGeneration))
        { Stop("原交易窗口已被替换，已停止；不会操作新窗口。", false); return; }
        if (!Visible(Trade)) { Stop("交易已取消或窗口意外关闭；未计入进度，也不会自动重发。", false); return; }
        if (manager->TradePartnerEntityId != recipient.Entity) { Stop("交易对象发生变化，已停止。", false); return; }
        if (!RemoteEmpty()) { Stop("对方放入了物品或金币，已停止；本任务只支持单向赠送。", true); return; }
        var currentInventory = TradeRules.Totals(ReadInventory());
        if (!TradeRules.SameInventory(baseline, currentInventory, selected))
        {
            if (CanReconcileFinal() && TradeRules.ClassifySettlement(baseline, currentInventory, selected, batchQuantity) != InventoryChange.Unexpected)
            { BeginReadOnlySettlement("ExpectedInventoryBeforeSubmit"); Settle(); return; }
            Stop("交易完成信号前背包已变化，已停止。请核对实际结果。", true); return;
        }
        switch (stage)
        {
            case Stage.OpeningMenu: OpenItemMenu(); break;
            case Stage.SelectingMenu: SelectTradeOption(); break;
            case Stage.WaitingItem: WaitForItem(); break;
            case Stage.Ready:
                if (!RequireOffers(batch.Count, "本方交易物品或数量被修改")) return;
                if (manager->TradeIsSyncPending) return;
                if (Visible(SelectYesno) || Visible(InputNumeric) || Visible(ContextAddon))
                { Stop("出现了无关弹窗，已停止。", true); return; }
                if (manager->TradeLocalState != TradeState.SelectingTradeGoods) return;
                var confirmButton = Trade->GetComponentButtonById(33);
                if (confirmButton == null || confirmButton->OwnerNode == null) { Stop("当前交易界面缺少预确认按钮，已停止。", true); return; }
                if (!confirmButton->IsEnabled) return;
                if (!CanSend(TradeActionKind.Propose, activeDelays.Propose)) return;
                finalOfferVerified = true;
                stage = Stage.AwaitFinal; status = "物品已核对，等待对方确认。";
                confirmationPointer = 0;
                if (!ClickTradeButton(Trade, confirmButton))
                    Stop("未找到交易按钮唯一有效的本地点击事件，已停止。", true);
                break;
            case Stage.AwaitFinal:
                if (!RequireOffers(batch.Count, "确认期间交易内容被修改")) return;
                if (manager->TradeLocalState == TradeState.SelectingTradeGoods)
                { Stop("交易确认已被撤销，已停止。", true); return; }
                if (finalSubmitted || !Visible(SelectYesno)) return;
                if (manager->TradeIsSyncPending || manager->TradeLocalState != TradeState.WaitingForConfirmation) return;
                if ((nint)SelectYesno != confirmationPointer)
                { Stop("最终确认窗口实例被替换 [FinalWindowChanged]，已停止。", true); return; }
                var promptCheck = CheckFinalPrompt(SelectYesno);
                if (promptCheck == FinalPromptCheck.ValueUnavailable && Now - confirmationBornAt < 2_000) return;
                if (promptCheck != FinalPromptCheck.Ready)
                { Stop($"最终确认原生提示校验失败 [{promptCheck}]，已停止。", true); return; }
                if (!ConfirmationOwnerReady()) return;
                if (!CanSend(TradeActionKind.FinalConfirm, activeDelays.FinalConfirm)) return;
                finalSubmitted = true; stage = Stage.Settlement;
                settlementTrigger = "OwnFinalSubmission";
                SelectYesno->FireCallbackInt(0);
                if (stage == Stage.Settlement) status = "已确认本方交易，等待交易完成回执与库存核对。";
                break;
        }
    }

    private void OpenItemMenu()
    {
        if (!RequireOffers(filling, "填充前交易内容不匹配")) return;
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
        if (!CanSend(TradeActionKind.OpenMenu, activeDelays.OpenMenu)) return;
        stage = Stage.SelectingMenu;
        context->OpenForItemSlot((InventoryType)planned.Source.Container, planned.Source.Slot, 0, inventoryAgent->AddonId);
    }

    private void SelectTradeOption()
    {
        if (!RequireOffers(filling, "菜单期间交易内容变化")) return;
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
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)) continue;
            var label = value.String.ToString().Trim();
            if (label == AddonText(95) && !agent->IsContextItemDisabled(i))
            { if (index != -1) { Stop("存在多个交易菜单项，已停止。", true); return; } index = i; }
        }
        if (index < 0) { Stop("物品菜单没有可用的交易选项，已停止。", true); return; }
        if (!CanSend(TradeActionKind.SelectMenu, activeDelays.SelectMenu)) return;
        // Existing game's ContextMenu callback contract; no guessed native member function.
        var args = stackalloc AtkValue[5];
        args[0].Type = AtkValueType.Int; args[0].Int = 0;
        args[1].Type = AtkValueType.Int; args[1].Int = index;
        args[2].Type = AtkValueType.UInt; args[2].UInt = 0;
        args[3].Type = AtkValueType.Int; args[3].Int = 0;
        args[4].Type = AtkValueType.Int; args[4].Int = 0;
        numericSubmitted = false; numericPointer = 0; confirmationPointer = 0;
        contextPointer = (nint)ContextAddon;
        expectedMenuGeneration = menuGeneration;
        numericMappingBound = confirmationMappingBound = false;
        numericReceiver = confirmationReceiver = 0;
        stage = Stage.WaitingItem;
        // PostSetup can run inside this native callback before OpenAddon has registered its receiver.
        creatingTradePopup = true;
        try { ContextAddon->FireCallback(5, args); }
        finally { creatingTradePopup = false; }
    }

    private void WaitForItem()
    {
        if (Visible(SelectYesno))
        { Stop("放入物品时出现了额外确认窗口，已停止；本插件不代确认此提示。", true); return; }
        var planned = batch[filling];
        var check = InspectOffers(filling, true);
        if (check.Failed) { StopOffer("待填交易格或此前已填物品格被修改", check); return; }
        // The temporary quantity is 1 even when the planned amount is also 1. A source stack >1
        // must complete this operation's own numeric callback before that quantity can count as filled.
        if (planned.Source.Quantity > 1 && !numericSubmitted)
        {
            // A visible popup alone is insufficient: the raw slot must already bind the expected source.
            if (check.Code != OfferCheckCode.QuantityReady || !Visible(InputNumeric)) return;
            if ((nint)InputNumeric != numericPointer)
            { Stop("数量窗口被替换：不是本次菜单操作记录的实例，已停止。", true); return; }
            SubmitQuantity(planned.Quantity);
            return;
        }
        if (Visible(InputNumeric)) return;
        if (check.Code != OfferCheckCode.Ready) return;
        var closeMenu = Visible(ContextAddon);
        if (closeMenu && ((nint)ContextAddon != contextPointer || menuGeneration != expectedMenuGeneration))
        { Stop("物品菜单被替换，已停止。", true); return; }
        // Consume this step even when the game's menu closes itself during the configured delay.
        if (!CanSend(TradeActionKind.CloseMenu, activeDelays.CloseMenu)) return;
        filled += planned.Quantity; filling++;
        stage = filling == batch.Count ? Stage.Ready : Stage.OpeningMenu;
        if (closeMenu) ContextAddon->Close(false);
        if (Running) status = $"本笔已放入 {filled} / {batchQuantity} 个（{filling} / {batch.Count} 格）。";
    }

    private void SubmitQuantity(int amount)
    {
        var numeric = InputNumeric;
        var agent = AgentInventoryContext.Instance();
        var manager = InventoryManager.Instance();
        var planned = batch[filling];
        var source = manager->GetInventorySlot((InventoryType)planned.Source.Container, planned.Source.Slot);
        var sourceMatches = agent != null && agent->TargetInventoryId == (InventoryType)planned.Source.Container &&
            agent->TargetInventorySlotId == planned.Source.Slot && source != null && Key(source) == selected && source->Quantity == planned.Source.Quantity;
        var owner = ReadPopupCallback(numeric, out var receiver, out var kind);
        if (owner == PopupOwner.Missing && Now - numericBornAt < 2_000) return;
        if (!TryNumericRange(numeric, out var minimum, out var maximum))
        { Stop("数量窗口范围类型不匹配：需要有效的整数 min/max，已停止。", true); return; }
        var evidence = new NumericPopupState(numericMenuGeneration == expectedMenuGeneration && menuGeneration == expectedMenuGeneration,
            sourceMatches, Visible(Trade) && manager->TradePartnerEntityId == recipient.Entity && manager->TradeLocalState == TradeState.SelectingTradeGoods,
            owner, !numericMappingBound || receiver == numericReceiver && kind == numericEventKind,
            IsTradeQuantityPrompt(numeric), minimum, maximum, amount, planned.Source.Quantity, activeStackLimit, activeSlotCap);
        var result = NumericPopupRules.Check(evidence);
        if (result != NumericCheck.Ready)
        {
            var detail = result switch
            {
                NumericCheck.ActionNotBound => "菜单操作代次已经变化",
                NumericCheck.SourceChanged => "源物品格已经变化",
                NumericCheck.TradeChanged => "交易对象或交易状态已经变化",
                NumericCheck.OwnerMissing => "回调接收者在初始化等待后仍未登记",
                NumericCheck.OwnerUnexpected => "回调接收者或事件种类不属于此版本的交易数量处理器",
                NumericCheck.MappingChanged => "回调接收者或事件种类被替换",
                NumericCheck.PromptMismatch => "提示文本不是游戏提供的交易数量提示",
                _ => $"范围不匹配（最小 {minimum}，最大 {maximum}，计划 {amount}，物品堆叠 {activeStackLimit}）"
            };
            Stop($"数量窗口校验失败：{detail}，已停止。", true);
            return;
        }
        numericReceiver = receiver; numericEventKind = kind; numericMappingBound = true;
        if (!CanSend(TradeActionKind.Quantity, activeDelays.Quantity)) return;
        numericSubmitted = true;
        creatingTradePopup = true;
        try { numeric->FireCallbackInt(amount); }
        finally { creatingTradePopup = false; }
    }

    private void OnLogMessage(ILogMessage message)
    {
        // ILogMessage is callback-scoped; retain only this scalar, never the object.
        var id = message.LogMessageId;
        if (stage != Stage.Settlement && !CanReconcileFinal()) return;
        if (id is 10921 or 10922 or 10923) { Stop("收到非预期的金币或收取物品回执，已停止。", false); return; }
        // Local game LogMessage 10920 is an item-sent receipt, not an entire transaction-complete event.
        if (id != 10920) return;
        if (stage == Stage.AwaitFinal) BeginReadOnlySettlement("ItemReceiptBeforeSubmit");
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
        var ended = ownedTradeClosed || !Visible(Trade) || manager->TradeLocalState <= TradeState.NotTrading;
        if (!ended && (manager->TradePartnerEntityId != 0 && manager->TradePartnerEntityId != recipient.Entity || (nint)Trade != ownedTradePointer || tradeGeneration != ownedTradeGeneration))
        { Stop("结算期间出现了其他交易，已停止；不会关闭该窗口。", false); return; }
        // Reconciliation never sends. Native cleanup can clear working slots or partner before the other
        // completion signals; those fields no longer authorize anything here. Inventory/receipt/end must all agree.
        if (change != InventoryChange.Unchanged || ended) StartSettlementWindow();
        var result = CompletionGate.Evaluate(completionMessage, change, ended);
        if (result == CompletionResult.Invalid)
        { Stop("完成回执后的库存变化与本笔计划不一致，已停止，请核对实际结果。", false); return; }
        if (result != CompletionResult.Complete) return;
        delivered += batchQuantity; baseline = current;
        if (delivered == goal) { stage = Stage.Idle; status = $"批量交易已完成：{delivered} 个 {ItemName(selected.Id)} {(selected.HighQuality ? "HQ" : "NQ")}。"; return; }
        stage = Stage.BetweenBatches;
        clock.BeginDelay(Now, activeDelays.BetweenTrades);
        status = $"已完成 {delivered} / {goal} 个，准备下一笔。";
    }

    private void StartSettlementWindow()
    {
        if (settlementWindowStarted) return;
        settlementWindowStarted = true;
        clock.LimitSettlement(Now, 5_000);
    }

    private bool CanReconcileFinal() => TradeRules.CanReconcileFinal(stage == Stage.AwaitFinal,
        finalOfferVerified, ownedTradePointer != 0, filling, batch.Count, filled, batchQuantity);

    private void BeginReadOnlySettlement(string trigger)
    {
        if (!CanReconcileFinal()) return;
        stage = Stage.Settlement;
        settlementTrigger = trigger;
        settlementWindowStarted = true;
        // Revoke a queued final click and replace the response budget with a bounded, read-only reconciliation.
        clock.BeginBatch(Now, 5_000);
        status = "最终确认状态发生变化，正在只读核对本笔结果；不会继续发送或重试。";
    }

    private string AddonText(uint row) => data.GetExcelSheet<Addon>()?.GetRowOrDefault(row)?.Text.ExtractText().Trim() ?? string.Empty;
    private static string PlainText(byte* text) => text == null ? string.Empty : SeString.Parse(text).TextValue.Trim();
    private bool IsTradeQuantityPrompt(AtkUnitBase* addon) =>
        addon != null && addon->AtkValues != null && addon->AtkValuesCount > 6 &&
        addon->AtkValues[6].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString &&
        PlainText(addon->AtkValues[6].String) == AddonText(206);

    private FinalPromptCheck CheckFinalPrompt(AtkUnitBase* addon)
    {
        var available = addon != null && addon->AtkValues != null && addon->AtkValuesCount > 0;
        var supported = available && addon->AtkValues[0].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;
        // Verified native path builds 13 AtkValues, writes Addon102223 to String value[0], and passes it
        // to SelectYesno OpenAddon. Public StandardTypedAtkValues.PromptText describes this same slot.
        // The rendered PromptText node is diagnostic only, never a fallback for a mismatched native value.
        return FinalPromptRules.Check(!available ? null : supported ? PlainText(addon->AtkValues[0].String) : "",
            supported, AddonText(102223));
    }

    private static bool TryNumericRange(AtkUnitBase* addon, out int minimum, out int maximum)
    {
        minimum = maximum = -1;
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 4) return false;
        static int Read(AtkValue value) => value.Type switch
        {
            AtkValueType.UInt when value.UInt <= int.MaxValue => (int)value.UInt,
            AtkValueType.Int when value.Int >= 0 => value.Int,
            _ => -1
        };
        minimum = Read(addon->AtkValues[2]); maximum = Read(addon->AtkValues[3]);
        return minimum >= 0 && maximum >= minimum;
    }

    // PostSetup can precede registration in AddonCallbackMapping. Resolve on a later framework tick.
    // Only version-pinned InventoryManager embedded receivers are accepted; arbitrary agents are rejected.
    private static PopupOwner ReadPopupCallback(AtkUnitBase* addon, out nint receiver, out ulong eventKind)
    {
        receiver = 0; eventKind = 0;
        var module = RaptureAtkModule.Instance();
        if (addon == null || module == null || !module->AddonCallbackMapping.TryGetValue(addon->Id, out var entry, false)) return PopupOwner.Missing;
        receiver = (nint)entry.AgentInterface; eventKind = entry.EventKind;
        if (receiver == 0) return PopupOwner.Missing;
        var inventory = (nint)InventoryManager.Instance();
        if (GameCompatibility.IsQuantityReceiver(inventory, receiver, eventKind)) return PopupOwner.InventoryQuantity;
        return GameCompatibility.IsFinalReceiver(inventory, receiver, eventKind) ? PopupOwner.InventoryFinal : PopupOwner.Other;
    }

    private bool ConfirmationOwnerReady()
    {
        var owner = ReadPopupCallback(SelectYesno, out var receiver, out var kind);
        if (owner == PopupOwner.Missing && Now - confirmationBornAt < 2_000) return false;
        if (owner != PopupOwner.InventoryFinal)
        { Stop($"确认窗口归属不匹配（{owner}），已停止。", true); return false; }
        if (confirmationMappingBound && (receiver != confirmationReceiver || kind != confirmationEventKind))
        { Stop("确认窗口回调在等待期间被替换，已停止。", true); return false; }
        confirmationReceiver = receiver; confirmationEventKind = kind; confirmationMappingBound = true;
        return true;
    }

    private void OnConfirmationLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Running) return;
        var addon = args.GetAddonPointer<AtkUnitBase>();
        if (type == AddonEvent.PreFinalize)
        {
            if ((nint)addon == confirmationPointer &&
                stage == Stage.AwaitFinal && !finalSubmitted)
            {
                if (CanReconcileFinal()) BeginReadOnlySettlement("FinalPopupClosedBeforeSubmit");
                else Stop("交易确认窗口被取消，已停止。", true);
            }
            return;
        }
        var expected = stage == Stage.AwaitFinal ? 102223u : 0u;
        if (expected == 0 || confirmationPointer != 0)
        { Stop("确认窗口在非预期阶段出现或被替换，已停止。", true); return; }
        confirmationPointer = (nint)addon; confirmationBornAt = Now;
        confirmationMappingBound = false;
    }

    private void OnNumericLifecycle(AddonEvent type, AddonArgs args)
    {
        if (stage != Stage.WaitingItem) return;
        var addon = args.GetAddonPointer<AtkUnitBase>();
        if (type == AddonEvent.PreFinalize)
        {
            if ((nint)addon == numericPointer && !numericSubmitted)
                Stop("交易数量窗口被取消，已停止。", true);
            return;
        }
        // Loading may complete on a later frame, so the native callback's synchronous scope is not required.
        if (numericPointer != 0 || numericSubmitted || contextPointer == 0 || menuGeneration != expectedMenuGeneration)
        { Stop("数量窗口被替换或菜单操作代次已变化，已停止。", true); return; }
        numericPointer = (nint)addon; numericBornAt = Now;
        numericMenuGeneration = expectedMenuGeneration; numericMappingBound = false;
    }

    private void OnTradeLifecycle(AddonEvent type, AddonArgs args)
    {
        var previousGeneration = tradeGeneration++;
        if (!Running) return;
        if (type == AddonEvent.PostSetup && ownedTradePointer != 0)
        { Stop("原交易后出现了新的交易窗口，已停止；不会操作新窗口。", false); return; }
        if (type != AddonEvent.PreFinalize) return;
        if (ownedTradePointer != 0 && (nint)args.GetAddonPointer<AtkUnitBase>() == ownedTradePointer && ownedTradeGeneration == previousGeneration)
        {
            ownedTradeClosed = true;
            if (CanReconcileFinal()) BeginReadOnlySettlement("OriginalTradeFinalizedBeforeSubmit");
            else if (stage != Stage.Settlement) Stop("本次交易窗口被关闭，已停止。", false);
        }
        else if (stage == Stage.Requesting) Stop("正在等待的交易窗口已关闭，已停止。", false);
    }

    private void OnContextLifecycle(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup)
        {
            menuGeneration++;
            if (stage is Stage.WaitingItem or Stage.Ready or Stage.AwaitFinal)
                Stop("等待交易期间打开了新的物品菜单，已停止。", true);
            return;
        }
        if (stage == Stage.WaitingItem && !creatingTradePopup && args is AddonReceiveEventArgs receive &&
            (AtkEventType)receive.AtkEventType is AtkEventType.ListItemClick or AtkEventType.ButtonClick or AtkEventType.MouseClick)
            Stop("本次交易菜单又收到其他点击，已停止。", true);
    }

    private bool CanSend(TradeActionKind action, int delay)
    {
        clock.Schedule(action, Now, delay);
        return clock.TryDispatch(action, Now, updateFrame);
    }

    private static string StepName(TradeActionKind? step) => step switch
    {
        TradeActionKind.Request => "发起交易倒计时", TradeActionKind.OpenMenu => "打开物品菜单倒计时",
        TradeActionKind.SelectMenu => "选择交易菜单项倒计时", TradeActionKind.Quantity => "提交数量倒计时",
        TradeActionKind.CloseMenu => "关闭菜单倒计时",
        TradeActionKind.Propose => "提出交易条件倒计时", TradeActionKind.FinalConfirm => "最终确认倒计时",
        TradeActionKind.Cancel => "取消交易倒计时", _ => "操作倒计时"
    };

    private void Stop(string reason, bool cancelOwnTrade)
    {
        var previousStage = stage;
        if (Running && !diagnosticRecorded) RecordDiagnostic(reason);
        var wasRunning = Running; stage = Stage.Idle;
        if (!wasRunning && pendingCancel != null) return; // Repeated Stop never restarts the cancellation timer.
        clock.CancelPending();
        if (cancelOwnTrade && wasRunning && previousStage is not (Stage.Starting or Stage.SendingRequest or Stage.Settlement or Stage.BetweenBatches) &&
            ownedTradePointer != 0 && (nint)Trade == ownedTradePointer && ownedTradeGeneration == tradeGeneration &&
            InventoryManager.Instance() is var m && m != null && m->TradePartnerEntityId == recipient.Entity && Visible(Trade))
        {
            pendingCancel = new(ownedTradePointer, ownedTradeGeneration, sender, recipient);
            clock.Schedule(TradeActionKind.Cancel, Now, activeDelays.Cancel);
        }
        status = reason + (goal > 0 ? $" 已确认完成 {delivered} / {goal} 个。" : "");
    }

    private void ExecutePendingCancel()
    {
        if (pendingCancel is not { } pending) return;
        var manager = InventoryManager.Instance();
        if (framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread ||
            pending.Generation != tradeGeneration || (nint)Trade != pending.TradePointer || !Visible(Trade) || manager == null ||
            manager->TradePartnerEntityId != pending.Recipient.Entity || manager->TradeLocalState <= TradeState.NotTrading || manager->TradeLocalState >= TradeState.Confirmed ||
            objects.LocalPlayer is not IPlayerCharacter local || Describe(local) != pending.Sender ||
            objects.SearchByEntityId(pending.Recipient.Entity) is not IPlayerCharacter other || Describe(other) != pending.Recipient)
        {
            pendingCancel = null; clock.CancelPending();
            return;
        }
        if (!clock.TryDispatch(TradeActionKind.Cancel, Now, updateFrame)) return;
        pendingCancel = null; clock.CancelPending(); // Clear before the native callback can re-enter lifecycle handlers.
        CancelTrade(Trade);
        status += " 已发送取消请求。";
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
    private OfferCheck InspectOffers(int completed, bool pending = false)
    {
        lastOfferView = CaptureOffers();
        lastOfferCheck = TradeOfferRules.Check(lastOfferView, batch, completed, pending, numericSubmitted);
        return lastOfferCheck;
    }

    private bool RequireOffers(int completed, string reason)
    {
        var check = InspectOffers(completed);
        if (check.Failed) { StopOffer(reason, check); return false; }
        return check.Code == OfferCheckCode.Ready;
    }

    private void StopOffer(string reason, OfferCheck check, bool cancel = true)
    {
        RecordDiagnostic(check.Code.ToString(), check.Slot, lastOfferView);
        Stop($"{reason} [{check.Code}{(check.Slot >= 0 ? $" / 格 {check.Slot + 1}" : "")}]，已停止。", cancel);
    }

    private TradeOfferSnapshot CaptureOffers()
    {
        var manager = InventoryManager.Instance();
        var sources = batch.Select(x => ReadSource(x.Source.Container, x.Source.Slot)).ToArray();
        var working = new WorkingTradeSlot[5];
        var available = manager != null && ownedTradePointer != 0 && (nint)Trade == ownedTradePointer &&
            ownedTradeGeneration == tradeGeneration && Visible(Trade) && manager->TradePartnerEntityId == recipient.Entity &&
            manager->TradeLocalState > TradeState.NotTrading;
        var container = available ? manager->GetInventoryContainer(InventoryType.HandIn) : null;
        available = container != null && container->Items != null && container->Size >= 5;
        if (available)
        {
            for (var i = 0; i < 5; i++)
            {
                // GetInventorySlot resolves symbolic links and would return the bag source, losing trade quantity.
                // Read only these five public raw slots. HandIn slot 5 is NOT assumed to be a gil slot.
                var raw = container->Items + i;
                var linkedContainer = raw->IsSymbolic ? (int)raw->LinkedInventoryType : -1;
                var linkedSlot = raw->IsSymbolic ? (int)raw->LinkedItemSlot : -1;
                var identity = raw->IsSymbolic ? ReadSource(linkedContainer, linkedSlot).Item : Key(raw);
                working[i] = new(identity, raw->Quantity, raw->IsSymbolic, linkedContainer, linkedSlot);
            }
        }
        return new(available, working, sources, ReadOffer(true).ToArray(), ReadOffer(false).ToArray(),
            manager == null ? -1 : manager->TradeItemsLocal[5].Quantity,
            manager == null ? -1 : manager->TradeItemsRemote[5].Quantity,
            manager == null || manager->TradeIsSyncPending);
    }

    private static BagSlot ReadSource(int bag, int slot)
    {
        // Never follow an unvalidated symbolic link or invoke its getter. Only normal player bags are sources.
        if (bag is < 0 or > 3 || slot < 0) return new(bag, slot, default, -1);
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer((InventoryType)bag);
        if (container == null || container->Items == null || slot >= container->Size) return new(bag, slot, default, -1);
        var source = container->Items + slot;
        return source->IsSymbolic ? new(bag, slot, default, -1) : new(bag, slot, Key(source), source->Quantity);
    }

    private void RecordDiagnostic(string code, int slot = -1, TradeOfferSnapshot? view = null)
    {
        if (diagnosticRecorded) return;
        diagnosticRecorded = true;
        try
        {
            var canReadNative = !disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread && Compatible;
            view ??= canReadNative ? CaptureOffers() : lastOfferView;
            var manager = canReadNative ? InventoryManager.Instance() : null;
            // Deliberately exclude identities, entity IDs, pointers, inventory-wide contents and exception text.
            lastDiagnostic = JsonSerializer.Serialize(new
            {
                PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(), GameVersion = gameVersion ?? "unknown",
                Code = code, Slot = slot < 0 ? (int?)null : slot + 1, Stage = stage.ToString(), filling, filled, activeSlotCap,
                NativeSampleAvailable = canReadNative, numericSubmitted, NumericSeen = numericPointer != 0,
                NumericVisible = canReadNative ? (bool?)Visible(InputNumeric) : null,
                FinalSubmitted = finalSubmitted, FinalOfferVerified = finalOfferVerified, SettlementTrigger = settlementTrigger,
                ConfirmationSeen = confirmationPointer != 0,
                ConfirmationMappingBound = confirmationMappingBound,
                Confirmation = canReadNative ? ReadConfirmationDiagnostic() : null,
                NumericMappingBound = numericMappingBound, MenuGenerationMatches = menuGeneration == expectedMenuGeneration,
                OwnedWindowMatches = canReadNative ? (bool?)(ownedTradePointer != 0 && (nint)Trade == ownedTradePointer && ownedTradeGeneration == tradeGeneration) : null,
                PartnerMatches = manager == null ? (bool?)null : manager->TradePartnerEntityId == recipient.Entity,
                LocalState = manager == null ? "unavailable" : manager->TradeLocalState.ToString(),
                RemoteState = manager == null ? "unavailable" : manager->TradeRemoteState.ToString(),
                LastOfferCheck = lastOfferCheck.Code.ToString(), Plan = batch, Observation = view
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { lastDiagnostic = $"BatchTrade diagnostic capture failed: {ex.GetType().Name}; code={code}; stage={stage}"; }
        log.Warning("BatchTrade stopped with diagnostic:\n{Diagnostic}", lastDiagnostic);
    }

    private object ReadConfirmationDiagnostic()
    {
        var addon = SelectYesno;
        var valuesAvailable = addon != null && addon->AtkValues != null && addon->AtkValuesCount > 0;
        var valueType = valuesAvailable ? addon->AtkValues[0].Type : AtkValueType.Undefined;
        var valueTextAvailable = valuesAvailable && valueType is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString;
        var valueText = valueTextAvailable ? PlainText(addon->AtkValues[0].String) : "";
        var node = addon == null ? null : ((AddonSelectYesno*)addon)->PromptText;
        var nodeText = node == null ? "" : PlainText(node->NodeText.StringPtr);
        var expected = AddonText(102223);
        var owner = ReadPopupCallback(addon, out _, out var kind);
        // Unknown prompt contents can contain player names; record match/type/length only, never raw text.
        return new
        {
            Visible = Visible(addon), InstanceMatches = confirmationPointer != 0 && (nint)addon == confirmationPointer,
            Owner = owner.ToString(), EventKind = kind, ValuesAvailable = valuesAvailable,
            PromptValueType = valueType.ToString(), PromptValueLength = valueText.Length,
            PromptValueMatches102223 = valueTextAvailable && expected.Length > 0 && valueText == expected,
            PromptNodeAvailable = node != null, PromptNodeLength = nodeText.Length,
            PromptNodeMatches102223 = node != null && expected.Length > 0 && nodeText == expected,
            PromptValueAndNodeAgree = valueTextAvailable && node != null && valueText == nodeText,
            ExpectedPromptLength = expected.Length
        };
    }

    private static ItemKey Key(InventoryItem* item) => new(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
    private static List<BagSlot> ReadOffer(bool local)
    {
        var manager = InventoryManager.Instance(); var result = new List<BagSlot>(5);
        if (manager == null) return result;
        var offer = local ? manager->TradeItemsLocal : manager->TradeItemsRemote;
        fixed (InventoryItem* first = offer)
        {
            for (var i = 0; i < 5; i++)
            {
                var item = first + i;
                // The cache contains resolved source identity and a separately copied offered amount.
                // Source-link authority belongs only to raw HandIn.Items[i], not this value view.
                // Unexpected symbolic cache is not followed: positive quantity with unknown identity cannot match.
                var key = item->IsSymbolic ? default : Key(item);
                result.Add(new(0, i, key, item->Quantity));
            }
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
