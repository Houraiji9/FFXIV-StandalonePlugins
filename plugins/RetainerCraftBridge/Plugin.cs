using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace RetainerCraftBridge;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; }
    public int RecipeId { get; set; }
    public int CraftCount { get; set; } = 1;
    public ulong FirstCharacterCid { get; set; }
    public ulong CraftCharacterCid { get; set; }
    public bool RestockMaterials { get; set; }
    public MaterialDelays MaterialDelays { get; set; } = new();
    public Dictionary<int, CraftMode> RecipeModes { get; set; } = [];
    public bool ArtisanAutoModeConfirmed { get; set; }
    public CraftDelays CraftDelays { get; set; } = new();
}

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly PublicIpcHost host;
    private readonly BridgeMachine machine;
    private readonly MaterialRestockService materials;
    private readonly NativeCraftService craftSession;
    private readonly OwnedUiSuppression uiSuppression;
    private readonly Configuration config;
    private bool visible;
    private volatile bool disposed;
    private int operationPending;
    private int stopGeneration;
    private int cancelRequested;
    private string message = "尚未检查依赖。";
    private string search = "";
    private List<(Recipe Recipe, string Label)>? recipes;
    private List<CharacterSnapshot> characters = [];
    private sealed record UiState(bool Running, bool Blocking, string Status, string Detail);
    private UiState view = new(false, false, "未开始", "尚未开始。");
    private long nextTick;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, ICommandManager commands,
        IClientState client, IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gameGui, IDataManager data, IPluginLog log,
        ITargetManager targets, IAddonLifecycle lifecycle)
    {
        pi = pluginInterface; this.framework = framework; this.commands = commands; this.data = data; this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new();
        config.MaterialDelays ??= new();
        config.RecipeModes ??= [];
        config.CraftDelays ??= new();
        uiSuppression = new(pi, framework);
        craftSession = new(framework, player, condition, data, lifecycle, log) { Delays = config.CraftDelays };
        host = new(pi, client, player, objects, condition, gameGui, craftSession);
        materials = new(framework, player, objects, targets, condition, gameGui, data, lifecycle, log) { Delays = config.MaterialDelays };
        machine = new(host, materials);
        pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask").Subscribe(machine.OnRetainerAdditional);
        pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess").Subscribe(machine.OnRetainerReady);
        pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask").Subscribe(machine.OnCharacterAdditional);
        pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess").Subscribe(machine.OnCharacterReady);
        commands.AddHandler("/retainercraft", new CommandInfo(OnCommand) { HelpMessage = "打开雇员收取后制作；/retainercraft cancel 停止循环" });
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenMainUi += Open; pi.UiBuilder.OpenConfigUi += Open;
        framework.Update += Update;
    }
    private void Open() => visible = true;
    private void OnCommand(string command, string args)
    {
        if(args.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase)) QueueCancel();
        visible = true;
    }
    private void Queue(System.Action action)
    {
        if(disposed || Interlocked.CompareExchange(ref operationPending, 1, 0) != 0) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            try { if(!disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread) action(); }
            catch(Exception ex) { message = ex.Message; log.Error(ex, "RetainerCraftBridge operation failed"); }
            finally { Volatile.Write(ref operationPending, 0); }
        });
    }
    private void QueueCancel()
    {
        // Cancel cannot be discarded behind a pending Start/read operation.
        Interlocked.Increment(ref stopGeneration);
        Interlocked.Exchange(ref cancelRequested, 1);
        message = "已收到停止请求；停止后续轮换和制作，等待本插件当前操作收尾。";
        if(disposed) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
            ApplyQueuedCancel();
        });
    }
    private void ApplyQueuedCancel()
    {
        if(Interlocked.Exchange(ref cancelRequested, 0) == 0) return;
        try { machine.Cancel(); }
        catch(Exception ex) { message = "停止请求失败：" + ex.Message; log.Error(ex, "RetainerCraftBridge cancel failed"); }
    }
    private void Update(IFramework _)
    {
        if(disposed) return;
        ApplyQueuedCancel();
        if(machine.Blocking || materials.Blocking || craftSession.Blocking)
        {
            if(!uiSuppression.TryAcquire(out var suppressionError))
            {
                // Stop intent is managed-only. No native queue may advance without the cooperative lock.
                craftSession.Stop(); materials.Cancel(); machine.Fail(suppressionError);
                message = suppressionError;
                Volatile.Write(ref view, new(machine.Running, machine.Blocking, "已停止，等待界面协作暂停", machine.Detail));
                return;
            }
        }
        else uiSuppression.Release();
        // Own native queues must observe every frame, including when a fast craft finishes between bridge polls.
        craftSession.Tick();
        // Detect the short between-crafts window every frame; native rotation needs less frequent polling.
        if(machine.State is not (BridgeState.Crafting or BridgeState.StoppingCraft or BridgeState.CraftPending)
            && machine.Running && Environment.TickCount64 < nextTick) return;
        nextTick = Environment.TickCount64 + 100;
        machine.Tick();
        var detail = machine.Detail;
        if(host.UsesLocalCraftQueue && machine.State is BridgeState.CraftPending or BridgeState.Crafting or BridgeState.StoppingCraft or BridgeState.Cancelled or BridgeState.Failed)
            detail += " " + craftSession.Detail;
        Volatile.Write(ref view, new(machine.Running, machine.Blocking,
            $"{StateLabel(machine.State)} · 第 {machine.Cycle} 次轮转 · 制作角色收取确认 {machine.Successful}/{machine.Total}", detail));
    }
    private void QueueStart()
    {
        // Freeze UI values before dispatch; no queued closure reads mutable form state.
        var enabled = config.Enabled; var first = config.FirstCharacterCid; var crafter = config.CraftCharacterCid;
        var recipeId = config.RecipeId; var count = config.CraftCount; var restock = config.RestockMaterials;
        var mode = config.RecipeModes.GetValueOrDefault(recipeId, CraftMode.Standard);
        var autoModeConfirmed = config.ArtisanAutoModeConfirmed;
        var generation = Volatile.Read(ref stopGeneration);
        Queue(() =>
        {
            if(generation != Volatile.Read(ref stopGeneration)) { message = "待执行的开始请求已取消。"; return; }
            if(!enabled) throw new InvalidOperationException("请先启用循环。");
            if(recipeId is <= 0 or > ushort.MaxValue || count is < 1 or > 9999
                || !data.GetExcelSheet<Recipe>().TryGetRow((uint)recipeId, out var recipe) || recipe.ItemResult.RowId == 0 || recipe.Number == 0)
                throw new InvalidOperationException("请选择标准制作配方，制作次数须为 1–9999；首版不支持星海制作配方。");
            var plan = host.PrepareCycle(first, crafter);
            if(!Enum.IsDefined(mode)) throw new InvalidOperationException("请选择有效制作方式。");
            if(mode == CraftMode.Standard && !autoModeConfirmed)
                throw new InvalidOperationException("标准制作需要先在 Artisan 开启自动模式，并在本界面确认；不会自动切换全局开关。");
            if(mode == CraftMode.Quick && !recipe.CanQuickSynth)
                throw new InvalidOperationException("此配方不支持简易制作，请选择标准制作。");
            if(generation != Volatile.Read(ref stopGeneration)) { message = "待执行的开始请求已取消。"; return; }
            host.RequestedCraftMode = mode;
            if(!uiSuppression.TryAcquire(out var suppressionError)) throw new InvalidOperationException(suppressionError);
            machine.Start(plan, (ushort)recipeId, count, restock);
            message = "循环已启动。开头与制作角色的选择在本次运行中固定；其他角色与顺序仍使用 AR 原有设置。";
        });
    }
    private void Draw()
    {
        if(!visible || disposed) return;
        var state = Volatile.Read(ref view);
        ImGui.SetNextWindowSize(new Vector2(700, 700), ImGuiCond.FirstUseEver);
        if(!ImGui.Begin("雇员收取后制作", ref visible)) { ImGui.End(); return; }
        try
        {
            if(state.Blocking || Volatile.Read(ref operationPending) != 0)
            {
                // Keep stopping available above all disabled settings and during cleanup.
                if(ImGui.Button(state.Running ? "停止循环" : "再次请求停止与清理")) QueueCancel();
                ImGui.SameLine();
                ImGui.TextWrapped(state.Running ? "也可输入 /retainercraft cancel" : "循环已停止，仍在等待当前操作退出；不会自动恢复 AR。");
            }
            ImGui.TextWrapped("从开头角色启动 AR 原生轮换 → 指定制作角色收完雇员后停止轮换并制作 → 开头角色的雇员再次到期后，安全停工并回到开头，再开启 AR。不会计算全账号是否完成一轮。");
            ImGui.TextWrapped("开始前请关闭 AR 多角色模式，将范围设为 Retainers（仅雇员），结束其他自动流程。此循环独占 AR/Artisan/Lifestream；不要同时运行其他收尾插件或手动换角、改配方。");
            ImGui.TextWrapped("运行及清理期间通过公开协作接口暂停 YesAlready 自动确认，结束后只释放本插件自己的暂停请求。");
            if(ImGui.CollapsingHeader("AR 的 Retainers（仅雇员）在哪里设置？"))
            {
                ImGui.TextWrapped(PublicIpcHost.RetainerModeHelp);
                ImGui.TextWrapped("Retainers 只处理雇员；Submersibles 处理潜艇；Everything 同时处理两者。指定角色收完雇员就转制作，因此本循环要求 Retainers。");
                if(ImGui.Button("打开 AR 设置")) Queue(() => commands.ProcessCommand("/ays settings"));
            }
            ImGui.Separator();
            ImGui.BeginDisabled(state.Blocking || Volatile.Read(ref operationPending) != 0);
            var enabled = config.Enabled;
            var changed = ImGui.Checkbox("启用循环（仍需手动点击开始）", ref enabled); config.Enabled = enabled;
            if(ImGui.Button("读取/刷新角色列表"))
                Queue(() => { characters = host.RegisteredCharacters(); message = $"已读取 {characters.Count} 个角色。"; });
            var firstCid = config.FirstCharacterCid; var craftCid = config.CraftCharacterCid;
            changed |= DrawCharacterPicker("开头角色", ref firstCid);
            changed |= DrawCharacterPicker("制作角色（其收取结束作为边界）", ref craftCid);
            config.FirstCharacterCid = firstCid; config.CraftCharacterCid = craftCid;
            ImGui.TextWrapped("两个角色可以相同，均须在 AR 中启用雇员轮换且有已勾选、执行中的探险。其余角色按 AR 的原生顺序运行；不会代替 AR 检查全部角色是否收齐。");
            recipes ??= data.GetExcelSheet<Recipe>().Where(x => x.ItemResult.RowId != 0 && x.RowId <= ushort.MaxValue && x.Number != 0)
                .Select(x => (x, $"{x.ItemResult.Value.Name} · {x.CraftType.Value.Name} · 配方 {x.RowId}")).ToList();
            var selected = recipes.FirstOrDefault(x => x.Recipe.RowId == config.RecipeId);
            ImGui.SetNextItemWidth(-1);
            if(ImGui.BeginCombo("配方", selected.Label ?? "请选择配方"))
            {
                ImGui.InputText("搜索物品、职业或配方编号", ref search, 100);
                var shown = 0;
                foreach(var entry in recipes)
                {
                    if(search.Length > 0 && !entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    if(shown++ >= 150) { ImGui.TextDisabled("请继续输入以缩小结果。"); break; }
                    if(ImGui.Selectable(entry.Label, entry.Recipe.RowId == config.RecipeId)) { config.RecipeId = (int)entry.Recipe.RowId; changed = true; }
                }
                ImGui.EndCombo();
            }
            var count = config.CraftCount;
            changed |= ImGui.InputInt("每个制作时段的次数上限（1–9999）", ref count); config.CraftCount = count;
            if(selected.Recipe.RowId != 0)
            {
                ImGui.TextWrapped($"最多预计产出 {Math.Max(0L, count) * selected.Recipe.AmountResult} 个，每次 {selected.Recipe.AmountResult} 个。次数是制作次数；开头角色到期可能提前结束。本时段自然结束后仍继续监视开头。");
                ImGui.TextWrapped(selected.Recipe.CanQuickSynth
                    ? "此配方支持简易制作；该角色还须已完成过此配方。"
                    : "此配方不支持简易制作，请选择标准制作。");
            }
            var mode = config.RecipeModes.GetValueOrDefault(config.RecipeId, CraftMode.Standard);
            if(ImGui.BeginCombo("此配方的制作方式", ModeLabel(mode)))
            {
                foreach(var option in new[] { CraftMode.Standard, CraftMode.Quick, CraftMode.Inherit })
                    if(ImGui.Selectable(ModeLabel(option), mode == option))
                    { mode = option; config.RecipeModes[config.RecipeId] = option; changed = true; }
                ImGui.EndCombo();
            }
            if(mode == CraftMode.Inherit)
                ImGui.TextWrapped("沿用 Artisan 全局简易制作设置。此兼容方式不能撤回尚未接收的私有请求，停止时可能需手动清理 Artisan。标准/简易两种明确方式使用本插件可取消的队列。");
            else
            {
                ImGui.TextWrapped("此方式由桥接器安排次数与停止；请让制作角色穿好装备并处于对应制作职业。不会代执行 Artisan 连续制作的换装、食药、修理等预处理。");
                if(mode == CraftMode.Standard)
                {
                    var confirmed = config.ArtisanAutoModeConfirmed;
                    changed |= ImGui.Checkbox("我已在 Artisan 开启自动模式（标准制作必需）", ref confirmed);
                    config.ArtisanAutoModeConfirmed = confirmed;
                    ImGui.TextWrapped("使用 Artisan 当前求解器或宏完成普通工艺，不保证 HQ；停止后不再开下一件，当前工艺需完成或手动结束。");
                }
                else ImGui.TextWrapped("每批最多 99 次。停止时撤销后续批次，并请求结束当前简易制作；不会回退成标准制作。");
                if(ImGui.CollapsingHeader("制作各步骤延迟（毫秒）"))
                {
                    var delay = config.CraftDelays;
                    bool InputCraftDelay(string label, int current, Action<int> assign)
                    {
                        if(!ImGui.InputInt(label, ref current)) return false;
                        assign(Math.Clamp(current, 100, 30000)); return true;
                    }
                    changed |= InputCraftDelay("打开本次配方", delay.OpenRecipeMs, v => delay.OpenRecipeMs = v);
                    changed |= InputCraftDelay("选择制作材料", delay.FillMaterialsMs, v => delay.FillMaterialsMs = v);
                    changed |= InputCraftDelay("开始一次制作", delay.StartCraftMs, v => delay.StartCraftMs = v);
                    changed |= InputCraftDelay("提交简易制作次数", delay.SubmitQuickMs, v => delay.SubmitQuickMs = v);
                    changed |= InputCraftDelay("取消本次简易制作", delay.CancelQuickMs, v => delay.CancelQuickMs = v);
                    changed |= InputCraftDelay("退出本次配方", delay.CloseRecipeMs, v => delay.CloseRecipeMs = v);
                    changed |= InputCraftDelay("两次制作之间", delay.BetweenCraftsMs, v => delay.BetweenCraftsMs = v);
                    var timeout = delay.TimeoutSeconds;
                    if(ImGui.InputInt("制作每步响应超时（秒）", ref timeout))
                    { delay.TimeoutSeconds = Math.Clamp(timeout, 15, 600); changed = true; }
                }
            }
            var restock = config.RestockMaterials;
            changed |= ImGui.Checkbox("制作前从雇员取出缺少的材料", ref restock); config.RestockMaterials = restock;
            if(restock)
            {
                ImGui.TextWrapped("按本时段的全部制作次数准备材料与水晶；只从制作角色自己的雇员取出。需要留在可交互的雇员铃旁并有足够背包空间。材料不足会停止，不会自行降低次数或反复提交制作。");
                if(ImGui.CollapsingHeader("取料各步骤延迟（毫秒）"))
                {
                    var delay = config.MaterialDelays;
                    bool InputDelay(string label, int current, Action<int> assign)
                    {
                        if(!ImGui.InputInt(label, ref current)) return false;
                        assign(Math.Clamp(current, 100, 30000)); return true;
                    }
                    changed |= InputDelay("交互雇员铃", delay.BellMs, v => delay.BellMs = v);
                    changed |= InputDelay("选择雇员", delay.SelectRetainerMs, v => delay.SelectRetainerMs = v);
                    changed |= InputDelay("打开雇员仓库", delay.OpenInventoryMs, v => delay.OpenInventoryMs = v);
                    changed |= InputDelay("逐格取出物品", delay.WithdrawMs, v => delay.WithdrawMs = v);
                    changed |= InputDelay("提交取出数量", delay.SubmitQuantityMs, v => delay.SubmitQuantityMs = v);
                    changed |= InputDelay("关闭本次界面", delay.CloseUiMs, v => delay.CloseUiMs = v);
                    var timeout = delay.TimeoutSeconds;
                    if(ImGui.InputInt("取料每步响应超时（秒）", ref timeout))
                    { delay.TimeoutSeconds = Math.Clamp(timeout, 15, 600); changed = true; }
                    ImGui.TextWrapped("各动作串行执行，至少间隔 100 毫秒。取料设置在本次准备开始时固定；停止和退出也遵守动作间隔。");
                }
            }
            if(ImGui.CollapsingHeader("到期判定说明"))
            {
                ImGui.TextWrapped("只检查开头角色的已选雇员，沿用 AR 当前 MultiWaitForAll（任一/全部）与 AdvanceTimer 提前窗口。不会等待全账号雇员。AR 隐藏的一次性 Synchronize 状态没有公开 getter；运行前请取消该同步模式，本插件无法复刻它。");
            }
            if(changed) pi.SavePluginConfig(config);
            if(ImGui.Button("检查依赖与角色条件"))
            {
                var first = config.FirstCharacterCid; var crafter = config.CraftCharacterCid;
                Queue(() => { host.PrepareCycle(first, crafter); message = "依赖与当前条件通过；" + host.ReadinessDescription + "。尚未执行游戏动作。"; });
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(!config.Enabled || config.FirstCharacterCid == 0 || config.CraftCharacterCid == 0 || config.RecipeId <= 0 || count is < 1 or > 9999);
            if(ImGui.Button("开始循环")) QueueStart();
            ImGui.EndDisabled(); ImGui.EndDisabled();
            ImGui.Separator();
            ImGui.TextWrapped("状态：" + state.Status);
            ImGui.TextWrapped(state.Detail);
            ImGui.TextWrapped(message);
            ImGui.TextWrapped("停止会撤销后续动作；标准制作等待当前工艺完成或手动结束，明确简易模式请求取消当前批次。已发请求需等待结果及界面收尾，期间保持 AR 关闭。停止后不会恢复旧制作；已提交的登出、登录、传送不能撤回。请先停止循环、等待空闲再卸载插件。");
        }
        catch(Exception ex) { log.Error(ex, "RetainerCraftBridge UI failed"); }
        finally { ImGui.End(); }
    }
    private bool DrawCharacterPicker(string label, ref ulong cid)
    {
        var currentCid = cid;
        var selected = characters.FirstOrDefault(x => x.CID == currentCid);
        var preview = selected == null ? (cid == 0 ? "请选择角色" : "已保存，请刷新列表核对") : $"{selected.Name}@{selected.World}";
        var changed = false;
        ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo(label, preview))
        {
            foreach(var character in characters)
                if(ImGui.Selectable($"{character.Name}@{character.World}##{character.CID}", cid == character.CID)) { cid = character.CID; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }
    private static string ModeLabel(CraftMode mode) => mode switch
    {
        CraftMode.Standard => "标准制作（Artisan 自动模式）", CraftMode.Quick => "简易制作", _ => "跟随 Artisan 设置（兼容方式）"
    };
    private static string StateLabel(BridgeState state) => state switch
    {
        BridgeState.Idle => "未开始", BridgeState.StartingCycle => "准备开头角色", BridgeState.TravellingToFirst => "切换到开头",
        BridgeState.Retainers => "AR 原生轮换", BridgeState.DrainingRetainers => "制作角色收尾", BridgeState.CraftPending => "等待制作开始",
        BridgeState.PreparingMaterials => "准备制作材料", BridgeState.StoppingMaterials => "等待取料收尾",
        BridgeState.Crafting => "制作并监视开头", BridgeState.StoppingCraft => "等待安全停工", BridgeState.WaitingForFirst => "等待开头到期",
        BridgeState.Failed => "失败（检查清理状态）", BridgeState.Cancelled => "已请求停止", _ => "未知",
    };
    public void Dispose()
    {
        disposed = true;
        Interlocked.Increment(ref stopGeneration);
        try
        {
            var warning = machine.DisposeStop();
            if(warning != null) log.Warning(warning);
        }
        catch(Exception ex) { log.Error(ex, "Unload cleanup could not finish; check AutoRetainer / Artisan manually"); }
        finally
        {
            try { materials.Dispose(); }
            catch(Exception ex) { log.Error(ex, "Material service disposal failed; continuing plugin event cleanup"); }
            try { craftSession.Dispose(); }
            catch(Exception ex) { log.Error(ex, "Craft session disposal failed; continuing plugin event cleanup"); }
            try { uiSuppression.Dispose(); }
            catch(Exception ex) { log.Error(ex, "UI suppression cleanup failed; continuing plugin event cleanup"); }
            framework.Update -= Update;
            pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= Open; pi.UiBuilder.OpenConfigUi -= Open;
            commands.RemoveHandler("/retainercraft");
            pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask").Unsubscribe(machine.OnRetainerAdditional);
            pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess").Unsubscribe(machine.OnRetainerReady);
            pi.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask").Unsubscribe(machine.OnCharacterAdditional);
            pi.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess").Unsubscribe(machine.OnCharacterReady);
        }
    }
}

internal sealed unsafe class PublicIpcHost(IDalamudPluginInterface pi, IClientState client, IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gameGui, NativeCraftService craftSession) : IBridgeHost
{
    public const string RetainerModeHelp = "输入 /ays settings → 高级设置 → 专家设置 → 勾选“显示多角色模式类型选择器”；返回 /ays 主窗口，在“多角色”旁的下拉框选择 Retainers。";
    private long readinessAt;
    private CyclePlan? readinessPlan;
    private bool readinessCache;
    public CraftMode RequestedCraftMode { get; set; }
    private bool usesLocalCraftQueue;
    private ushort localRecipe;
    public string ReadinessDescription { get; private set; } = "";
    public long Now => Environment.TickCount64;
    public ulong CurrentCid => player.ContentId;
    public bool LoggedIn => client.IsLoggedIn;
    public bool ArBusy => Get<bool>("AutoRetainer.PluginState.IsBusy");
    public bool NavigationBusy => Get<bool>("Lifestream.IsBusy");
    public bool ArMultiEnabled => Get<bool>("AutoRetainer.GetMultiModeEnabled");
    public bool ArtisanEndurance => usesLocalCraftQueue ? craftSession.RepeatEnabled : Get<bool>("Artisan.GetEnduranceStatus");
    public bool CraftRequestAccepted => usesLocalCraftQueue ? craftSession.Accepted : ArtisanEndurance;
    public bool CanStopCraftNow => usesLocalCraftQueue || SafeCraftBoundary || !ActualCraftActive || Visible("SynthesisSimple");
    public bool CraftHasStarted => usesLocalCraftQueue ? craftSession.HasStarted : ActualCraftActive;
    public string? CraftError => usesLocalCraftQueue && craftSession.Failed ? craftSession.Detail : null;
    public bool UsesLocalCraftQueue => usesLocalCraftQueue;
    public bool ArtisanForeignControl => Get<bool>("Artisan.IsListRunning") || Get<bool>("Artisan.GetStopRequest")
        || (usesLocalCraftQueue && Get<bool>("Artisan.GetEnduranceStatus"));
    public bool ArtisanIdle => !Get<bool>("Artisan.IsBusy") && !craftSession.Blocking && !ArtisanEndurance && !ArtisanForeignControl;
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
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("RecipeNote").Address;
        var args = stackalloc AtkValue[1];
        args[0] = new AtkValue { Type = AtkValueType.Int, Int = -1 };
        addon->FireCallback(1, args, true);
        return true;
    }
    private T Get<T>(string name) => pi.GetIpcSubscriber<T>(name).InvokeFunc();
    private void Act(string name) => pi.GetIpcSubscriber<object>(name).InvokeAction();
    public CharacterSnapshot? ReadCharacter(ulong cid) => pi.GetIpcSubscriber<ulong, CharacterSnapshot?>("AutoRetainer.GetOfflineCharacterData").InvokeFunc(cid);
    public void EnableMultiMode() => pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled").InvokeAction(true);
    public void DisableArFunctions() => Act("AutoRetainer.PluginState.DisableAllFunctions");
    public void AbortArTasks() => Act("AutoRetainer.PluginState.AbortAllTasks");
    public void AbortNavigation() => Act("Lifestream.Abort");
    public void Relog(string name, string world)
    {
        var result = pi.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter").InvokeFunc(name, world);
        if(result != 0) throw new InvalidOperationException($"Lifestream 换角被拒绝（错误码 {result}），不会重复提交。");
    }
    public void RequestRetainer(string owner) => pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess").InvokeAction(owner);
    public void FinishRetainer() => Act("AutoRetainer.FinishPostprocessRequest");
    public void RequestCharacter(string owner) => pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess").InvokeAction(owner);
    public void FinishCharacter() => Act("AutoRetainer.FinishCharacterPostprocessRequest");
    public void Craft(ushort recipe, int count)
    {
        usesLocalCraftQueue = RequestedCraftMode != CraftMode.Inherit;
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
        var character = ReadCharacter(cid) ?? throw new InvalidOperationException("制作角色资料不可用。");
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
        if(!sentinel.Enabled || sentinel.Excluded || sentinel.Remaining.Count == 0)
            throw new InvalidOperationException("开头角色已禁用、排除或不再有已选执行中探险；循环暂停。");
        readinessCache = ReadinessRules.Ready(sentinel, timing.AdvanceTimer, timing.MultiWaitForAll);
        readinessAt = Now + 1000; readinessPlan = plan;
        ReadinessDescription = $"开头判定：{(timing.MultiWaitForAll ? "全部" : "任一")}已选探险进入 {timing.AdvanceTimer} 秒提前窗口";
        return readinessCache;
    }
    public CyclePlan PrepareCycle(ulong firstCid, ulong craftCid)
    {
        Act("AutoRetainer.Init");
        var registered = RegisteredCharacters();
        var first = registered.FirstOrDefault(x => x.CID == firstCid) ?? throw new InvalidOperationException("请选择有效开头角色。");
        var crafting = registered.FirstOrDefault(x => x.CID == craftCid) ?? throw new InvalidOperationException("请选择有效制作角色。");
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
            || !pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem").HasAction
            || !pi.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus").HasAction
            || !pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess").HasAction
            || !pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess").HasAction
            || !pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled").HasAction)
            throw new InvalidOperationException("依赖插件缺少本流程需要的公开接口。");
        var selected = Selection();
        foreach(var character in new[] { first, crafting })
            if(!character.Enabled || character.ExcludeRetainer || TimedRetainers(character, selected).Length == 0)
                throw new InvalidOperationException("开头与制作角色均须启用雇员轮换，且有已勾选的执行中探险。");
        var plan = new CyclePlan(new(first.CID, first.Name, first.World), new(crafting.CID, crafting.Name, crafting.World));
        readinessAt = 0; FirstReady(plan);
        return plan;
    }
}
