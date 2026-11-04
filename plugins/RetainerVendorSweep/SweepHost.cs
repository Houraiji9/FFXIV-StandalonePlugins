using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerVendorSweep;

// Public IPC DTO: Dalamud serializes the foreign object. No AR binary/private reflection.
public sealed class CharacterCache
{
    public ulong CID { get; set; }
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
}

internal sealed unsafe class SweepHost(IDalamudPluginInterface pi, IClientState client,
    IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gui) : ISweepHost
{
    private ulong requestedCid;
    public ulong CurrentCid => player.ContentId;
    public bool LoggedIn => client.IsLoggedIn && player.IsLoaded && objects.LocalPlayer != null;
    public bool IsSwitching
    {
        get
        {
            var busy = Required<bool>("Lifestream.IsBusy");
            if(!busy && LoggedIn && CurrentCid == requestedCid) requestedCid = 0;
            return busy;
        }
    }
    private bool Visible(string name)
    {
        var addon = (AtkUnitBase*)gui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible;
    }
    private static readonly ConditionFlag[] Occupied =
    [ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.OccupiedInEvent,
     ConditionFlag.OccupiedInQuestEvent, ConditionFlag.Occupied33, ConditionFlag.OccupiedInCutSceneEvent,
     ConditionFlag.Occupied38, ConditionFlag.Occupied39, ConditionFlag.OccupiedSummoningBell,
     ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut,
     ConditionFlag.InCombat, ConditionFlag.Casting, ConditionFlag.Crafting,
     ConditionFlag.PreparingToCraft, ConditionFlag.Gathering];
    public bool SafeForSwitch => LoggedIn && !Occupied.Any(x => condition[x])
        && !new[] { "RetainerList", "InventoryRetainer", "InventoryRetainerLarge", "Talk", "InputNumeric",
            "ContextMenu", "Trade", "SelectString", "SelectYesno", "RetainerTaskResult", "RetainerTaskAsk",
            "RecipeNote", "Synthesis", "SynthesisSimple", "SynthesisSimpleDialog", "Shop" }.Any(Visible);

    private T Required<T>(string endpoint)
    {
        var ipc = pi.GetIpcSubscriber<T>(endpoint);
        if(!ipc.HasFunction) throw new InvalidOperationException("依赖接口不可用：" + endpoint);
        return ipc.InvokeFunc();
    }
    private bool Loaded(string name) => pi.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == name);

    public string? Conflict()
    {
        try
        {
            if(!Loaded("AutoRetainer") || !Loaded("Lifestream")) return "请先启用 AutoRetainer 与 Lifestream。";
            if(!pi.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter").HasFunction)
                return "Lifestream 缺少公开换角接口。";
            if(Required<bool>("AutoRetainer.GetMultiModeEnabled")) return "请先关闭 AR 多角色轮换。";
            if(Required<bool>("AutoRetainer.GetSuppressed")) return "AR 已被其他流程占用或抑制。";
            foreach(var (name, label) in new[] { ("RetainerSense", "雇员感知"), ("OldRetainerSense", "旧雇员感知"),
                ("FullAutoGCDelivery", "自动军票交付"), ("NightMode", "夜间模式"),
                ("MultiAutoStart", "游戏启动时自动多角色"), ("MultiOnPluginLoad", "插件加载时自动多角色") })
                if(Required<bool>("AutoRetainer.GetConfig." + name)) return "请先关闭 AR 的“" + label + "”。";
            // 2 == Disable_AutoRetainer, verified in AutoRetainerAPI.Configuration.OpenBellBehavior.
            // Do_nothing preserves a previously enabled scheduler; Pause can restore it later.
            if(Required<int>("AutoRetainer.GetConfig.OpenBellBehaviorNoVentures") != 2 ||
                Required<int>("AutoRetainer.GetConfig.OpenBellBehaviorWithVentures") != 2)
                return "请在 AR 行为设置中将有/无可用派遣任务时访问雇员铃的操作均设为 Disable AutoRetainer（禁用自动收取）。";
            // AR IsBusy also includes Lifestream. A switch submitted by this host is expected busy.
            if(requestedCid == 0 && Required<bool>("AutoRetainer.PluginState.IsBusy")) return "AR 或 Lifestream 正忙，请等待空闲。";
            if(Loaded("Artisan"))
                foreach(var endpoint in new[] { "Artisan.IsBusy", "Artisan.IsListRunning", "Artisan.GetEnduranceStatus" })
                    if(Required<bool>(endpoint)) return "请先停止 Artisan 制作队列。";
            // The existing bridge has no read-only running IPC. Loaded is not proof of idle.
            if(Loaded("RetainerCraftBridge")) return "当前制作桥接器未提供运行状态接口；本轮直售前请暂时禁用“雇员收取后制作”。";
            return null;
        }
        catch(Exception ex) { return "依赖检查失败：" + ex.Message; }
    }

    public IReadOnlyList<VendorCharacter> ReadCharacters()
    {
        var ids = Required<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
        var reader = pi.GetIpcSubscriber<ulong, CharacterCache?>("AutoRetainer.GetOfflineCharacterData");
        if(!reader.HasFunction) throw new InvalidOperationException("AR 角色缓存读取接口不可用。");
        var result = new List<VendorCharacter>();
        foreach(var id in ids.Distinct())
        {
            var row = reader.InvokeFunc(id);
            if(id == 0 || row == null || row.CID != id || string.IsNullOrWhiteSpace(row.Name)
                || row.Name == "Unknown" || string.IsNullOrWhiteSpace(row.World))
                throw new InvalidOperationException("AR 角色缓存不完整，请在 AR 中刷新角色资料后重试。");
            result.Add(new(id, row.Name, row.World));
        }
        if(result.Count == 0) throw new InvalidOperationException("AR 尚未登记可用角色；请先更新角色缓存。");
        return result;
    }

    public void ChangeCharacter(VendorCharacter character)
    {
        if(!SafeForSwitch || IsSwitching) throw new CharacterSwitchRejectedException("角色或界面尚未空闲，未提交换角。");
        var conflict = Conflict();
        if(conflict != null) throw new CharacterSwitchRejectedException(conflict);
        requestedCid = character.Cid;
        try
        {
            var result = pi.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter")
                .InvokeFunc(character.Name, character.World);
            if(result != 0) throw new CharacterSwitchRejectedException($"Lifestream 拒绝换角（{result}），不会重复提交。");
        }
        catch
        {
            // Do not retain permission to disregard AR busy after an uncertain IPC failure.
            // SweepMachine independently retains the outstanding request and does not retry it.
            requestedCid = 0;
            throw;
        }
    }

    public void PrepareCharacter()
    {
        if(!SafeForSwitch || IsSwitching) throw new InvalidOperationException("本角色尚未空闲，未打开雇员铃。");
        if(Conflict() is { } conflict) throw new InvalidOperationException(conflict);
        var stop = pi.GetIpcSubscriber<object>("AutoRetainer.PluginState.DisableAllFunctions");
        if(!stop.HasAction) throw new InvalidOperationException("AR 缺少公开停止运行接口，不能取得本轮雇员铃控制。");
        // Runtime state only: clear an old Artisan scheduler reason before bell access. No configuration edits.
        stop.InvokeAction();
    }
}
