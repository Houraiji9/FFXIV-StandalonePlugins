using System.Diagnostics;
using System.Globalization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace RetainerCraftBridge;

// Explicit Standard/Quick mode has no Artisan queue or configuration dependency.
// Standard skill selection is left to the user's already-enabled Artisan AutoMode.
internal sealed unsafe class NativeCraftService : ICraftSession
{
    private const string SupportedVersion = "2026.10.06.0000.0000";
    private const int QuickReceiverRva = 0x28B6760;
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly ICondition condition;
    private readonly IDataManager data;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly CraftClock clock = new();
    private readonly Dictionary<nint, long> generations = [];
    private readonly nint imageBase = Process.GetCurrentProcess().MainModule?.BaseAddress ?? 0;
    private long generation, frame, requestedAfter, settleAt;
    private bool disposed, stopping, failed, openSent, startSent, batchStarted, batchEnded, progressComplete, craftFailed;
    private bool quickCancelSent, closeSent, recipeReleased, batchReconciled, nqFillSent;
    private bool quickDialogRequested, dialogCloseSent, responseTimedOut;
    private int pendingHqIndex = -1, pendingHqAmount;
    private ushort recipeId;
    private uint productId, job;
    private ulong cid;
    private int yield, batchCount, batchAttempts, attemptsBefore;
    private long productBefore;
    private CraftMode mode;
    private CraftDelays active = new();
    private CraftIngredient[] ingredients = [];
    private CraftAssignment[] assignments = [];
    private Lease? recipeLease, dialogLease, synthesisLease;
    private Phase phase;
    private readonly record struct Lease(nint Pointer, long Generation);
    private enum Phase { Idle, Open, Materials, Start, QuickDialog, AwaitStart, Running, Settle, Between, Close }

    public CraftDelays Delays { get; set; } = new();
    public CraftSessionState State { get; private set; }
    public bool Active { get; private set; }
    public bool RepeatEnabled => Accepted && !stopping && !failed && Attempted < Requested;
    public bool Accepted { get; private set; }
    public bool Blocking => phase != Phase.Idle;
    public bool Failed => failed;
    public bool HasStarted { get; private set; }
    public bool UiReleased => recipeReleased && !Blocking;
    public int Attempted { get; private set; }
    public int Completed { get; private set; }
    public int Requested { get; private set; }
    public string Detail { get; private set; } = "尚未开始本地制作任务。";
    private static long Now => Environment.TickCount64;
    private bool FrameworkSafe => !disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread;
    private bool VersionSupported => data.GameData.Repositories.TryGetValue("ffxiv", out var repository)
        && string.Equals(repository.Version?.Trim(), SupportedVersion, StringComparison.Ordinal)
        && sizeof(RecipeNote) == 0xB40;
    private bool IdentityMatches => player.IsLoaded && player.ContentId == cid && player.ClassJob.RowId == job;

