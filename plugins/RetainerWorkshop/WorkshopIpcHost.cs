using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;
internal sealed unsafe class WorkshopIpcHost(IDalamudPluginInterface pi, IClientState client, IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gameGui, NativeCraftService craftSession) : IBridgeHost
{
    public const string RetainerModeHelp = "输入 /ays settings → 高级设置 → 专家设置 → 勾选“显示多角色模式类型选择器”；返回 /ays 主窗口，在“多角色”旁的下拉框选择 Retainers。";
    private long readinessAt;
    private CyclePlan? readinessPlan;
    private bool readinessCache;
    public CraftMode RequestedCraftMode { get; set; }
    private bool usesLocalCraftQueue;
    private ushort localRecipe;
    private VendorDelays delays = new();
    private long nextRecipeClose;
    public void ConfigureDelays(VendorDelays settings) => delays = settings.Freeze();
    private bool Loaded(string name) => pi.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == name);
    private bool ArtisanLoaded => Loaded("Artisan");
    public string ReadinessDescription { get; private set; } = "";
    public long Now => Environment.TickCount64;
    public ulong CurrentCid => player.ContentId;
    public bool LoggedIn => client.IsLoggedIn;
    public bool ArBusy => Get<bool>("AutoRetainer.PluginState.IsBusy");
    public bool NavigationBusy => Get<bool>("Lifestream.IsBusy");
    public bool ArMultiEnabled => Get<bool>("AutoRetainer.GetMultiModeEnabled");
    public bool ArtisanEndurance => usesLocalCraftQueue ? craftSession.RepeatEnabled : ArtisanLoaded && Get<bool>("Artisan.GetEnduranceStatus");
    public bool CraftRequestAccepted => usesLocalCraftQueue ? craftSession.Accepted : ArtisanEndurance;
    public bool CanStopCraftNow => usesLocalCraftQueue || SafeCraftBoundary || !ActualCraftActive || Visible("SynthesisSimple");
    public bool CraftHasStarted => usesLocalCraftQueue ? craftSession.HasStarted : ActualCraftActive;
    public string? CraftError => usesLocalCraftQueue && craftSession.Failed ? craftSession.Detail : null;
    public bool UsesLocalCraftQueue => usesLocalCraftQueue;
    public bool ArtisanForeignControl => ArtisanLoaded && (Get<bool>("Artisan.IsListRunning") || Get<bool>("Artisan.GetStopRequest")
        || (usesLocalCraftQueue && Get<bool>("Artisan.GetEnduranceStatus")));
    public bool ArtisanIdle => (!ArtisanLoaded || !Get<bool>("Artisan.IsBusy")) && !craftSession.Blocking && !ArtisanEndurance && !ArtisanForeignControl;
    private bool Visible(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible;
    }
    public bool ActualCraftActive => Visible("Synthesis") || Visible("SynthesisSimple")
        || (condition[ConditionFlag.Crafting] && !condition[ConditionFlag.PreparingToCraft]);
    // PreparingToCraft is authoritative for IdleBetween. ExecutingCraftingAction can
    // remain set after quick synthesis (Artisan's own state machine documents this).
    public bool SafeCraftBoundary => !Visible("Synthesis") && !Visible("SynthesisSimple")
        && (condition[ConditionFlag.PreparingToCraft] || (!condition[ConditionFlag.Crafting] && !condition[ConditionFlag.ExecutingCraftingAction]));
    private static readonly ConditionFlag[] Occupied =
        [ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied33, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
        ConditionFlag.OccupiedSummoningBell, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut,
        ConditionFlag.InCombat, ConditionFlag.Casting, ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.Gathering];
    public bool UiReleased => LoggedIn && player.IsLoaded && objects.LocalPlayer != null && !Occupied.Any(flag => condition[flag])
        && !new[] { "RetainerList", "InventoryRetainer", "InventoryRetainerLarge", "Talk", "InputNumeric", "ContextMenu", "Trade", "SelectString", "SelectYesno", "RetainerTaskResult", "RetainerTaskAsk", "RecipeNote", "Synthesis", "SynthesisSimple", "SynthesisSimpleDialog", "WKSRecipeNotebook" }.Any(Visible);
    public bool RecipeMatches(ushort recipe)
    {
        if(usesLocalCraftQueue && craftSession.HasStarted && craftSession.UiReleased && !ActualCraftActive)
            return localRecipe == recipe; // The session verified its recipe at start, then closed its own menu.
        var note = RecipeNote.Instance();
        if(note == null || !note->IsRecipeListReady || note->RecipeList == null) return false;
        var list = note->RecipeList;
        return list->Recipes != null && list->RecipeCount > 0 && list->SelectedIndex < list->RecipeCount
            && list->SelectedRecipe != null && list->SelectedRecipe->RecipeId == recipe;
    }
    public bool CloseOwnRecipe(ushort recipe)
    {
        if(usesLocalCraftQueue)
        {
            craftSession.Stop();
            return !craftSession.Blocking && craftSession.UiReleased && UiReleased;
        }
        if(!SafeCraftBoundary || ArtisanEndurance || ArtisanForeignControl) return false;
        if(!Visible("RecipeNote")) return UiReleased;
        if(!RecipeMatches(recipe)) throw new InvalidOperationException("配方菜单不属于本次制作，拒绝关闭。");
        if(Now < nextRecipeClose) return false;
        nextRecipeClose = Now + delays.CloseMs;
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("RecipeNote").Address;
        var args = stackalloc AtkValue[1];
        args[0] = new AtkValue { Type = AtkValueType.Int, Int = -1 };
        addon->FireCallback(1, args, true);
        return true;
    }
    private T Get<T>(string name)
    {
        var ipc = pi.GetIpcSubscriber<T>(name);
        if(!ipc.HasFunction) throw new InvalidOperationException("依赖接口不可用：" + name);
        return ipc.InvokeFunc();
    }
    private void Act(string name) => pi.GetIpcSubscriber<object>(name).InvokeAction();
    public CharacterSnapshot? ReadCharacter(ulong cid) => pi.GetIpcSubscriber<ulong, CharacterSnapshot?>("AutoRetainer.GetOfflineCharacterData").InvokeFunc(cid);
    public void EnableMultiMode() => pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled").InvokeAction(true);
    public void DisableArFunctions() => Act("AutoRetainer.PluginState.DisableAllFunctions");
    public void AbortArTasks() => Act("AutoRetainer.PluginState.AbortAllTasks");
    public void AbortNavigation() => Act("Lifestream.Abort");
    public void Relog(string name, string world)
    {
        var result = pi.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter").InvokeFunc(name, world);
        if(result != 0) throw new CharacterSwitchRejectedException($"Lifestream 换角被拒绝（错误码 {result}），不会重复提交。");
    }
    public void RequestRetainer(string owner) => pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess").InvokeAction(owner);
    public void FinishRetainer() => Act("AutoRetainer.FinishPostprocessRequest");
    public void RequestCharacter(string owner) => pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess").InvokeAction(owner);
    public void FinishCharacter() => Act("AutoRetainer.FinishCharacterPostprocessRequest");
    public void Craft(ushort recipe, int count)
    {
        usesLocalCraftQueue = RequestedCraftMode != CraftMode.Inherit;
        if(!ArtisanLoaded) throw new InvalidOperationException("启用制作必须加载 Artisan。");
        nextRecipeClose = 0;
        if(usesLocalCraftQueue) { localRecipe = recipe; craftSession.Begin(recipe, count, RequestedCraftMode); }
        else pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem").InvokeAction(recipe, count);
    }
    public void CancelPendingCraft() { if(usesLocalCraftQueue) craftSession.Stop(); }
    public void StopEndurance()
    {
        if(usesLocalCraftQueue) craftSession.Stop();
        else pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus").InvokeAction(false);
    }
    public List<CharacterSnapshot> RegisteredCharacters() => Get<List<ulong>>("AutoRetainer.GetRegisteredCIDs").Select(ReadCharacter)
        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.World)).Cast<CharacterSnapshot>().ToList();
    private Dictionary<ulong, HashSet<string>> Selection() => Get<Dictionary<ulong, HashSet<string>>>("AutoRetainer.PluginState.GetEnabledRetainers");
    private (RetainerSnapshot Retainer, long Remaining)[] TimedRetainers(CharacterSnapshot character, Dictionary<ulong, HashSet<string>> selected)
    {
        if(!selected.TryGetValue(character.CID, out var names)) return [];
        var retainers = character.RetainerData.Where(x => names.Contains(x.Name) && x.HasVenture).ToArray();
        if(retainers.Length == 0) return [];
        var remaining = pi.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining").InvokeFunc(character.CID);
        if(remaining == null) throw new InvalidOperationException("无法读取已选雇员到期时间。");
        var arNow = retainers.Min(x => x.VentureEndsAt) - remaining.Value;
        return retainers.Select(x => (x, x.VentureEndsAt - arNow)).ToArray();
    }
    public IReadOnlyList<TargetRetainer> ReadVisitTargets(ulong cid)
    {
        var character = ReadCharacter(cid) ?? throw new InvalidOperationException("收尾角色资料不可用。");
        // AR's logged-in character Done rule uses a five-minute horizon.
        return TimedRetainers(character, Selection()).Where(x => x.Remaining <= 300 && x.Retainer.VentureID != 0)
            .Select(x => new TargetRetainer(x.Retainer.Name, x.Retainer.VentureID, x.Retainer.VentureEndsAt)).ToArray();
    }
    public bool FirstReady(CyclePlan plan)
    {
        if(readinessPlan == plan && Now < readinessAt) return readinessCache;
        var selected = Selection();
        var first = ReadCharacter(plan.First.Cid) ?? throw new InvalidOperationException("开头角色资料不可用。");
        var timing = Get<RetainerTiming>("AutoRetainer.GetConfig.MultiModeRetainerConfiguration");
        ReadinessCharacter Convert(CharacterSnapshot c) => new(c.Enabled, c.ExcludeRetainer, TimedRetainers(c, selected).Select(x => x.Remaining).ToArray());
        var sentinel = Convert(first);
        readinessCache = WorkshopReadiness.IsFirstReady(sentinel, timing);
        readinessAt = Now + 1000; readinessPlan = plan;
        ReadinessDescription = sentinel.Remaining.Count == 0
            ? "开头角色暂无已选执行中探险，等待新任务；不会自动开启下一轮收取"
            : $"开头判定：{(timing.MultiWaitForAll ? "全部" : "任一")}已选探险进入 {timing.AdvanceTimer} 秒提前窗口";
        return readinessCache;
    }
    // Check again during each coordinator tick. AR busy/navigation is phase-specific and is not a conflict here.
    public void ValidateExclusive(bool requireArtisan, bool allowMultiMode = false)
    {
        if(!Loaded("AutoRetainer") || !Loaded("Lifestream")) throw new InvalidOperationException("请启用 AutoRetainer 与 Lifestream。");
        if(Loaded("RetainerCraftBridge") || Loaded("RetainerVendorSweep"))
            throw new InvalidOperationException("请先禁用旧的雇员收取后制作及多角色雇员直售，避免同时接管。");
        if(requireArtisan && !ArtisanLoaded) throw new InvalidOperationException("启用制作必须加载 Artisan。");
        if(!allowMultiMode && ArMultiEnabled) throw new InvalidOperationException("当前阶段不能启用 AR 多角色模式。");
        if(Get<bool>("AutoRetainer.GetSuppressed")) throw new InvalidOperationException("AR 正被其他流程抑制。");
        foreach(var (name, label) in new[] { ("ArtisanIntegration", "旧 Artisan 整合"), ("RetainerSense", "雇员感知"),
            ("OldRetainerSense", "旧雇员感知"), ("FullAutoGCDelivery", "自动军票交付"), ("NightMode", "夜间模式"),
            ("MultiAutoStart", "游戏启动时自动多角色"), ("MultiOnPluginLoad", "插件加载时自动多角色") })
            if(Get<bool>("AutoRetainer.GetConfig." + name)) throw new InvalidOperationException("请关闭 AR 的“" + label + "”。");
        if(Get<int>("AutoRetainer.GetConfig.OpenBellBehaviorNoVentures") != 2 || Get<int>("AutoRetainer.GetConfig.OpenBellBehaviorWithVentures") != 2)
            throw new InvalidOperationException("请将 AR 有/无可用探险时的普通开铃操作均设为 Disable AutoRetainer；AR 自身多角色自动开铃不受此设置影响。");
        if(Get<int>("AutoRetainer.GetConfig.MultiModeType") != 0)
            throw new InvalidOperationException("AR 多角色范围必须为 Retainers（仅雇员）。" + RetainerModeHelp);
    }
    public CyclePlan PrepareCollection(ulong firstCid, ulong endCid, bool requireArtisan)
    {
        ValidateExclusive(requireArtisan);
        Act("AutoRetainer.Init");
        var registered = RegisteredCharacters();
        var first = registered.FirstOrDefault(x => x.CID == firstCid) ?? throw new InvalidOperationException("请选择有效开头角色。");
        var crafting = registered.FirstOrDefault(x => x.CID == endCid) ?? throw new InvalidOperationException("请选择有效收尾角色。");
        if(!UiReleased) throw new InvalidOperationException("请先登录并关闭当前交互及制作界面。");
        if(ArMultiEnabled || ArBusy || NavigationBusy || Get<bool>("AutoRetainer.GetSuppressed"))
            throw new InvalidOperationException("请关闭原 AR 多角色模式，等待 AR/Lifestream 空闲且未被抑制。");
        var multiMode = Get<int>("AutoRetainer.GetConfig.MultiModeType");
        if(multiMode != 0) throw new InvalidOperationException($"AR 当前范围为 {multiMode switch { 1 => "Submersibles（潜艇）", 2 => "Everything（雇员和潜艇）", _ => multiMode.ToString() }}，本循环需要 Retainers（仅雇员）。" + RetainerModeHelp);
        if(Get<bool>("AutoRetainer.GetConfig.ArtisanIntegration") || Get<bool>("AutoRetainer.GetConfig.RetainerSense")
            || Get<bool>("AutoRetainer.GetConfig.OldRetainerSense") || Get<bool>("AutoRetainer.GetConfig.FullAutoGCDelivery") || Get<bool>("AutoRetainer.GetConfig.NightMode"))
            throw new InvalidOperationException("请关闭 AR 旧 Artisan 整合、雇员感知、全自动军票交付及夜间模式，避免并行接管。");
        if(!ArtisanIdle) throw new InvalidOperationException("Artisan 正忙或被其他流程暂停。");
        foreach(var endpoint in new[] { "AutoRetainer.PluginState.DisableAllFunctions", "AutoRetainer.PluginState.AbortAllTasks", "Lifestream.Abort", "AutoRetainer.FinishPostprocessRequest", "AutoRetainer.FinishCharacterPostprocessRequest" })
            if(!pi.GetIpcSubscriber<object>(endpoint).HasAction) throw new InvalidOperationException("缺少接口：" + endpoint);
        if(!pi.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter").HasFunction
            || !pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess").HasAction
            || !pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess").HasAction
            || !pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled").HasAction)
            throw new InvalidOperationException("依赖插件缺少本流程需要的公开接口。");
        if(requireArtisan && (!pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem").HasAction
            || !pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus").HasAction))
            throw new InvalidOperationException("Artisan 缺少制作公开接口。");
        var selected = Selection();
        foreach(var character in new[] { first, crafting })
            WorkshopReadiness.RequireCollectionEndpoint(new(character.Enabled, character.ExcludeRetainer,
                !character.Enabled || character.ExcludeRetainer ? [] : TimedRetainers(character, selected).Select(x => x.Remaining).ToArray()));
        var plan = new CyclePlan(new(first.CID, first.Name, first.World), new(crafting.CID, crafting.Name, crafting.World));
        readinessAt = 0; FirstReady(plan);
        return plan;
    }
}