    public NativeCraftService(IFramework framework, IPlayerState player, ICondition condition,
        IDataManager data, IAddonLifecycle lifecycle, IPluginLog log)
    {
        this.framework = framework; this.player = player; this.condition = condition;
        this.data = data; this.lifecycle = lifecycle; this.log = log;
        framework.Update += OnFrame;
        lifecycle.RegisterListener(AddonEvent.PostSetup, OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "Synthesis", OnLifecycle);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "SynthesisSimple", OnLifecycle);
        recipeReleased = true;
    }

    public void Begin(ushort recipe, int count, CraftMode selectedMode)
    {
        if(!FrameworkSafe) throw new InvalidOperationException("制作必须在有效游戏框架线程启动。");
        if(Blocking) throw new InvalidOperationException("上次本地制作或其窗口尚未结束。");
        if(!VersionSupported) throw new InvalidOperationException($"本地制作仅适配客户端 {SupportedVersion}。");
        if(selectedMode is not (CraftMode.Standard or CraftMode.Quick) || count is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(count));
        if(!player.IsLoaded || player.ContentId == 0 || condition[ConditionFlag.Crafting] || AnyCraftUi() ||
           Visible("RecipeNote") || ForeignDialog()) throw new InvalidOperationException("请先结束现有制作和交互窗口。");
        var row = data.GetExcelSheet<Recipe>().GetRowOrDefault(recipe)
            ?? throw new InvalidOperationException("配方不存在。");
        if(row.Number == 0 || row.ItemResult.RowId == 0 || row.AmountResult < 1)
            throw new InvalidOperationException("仅支持普通配方，暂不支持特殊制作手册。");
        if(selectedMode == CraftMode.Quick && !row.CanQuickSynth)
            throw new InvalidOperationException("该配方不支持简易制作。");
        var requiredJob = row.CraftType.RowId + 8;
        if(player.ClassJob.RowId != requiredJob) throw new InvalidOperationException("当前职业不符合配方；请先切到对应制作职业。");
        recipeId = recipe; job = requiredJob; productId = row.ItemResult.RowId; yield = row.AmountResult;
        cid = player.ContentId; Requested = count; mode = selectedMode;
        ingredients = Enumerable.Range(0, row.Ingredient.Count)
            .Select(i => new CraftIngredient(row.Ingredient[i].RowId, row.AmountIngredient[i])).ToArray();
        if(ingredients.Any(x => x.ItemId == productId && x.Amount > 0))
            throw new InvalidOperationException("配方消耗与产出相同物品，无法可靠核对数量。");
        active = (Delays ?? new()).Freeze();
        stopping = failed = openSent = startSent = batchStarted = batchEnded = progressComplete = craftFailed = false;
        quickCancelSent = closeSent = batchReconciled = nqFillSent = false; pendingHqIndex = -1;
        quickDialogRequested = dialogCloseSent = responseTimedOut = false;
        recipeLease = dialogLease = synthesisLease = null;
        Attempted = Completed = 0; HasStarted = Active = false;
        batchAttempts = batchCount = 0; recipeReleased = false; Accepted = true;
        phase = Phase.Open; State = CraftSessionState.Preparing;
        clock.Begin(Now, active.TimeoutSeconds);
        Detail = selectedMode == CraftMode.Standard ? "准备标准制作；请保持 Artisan 自动模式开启。" : "准备简易制作。";
    }

    public void Tick()
    {
        if(!FrameworkSafe || phase == Phase.Idle) return;
        try
        {
            if(!player.IsLoaded || player.ContentId == 0 || player.ContentId != cid)
            {
                // Definitive session invalidation: no old-session UI calls are permitted on the new character.
                Hold("原制作角色已退出或切换；已放弃旧会话的窗口所有权，不会操作新角色。");
                phase = Phase.Idle; recipeReleased = true; Active = false; return;
            }
            if(!VersionSupported || !IdentityMatches) { Hold("职业或客户端版本改变；已撤销所有未发送制作，需手动结束残留窗口。"); return; }
            ObserveCraft();
            if(failed && phase != Phase.Close && phase != Phase.Running && phase != Phase.Settle && phase != Phase.AwaitStart)
                phase = Phase.Close;
            if(!responseTimedOut && phase is not (Phase.Running or Phase.Settle) && clock.Expired(Now))
            {
                responseTimedOut = true;
                Hold(startSent ? "已发送制作但未取得可确认的开始/结束，已停止且不会重发；请手动检查。" : "等待制作窗口超时，已停止后续请求。");
                return;
            }
            if(ForeignDialog()) { Fail("出现了额外交互窗口，已停止后续制作。请手动处理该窗口。"); return; }
            switch(phase)
            {
                case Phase.Open: OpenRecipe(); break;
                case Phase.Materials: FillMaterials(); break;
                case Phase.Start: StartCraft(); break;
                case Phase.QuickDialog: SubmitQuick(); break;
                case Phase.AwaitStart:
                    if(batchStarted) { phase = Phase.Running; State = stopping ? CraftSessionState.Stopping : CraftSessionState.Crafting; }
                    else if(stopping && !startSent) phase = Phase.Close;
                    break;
                case Phase.Running: Running(); break;
                case Phase.Settle: Settle(); break;
                case Phase.Between:
                    if(stopping || Attempted >= Requested) { phase = Phase.Close; break; }
                    if(Gate("between", active.BetweenCraftsMs)) ResetBatch();
                    break;
                case Phase.Close: CloseOwnedUi(); break;
            }
        }
        catch(Exception ex) { log.Error(ex, "NativeCraftService stopped safely"); Hold("本地制作遇到异常，已撤销后续步骤；请手动确认当前制作状态。"); }
    }

    private void OpenRecipe()
    {
        if(stopping) { phase = Phase.Close; return; }
        if(openSent)
        {
            var addon = Addon("RecipeNote");
            if(!Visible(addon)) return;
            // Opening a recipe can show the persistent notebook before its selection/mapping is populated.
            if(!SelectedMatches() || !Owner(addon, (nint)AgentRecipeNote.Instance(), 0)) return;
            recipeLease ??= Capture(addon);
            if(!Matches(recipeLease, addon)) { Fail("制作手册窗口被替换。"); return; }
            phase = Phase.Materials; return;
        }
        if(Visible("RecipeNote") || AnyCraftUi()) { Fail("开始前出现了其他制作窗口。"); return; }
        if(!Gate("open", active.OpenRecipeMs)) return;
        var agent = AgentRecipeNote.Instance();
        if(agent == null) { Fail("制作手册代理不可用。"); return; }
        openSent = true; requestedAfter = generation;
        agent->OpenRecipeByRecipeId(recipeId);
    }

    private void FillMaterials()
    {
        if(stopping) { phase = Phase.Close; return; }
        if(!OwnSelectedRecipe()) { Fail("材料分配前配方或手册已改变。"); return; }
        var stock = ReadStock();
        var selected = Selected();
        if(selected == null || selected->Ingredients.Length != 6) { Fail("配方材料结构尚未就绪。"); return; }
        var recipeIngredients = selected->Ingredients.ToArray().Select(x => new CraftIngredient(x.ItemId, x.Amount)).ToArray();
        var required = ingredients.Where(x => x.ItemId > 19 && x.Amount > 0).GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
        var actual = recipeIngredients.Where(x => x.ItemId != 0 && x.Amount > 0).GroupBy(x => x.ItemId)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
        if(required.Count != actual.Count || required.Any(x => actual.GetValueOrDefault(x.Key) != x.Value))
        { Fail("制作手册材料与数据表配方不一致。"); return; }
        // Includes crystals. Quick uses an exact bounded batch; Standard consumes one attempt.
        batchCount = mode == CraftMode.Quick ? CraftRules.QuickBatch(Requested - Attempted, ingredients, stock) : 1;
        if(batchCount < 1 || CraftRules.QuickBatch(1, ingredients, stock) != 1) { Fail("背包材料不足，未发送制作请求。"); return; }
        assignments = CraftRules.Assign(recipeIngredients, stock);
        if(!nqFillSent)
        {
            if(!Gate("fill-normal", active.FillMaterialsMs)) return;
            nqFillSent = true;
            // Verified AgentRecipeNote callback 6 needs a second value, not a one-argument refresh.
            // 0xFFFF is the native fill-all-NQ sentinel. It fills NQ and only preserves valid existing HQ.
            CallbackPair(Addon("RecipeNote"), 6, 0xFFFF); return;
        }
        var selection = selected->Ingredients.ToArray().Select(x => new CraftMaterialSelection(x.ItemId, x.Amount, x.NQCount, x.HQCount)).ToArray();
        var next = CraftRules.NextMaterialStep(selection, assignments, pendingHqIndex, pendingHqAmount);
        if(next.Action == CraftMaterialAction.Invalid) { Fail("材料选择与核实的 NQ/HQ 分配不一致。"); return; }
        if(next.Action == CraftMaterialAction.Waiting) return;
        pendingHqIndex = -1;
        if(next.Action == CraftMaterialAction.AddHighQuality)
        {
            // Add exactly one HQ only while the ingredient is incomplete and true stock covers it.
            if(!Gate("fill-hq", active.FillMaterialsMs)) return;
            pendingHqIndex = next.Index; pendingHqAmount = next.NextAmount;
            CallbackPair(Addon("RecipeNote"), 6, next.Index | 0x10000); return;
        }
        phase = Phase.Start;
    }

    private void StartCraft()
    {
        if(stopping) { phase = Phase.Close; return; }
        if(!OwnSelectedRecipe() || AnyCraftUi() || !SafeCraftBoundary()) { Fail("制作开始前窗口或状态已改变。"); return; }
        var selected = Selected();
        for(var i = 0; i < assignments.Length; i++)
        {
            var item = selected->Ingredients[i]; var expected = assignments[i];
            if(item.ItemId != expected.ItemId || item.NQCount != expected.Normal || item.HQCount != expected.HighQuality ||
               item.Amount != expected.Normal + expected.HighQuality) { Fail("材料分配被修改，未发送制作。"); return; }
        }
        var stock = ReadStock();
        if(CraftRules.QuickBatch(batchCount, ingredients, stock) != batchCount) { Fail("开始前材料数量发生变化。"); return; }
        var addon = (AddonRecipeNote*)Addon("RecipeNote");
        var button = mode == CraftMode.Quick ? addon->QuickSynthesisButton : addon->SynthesizeButton;
        if(button == null || !button->IsEnabled) { Fail("制作按钮不可用；请核对解锁、属性、装备与材料。"); return; }
        if(!Gate("start", active.StartCraftMs)) return;
        productBefore = Total(stock, productId); attemptsBefore = Attempted;
        requestedAfter = generation;
        if(mode == CraftMode.Quick) { phase = Phase.QuickDialog; startSent = false; quickDialogRequested = true; }
        else { phase = Phase.AwaitStart; startSent = true; State = CraftSessionState.WaitingStart; }
        // Direct callbacks avoid Artisan's button-event preprocessing queue (events 14/15).
        Callback(&addon->AtkUnitBase, mode == CraftMode.Quick ? 9 : 8);
    }

    private void SubmitQuick()
    {
        var addon = Addon("SynthesisSimpleDialog");
        if(!Visible(addon)) return;
        if(dialogLease == null || !Matches(dialogLease, addon) || !OwnSelectedRecipe()) { Fail("简易制作数量窗口不属于本次任务。"); return; }
        if(!Owner(addon, (nint)AgentRecipeNote.Instance(), 1)) return;
        if(stopping)
        {
            if(!Gate("close-dialog", active.CloseRecipeMs)) return;
            // Kind 1's native -1 branch clears the agent's owned dialog ID without starting synthesis.
            phase = Phase.Close; dialogCloseSent = true; Callback(addon, -1); return;
        }
        if(addon->AtkValuesCount < 4 || !Integer(addon->AtkValues[0], out var normal) || !Integer(addon->AtkValues[1], out var all) ||
           normal < 0 || all < 0 || batchCount > Math.Max(normal, all) || batchCount > 99 || batchCount < 1)
        { Fail("简易制作数量上限不符，未提交。"); return; }
        var stock = ReadStock();
        if(CraftRules.QuickBatch(batchCount, ingredients, stock) != batchCount) { Fail("简易制作材料在提交前发生变化。"); return; }
        if(!Gate("quick-quantity", active.SubmitQuickMs)) return;
        productBefore = Total(stock, productId); requestedAfter = generation;
        phase = Phase.AwaitStart; State = CraftSessionState.WaitingStart; startSent = true;
        var values = stackalloc AtkValue[3];
        values[0] = new() { Type = AtkValueType.Int, Int = batchCount };
        values[1] = new() { Type = AtkValueType.Bool, Byte = 1 };
        values[2] = new() { Type = AtkValueType.Bool, Byte = 1 };
        addon->FireCallback(3, values, true);
    }

    private void ObserveCraft()
    {
        if(!startSent || batchReconciled) return;
        var addon = Addon(mode == CraftMode.Quick ? "SynthesisSimple" : "Synthesis");
        if(Visible(addon))
        {
            if(synthesisLease == null || !Matches(synthesisLease, addon)) { Fail("制作进度窗口被替换或不属于本次请求。"); return; }
            if(!ActiveRecipeMatches()) { Fail("实际制作配方与请求不符。"); return; }
            if(mode == CraftMode.Quick)
            {
                if(!QuickState(addon, out var current, out var total)) return;
                if(total != batchCount || current < batchAttempts) { Fail("简易制作进度或总次数发生异常变化。"); return; }
                batchAttempts = current;
            }
            else { batchAttempts = 1; CaptureStandard((AddonSynthesis*)addon); }
            batchStarted = HasStarted = true; Active = true;
            Attempted = attemptsBefore + batchAttempts;
        }
        if(batchStarted && !Visible(addon) && SafeCraftBoundary())
        { batchEnded = true; Active = false; }
    }

    private void Running()
    {
        if(batchEnded)
        {
            phase = Phase.Settle; State = stopping ? CraftSessionState.Stopping : CraftSessionState.Settling;
            settleAt = Now + 5_000; clock.Cancel(); return;
        }
        if(mode == CraftMode.Quick)
        {
            var addon = Addon("SynthesisSimple");
            if(!Visible(addon)) return;
            if(!ActiveRecipeMatches()) { Hold("简易制作的实际配方发生变化，未发送取消；请手动处理。"); return; }
            if(!QuickState(addon, out var current, out var total)) return;
            // The same native callback closes a finished own QS window or requests cancellation.
            // It does not guarantee that the current item finishes before cancellation.
            if((stopping || current == total) && !quickCancelSent)
            {
                if(!Gate("cancel-quick", active.CancelQuickMs)) return;
                quickCancelSent = true; Callback(addon, -1);
            }
        }
        Detail = $"{(mode == CraftMode.Standard ? "标准" : "简易")}制作：已观察尝试 {Attempted}/{Requested}；已确认成功 {Completed}。";
    }

    private void Settle()
    {
        var current = Total(ReadStock(), productId);
        var result = CraftRules.Reconcile(batchStarted, batchEnded, batchAttempts, batchCount, yield,
            productBefore, current, mode == CraftMode.Standard, progressComplete, craftFailed);
        if(result == CraftReconciliation.Invalid) { FailEnded("制作产出数量与本次实际尝试不符，已停止且不会补做。"); return; }
        if(result == CraftReconciliation.Waiting)
        {
            if(Now >= settleAt) { FailEnded("制作结束后 5 秒仍无法核实产出/进度，已停止且不会补做。"); }
            return;
        }
        Completed += checked((int)((current - productBefore) / yield)); batchReconciled = true;
        if(!stopping && batchAttempts < batchCount)
        { failed = stopping = true; Detail = "制作批次提前结束，已停止；不会补做未确认的尝试。"; }
        if(result == CraftReconciliation.Failed)
        { failed = stopping = true; Detail = $"标准制作失败；已计入尝试 {Attempted}/{Requested}，不会补做。"; }
        startSent = false; Active = false; responseTimedOut = false; clock.Begin(Now, active.TimeoutSeconds);
        phase = stopping || Attempted >= Requested ? Phase.Close : Phase.Between;
        State = failed ? CraftSessionState.Failed : stopping ? CraftSessionState.Stopping : CraftSessionState.Preparing;
    }

    private void ResetBatch()
    {
        startSent = batchStarted = batchEnded = progressComplete = craftFailed = quickCancelSent = batchReconciled = false;
        synthesisLease = dialogLease = null; batchAttempts = 0; nqFillSent = false; pendingHqIndex = -1;
        quickDialogRequested = dialogCloseSent = responseTimedOut = false;
        clock.Begin(Now, active.TimeoutSeconds);
        if(OwnSelectedRecipe()) phase = Phase.Materials;
        else if(!Visible("RecipeNote")) { openSent = false; recipeLease = null; phase = Phase.Open; }
        else Fail("下一次制作前手册已被替换。");
    }

    public void Stop()
    {
        if(disposed || stopping || phase == Phase.Idle) return;
        stopping = true; clock.Cancel(); clock.Begin(Now, active.TimeoutSeconds);
        State = failed ? CraftSessionState.Failed : CraftSessionState.Stopping;
        Detail = Active && mode == CraftMode.Standard ? "已取消后续标准制作，等待当前制作结束。" : "已取消所有未发送步骤，准备结束本插件拥有的制作窗口。";
        if(!FrameworkSafe) return;
        if(!startSent && phase != Phase.QuickDialog) phase = Phase.Close;
    }

    private void CloseOwnedUi()
    {
        if(startSent && !batchEnded) { phase = batchStarted ? Phase.Running : Phase.AwaitStart; return; }
        if(!SafeCraftBoundary() || Visible("Synthesis") || Visible("SynthesisSimple")) return;
        var dialog = Addon("SynthesisSimpleDialog");
        if(Visible(dialog))
        {
            if(!Matches(dialogLease, dialog) || !Owner(dialog, (nint)AgentRecipeNote.Instance(), 1))
            { Hold("简易制作数量窗口归属不明，请手动关闭。"); return; }
            if(!dialogCloseSent && Gate("close-dialog", active.CloseRecipeMs)) { dialogCloseSent = true; Callback(dialog, -1); }
            return;
        }
        // A sent callback 9 may create its dialog asynchronously after Stop/timeout.
        if(quickDialogRequested && dialogLease == null) return;
        var addon = Addon("RecipeNote");
        if(Visible(addon))
        {
            if(!OwnSelectedRecipe()) { Hold("制作手册归属已改变，不会关闭其他任务的窗口。"); return; }
            if(closeSent) return;
            if(!Gate("close-recipe", active.CloseRecipeMs)) return;
            closeSent = true; Callback(addon, -1); return;
        }
        // An already-issued OpenRecipe call may be waiting on resource loading.
        if(openSent && recipeLease == null) return;
        recipeReleased = true; phase = Phase.Idle; Active = false;
        State = failed ? CraftSessionState.Failed : stopping ? CraftSessionState.Stopped : CraftSessionState.Completed;
        if(!failed) Detail = $"制作已结束：尝试 {Attempted}/{Requested}，确认成功 {Completed}；自有窗口已释放。";
    }

    private void Fail(string reason)
    {
        if(failed) return;
        failed = true; Stop(); State = CraftSessionState.Failed; Detail = reason;
        log.Warning("NativeCraftService: {Reason}", reason);
    }
    private void Hold(string reason)
    {
        failed = stopping = true; clock.Cancel(); State = CraftSessionState.Failed; Detail = reason;
        // Preserve ownership/Blocking after uncertainty; never free AutoRetainer to relog over an unresolved craft.
    }
    private void FailEnded(string reason)
    {
        // Real end is independently proven. Accounting uncertainty blocks further crafting, not safe own-UI cleanup.
        Hold(reason); startSent = false; batchReconciled = true; Active = false;
        phase = Phase.Close; responseTimedOut = false; clock.Begin(Now, active.TimeoutSeconds);
    }
    private bool Gate(string action, int delay)
    {
        var sent = clock.Dispatch(action, delay, Now, frame);
        if(!sent && clock.Remaining(Now) > 0) Detail = $"等待制作步骤：{action}，剩余 {clock.Remaining(Now)} 毫秒。";
        return sent;
    }
    private void OnFrame(IFramework _) => frame++;

    private void OnLifecycle(AddonEvent evt, AddonArgs args)
    {
        if(disposed || !framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return;
        var name = args.AddonName;
        if(name is not ("RecipeNote" or "Synthesis" or "SynthesisSimple" or "SynthesisSimpleDialog")) return;
        var pointer = (nint)args.Addon.Address;
        if(evt == AddonEvent.PostSetup)
        {
            generations[pointer] = ++generation;
            if(phase == Phase.Idle) return;
            var lease = new Lease(pointer, generation);
            if(name == "RecipeNote" && openSent && recipeLease == null) recipeLease = lease;
            if(name == "SynthesisSimpleDialog" && quickDialogRequested && generation > requestedAfter && dialogLease == null) dialogLease = lease;
            if(((name == "Synthesis" && mode == CraftMode.Standard) || (name == "SynthesisSimple" && mode == CraftMode.Quick)) &&
                startSent && generation > requestedAfter && synthesisLease == null) synthesisLease = lease;
        }
        if(evt is AddonEvent.PostRefresh or AddonEvent.PreFinalize && startSent && synthesisLease?.Pointer == pointer && IdentityMatches)
        {
            if(name == "Synthesis" && ActiveRecipeMatches())
            {
                CaptureStandard((AddonSynthesis*)pointer); batchStarted = HasStarted = true; batchAttempts = 1;
                Attempted = attemptsBefore + 1;
            }
            else if(name == "SynthesisSimple" && ActiveRecipeMatches() && QuickState((AtkUnitBase*)pointer, out var current, out var total) && total == batchCount)
            { batchStarted = HasStarted = true; batchAttempts = Math.Max(batchAttempts, current); Attempted = attemptsBefore + batchAttempts; }
        }
        if(evt == AddonEvent.PreFinalize)
        {
            generations.Remove(pointer);
            if(recipeLease?.Pointer == pointer) recipeReleased = true;
        }
    }

    private void CaptureStandard(AddonSynthesis* addon)
    {
        if(addon == null) return;
        if(Number(addon->CurrentProgress, out var progress) && Number(addon->MaxProgress, out var maximum) && maximum > 0 && progress >= maximum)
            progressComplete = true;
        if(!progressComplete && Number(addon->CurrentDurability, out var durability) && durability == 0) craftFailed = true;
    }
    private bool QuickState(AtkUnitBase* addon, out int current, out int total)
    {
        current = total = 0;
        if(!Matches(synthesisLease, addon) || addon->AtkValuesCount < 9 ||
           !Owner(addon, imageBase + QuickReceiverRva, 0) || !Integer(addon->AtkValues[3], out current) ||
           !Integer(addon->AtkValues[4], out total) || !Integer(addon->AtkValues[7], out var item) || item != productId ||
           current < 0 || total < 1 || total > 99 || current > total) return false;
        return true;
    }
    private bool ActiveRecipeMatches()
    {
        var note = RecipeNote.Instance();
        // Standard start writes CraftingRecipeId; Quick does not, so that field may be stale in QS.
        // Never OR these distinct native contracts or fall back to a stale Agent selection.
        return note != null && startSent && CraftRules.ActiveRecipeMatches(mode, recipeId, note->CraftingRecipeId, note->ActiveCraftRecipeId);
    }
    private bool OwnSelectedRecipe() => Matches(recipeLease, Addon("RecipeNote")) && Visible("RecipeNote") && SelectedMatches()
        && Owner(Addon("RecipeNote"), (nint)AgentRecipeNote.Instance(), 0);
    private bool SelectedMatches() => Selected() is var selected && selected != null && selected->RecipeId == recipeId && selected->ItemId == productId && selected->CraftType + 8 == job;
    private static RecipeNote.RecipeEntry* Selected()
    {
        var note = RecipeNote.Instance();
        if(note == null || !note->IsRecipeListReady || note->RecipeList == null) return null;
        var list = note->RecipeList;
        return list->Recipes == null || list->RecipeCount <= 0 || list->SelectedIndex >= list->RecipeCount
            ? null : list->Recipes + list->SelectedIndex;
    }
    private Lease Capture(AtkUnitBase* addon)
    {
        var pointer = (nint)addon;
        if(!generations.TryGetValue(pointer, out var value)) generations[pointer] = value = ++generation;
        return new(pointer, value);
    }
    private bool Matches(Lease? lease, AtkUnitBase* addon) => lease is { } value && addon != null &&
        value.Pointer == (nint)addon && generations.GetValueOrDefault((nint)addon) == value.Generation;
    private static bool Owner(AtkUnitBase* addon, nint receiver, ulong kind)
    {
        var module = RaptureAtkModule.Instance();
        return receiver != 0 && addon != null && module != null && module->AddonCallbackMapping.TryGetValue(addon->Id, out var mapping, false)
            && (nint)mapping.AgentInterface == receiver && mapping.EventKind == kind;
    }
    private static bool Integer(AtkValue value, out int number)
    {
        number = 0;
        if(value.Type == AtkValueType.Int) { number = value.Int; return true; }
        if(value.Type == AtkValueType.UInt && value.UInt <= int.MaxValue) { number = (int)value.UInt; return true; }
        return false;
    }
    private static bool Number(AtkTextNode* node, out int value)
    {
        value = 0;
        return node != null && int.TryParse(node->NodeText.ToString(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }
    private static long Total(IReadOnlyDictionary<uint, CraftStock> stock, uint item) => stock.GetValueOrDefault(item).Normal + stock.GetValueOrDefault(item).HighQuality;
    private static Dictionary<uint, CraftStock> ReadStock()
    {
        var manager = InventoryManager.Instance();
        if(manager == null) throw new InvalidOperationException("背包不可用。");
        var result = new Dictionary<uint, CraftStock>();
        foreach(var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals })
        {
            var container = manager->GetInventoryContainer(type);
            if(container == null || !container->IsLoaded || container->Items == null || container->Size < 0 || container->Size > 1000)
                throw new InvalidOperationException("背包尚未载入。");
            for(var i = 0; i < container->Size; i++)
            {
                var item = container->Items + i;
                if(item->IsSymbolic || item->Quantity < 0) throw new InvalidOperationException("背包物品结构不受支持。");
                if(item->ItemId == 0 || item->Quantity == 0) continue;
                var old = result.GetValueOrDefault(item->ItemId);
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                result[item->ItemId] = hq ? old with { HighQuality = old.HighQuality + item->Quantity } : old with { Normal = old.Normal + item->Quantity };
            }
        }
        return result;
    }
    private static AtkUnitBase* Addon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : manager->GetAddonByName(name);
    }
    private static bool Visible(AtkUnitBase* addon) => addon != null && addon->IsVisible;
    private static bool Visible(string name) => Visible(Addon(name));
    private static bool AnyCraftUi() => Visible("Synthesis") || Visible("SynthesisSimple") || Visible("SynthesisSimpleDialog");
    private bool SafeCraftBoundary() => condition[ConditionFlag.PreparingToCraft] ||
        (!condition[ConditionFlag.Crafting] && !condition[ConditionFlag.ExecutingCraftingAction]);
    private static bool ForeignDialog() => Visible("SelectYesno") || Visible("InputNumeric") || Visible("Trade") || Visible("SelectString") || Visible("RetainerList");
    private static void Callback(AtkUnitBase* addon, int value)
    {
        var arg = new AtkValue { Type = AtkValueType.Int, Int = value };
        addon->FireCallback(1, &arg, true);
    }
    private static void CallbackPair(AtkUnitBase* addon, int first, int second)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new() { Type = AtkValueType.Int, Int = first };
        values[1] = new() { Type = AtkValueType.Int, Int = second };
        addon->FireCallback(2, values, true);
    }
    public void Dispose()
    {
        if(disposed) return;
        try
        {
            Stop();
            // Unload cannot wait for safe delays. Never bypass the limiter or touch another session.
            if(Blocking) log.Warning("NativeCraftService unloaded with an unresolved own craft/window; remaining queued steps were cancelled. Please close it manually.");
        }
        finally
        {
            disposed = true; clock.Cancel(); framework.Update -= OnFrame;
            lifecycle.UnregisterListener(OnLifecycle);
        }
    }
}
