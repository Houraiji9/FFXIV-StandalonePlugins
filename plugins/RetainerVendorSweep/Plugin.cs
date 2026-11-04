using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace RetainerVendorSweep;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public SaleRules Rules { get; set; } = new();
    public VendorDelays Delays { get; set; } = new();
    public List<ulong> CharacterOrder { get; set; } = [];
    public HashSet<ulong> ExcludedCharacters { get; set; } = [];
}

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly SweepHost host;
    private readonly NativeSaleService sales;
    private readonly SweepMachine machine;
    private readonly OwnedUiSuppression suppression;
    private readonly Dictionary<uint, CatalogItem> catalog;
    private readonly CatalogItem[] orderedCatalog;
    private readonly Dictionary<uint, string> categories;
    private IReadOnlyList<VendorCharacter> characters = [];
    private UiView view = new(false, false, "尚未开始。", []);
    private Preview? preview;
    private PendingStart? confirmation;
    private string message = "先刷新角色清单，再选择出售规则。";
    private string? configurationIssue;
    private string search = "", protectSearch = "";
    private uint category;
    private bool visible, showSelectedOnly;
    private volatile bool disposed;
    private int pendingOperation, stopGeneration, stopRequested;
    private long rulesRevision;
    private (uint Category, string Search, bool Selected, long Revision)? filterKey;
    private CatalogItem[] filteredItems = [];
    private string? runToken, lastSavedState;
    private sealed record CatalogItem(uint Id, string Name, uint Category, uint Price);
    private sealed record UiView(bool Running, bool Blocking, string Detail, string[] Results);
    private sealed record Preview(ulong Cid, string Detail, string[] Lines);
    private sealed record PendingStart(VendorCharacter[] Characters, SaleRules Rules, VendorDelays Delays, int Generation);

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IClientState client, IPlayerState player, IObjectTable objects, ICondition condition, IGameGui gameGui,
        IDataManager data, IPluginLog log, ITargetManager targets, IAddonLifecycle lifecycle)
    {
        pi = pluginInterface; this.commands = commands; this.framework = framework;
        this.player = player; this.data = data; this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new();
        if(config.Version != 1 || config.Rules == null || config.Rules.ItemIds == null || config.Rules.CategoryIds == null
            || config.Rules.ProtectedItemIds == null || config.Rules.CategoryProtectedItemIds == null
            || config.Rules.CategoryProtectedItemIds.Values.Any(x => x == null))
            configurationIssue = "保存的规则不完整或版本不兼容，不能保证保护名单；请清空规则后重新设置。";
        config.Rules ??= new(); config.Delays ??= new(); config.CharacterOrder ??= [];
        config.ExcludedCharacters ??= [];
        config.Rules.ItemIds ??= []; config.Rules.CategoryIds ??= []; config.Rules.ProtectedItemIds ??= [];
        config.Rules.CategoryProtectedItemIds ??= [];
        foreach(var id in config.Rules.CategoryProtectedItemIds.Keys.ToArray())
            config.Rules.CategoryProtectedItemIds[id] ??= [];
        config.Delays = config.Delays.Freeze();
        categories = data.GetExcelSheet<ItemUICategory>().Where(x => x.RowId != 0)
            .ToDictionary(x => x.RowId, x => x.Name.ExtractText());
        catalog = data.GetExcelSheet<Item>().Where(x => x.RowId != 0 && x.PriceLow > 0 && x.ItemUICategory.RowId != 0)
            .Select(x => new CatalogItem(x.RowId, x.Name.ExtractText(), x.ItemUICategory.RowId, x.PriceLow))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Id);
        orderedCatalog = catalog.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        host = new(pi, client, player, objects, condition, gameGui);
        sales = new(framework, player, objects, targets, condition, gameGui, data, lifecycle, log);
        sales.CheckConflict = host.Conflict;
        machine = new(host, sales);
        suppression = new(pi, framework);
        commands.AddHandler("/retainersell", new CommandInfo(OnCommand) { HelpMessage = "多角色雇员直售；/retainersell stop 停止本轮。" });
        pi.GetIpcProvider<bool>("RetainerVendorSweep.IsBusy").RegisterFunc(() => Volatile.Read(ref view).Blocking || Volatile.Read(ref pendingOperation) != 0);
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenConfigUi += Open; pi.UiBuilder.OpenMainUi += Open;
        framework.Update += Update;
    }

    private static long Now => Environment.TickCount64;
    private void Open() => visible = true;
    private void OnCommand(string command, string args) { if(args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) QueueStop(); Open(); }
    private void Save() { pi.SavePluginConfig(config); preview = null; rulesRevision++; }
    private void Queue(System.Action action)
    {
        if(disposed || Interlocked.CompareExchange(ref pendingOperation, 1, 0) != 0) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            try { if(!disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread) action(); }
            catch(Exception ex) { message = ex.Message; log.Error(ex, "RetainerVendorSweep command failed"); }
            finally
            {
                Volatile.Write(ref pendingOperation, 0);
                if(!disposed && !framework.IsFrameworkUnloading && framework.IsInFrameworkUpdateThread) Publish();
            }
        });
    }
    private void QueueStop()
    {
        Interlocked.Increment(ref stopGeneration);
        Interlocked.Exchange(ref stopRequested, 1);
        confirmation = null;
        message = "已收到停止请求，撤销后续出售与换角；已发送的请求等待结果。";
        // Not subject to pendingOperation: a queued refresh/start must never swallow Stop.
        if(!disposed) _ = framework.RunOnFrameworkThread(ApplyStop);
    }
    private void ApplyStop()
    {
        if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread
            || Interlocked.Exchange(ref stopRequested, 0) == 0) return;
        machine.Stop(Now);
    }
    private void Publish()
    {
        Volatile.Write(ref view, new(machine.Running, machine.Blocking || sales.Blocking, machine.Detail,
            machine.Results.Select(x => $"{x.Character.Name}@{x.Character.World} · {StatusLabel(x.Status)} · 已核实 {x.Count} 个 / {x.Gil} 金币 · {x.Detail}").ToArray()));
        SaveTerminalReport();
    }
    private void SaveTerminalReport()
    {
        if(runToken == null || machine.State is not (SweepState.Completed or SweepState.Stopped or SweepState.Failed)) return;
        var stateKey = $"{machine.State}-{machine.Blocking}";
        if(lastSavedState == stateKey) return;
        lastSavedState = stateKey;
        var directory = Path.Combine(pi.GetPluginConfigDirectory(), "runs");
        var path = Path.Combine(directory, $"{runToken}-{stateKey}.json");
        var report = new { TimestampUtc = DateTime.UtcNow, State = machine.State.ToString(), machine.Blocking,
            machine.Detail, Source = "Inventory1..4", Results = machine.Results.ToArray() };
        // This is private local history, never a GitHub artifact. Capture immutable results before leaving the frame.
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
            }
            catch(Exception ex) { log.Error(ex, "Could not save private retainer vendor run history"); }
        });
    }
    private static string StatusLabel(VendorCharacterStatus state) => state switch
    {
        VendorCharacterStatus.Pending => "待处理", VendorCharacterStatus.Switching => "正在换角",
        VendorCharacterStatus.Selling => "正在出售", VendorCharacterStatus.Completed => "完成",
        VendorCharacterStatus.Stopped => "停止", VendorCharacterStatus.Failed => "失败", _ => "未知"
    };
    private void Update(IFramework _)
    {
        if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
        try
        {
            if(Volatile.Read(ref preview) is { } snapshot && (!host.LoggedIn || snapshot.Cid != host.CurrentCid))
                Volatile.Write(ref preview, null);
            ApplyStop();
            if(machine.Blocking || sales.Blocking)
            {
                if(!suppression.TryAcquire(out var error))
                {
                    machine.Stop(Now); message = error;
                    Publish(); return; // Cannot advance a native cleanup without exclusive UI cooperation.
                }
            }
            else suppression.Release();
            machine.Tick(Now);
        }
        catch(Exception ex)
        {
            machine.Stop(Now); message = "已停止：" + ex.Message;
            log.Error(ex, "RetainerVendorSweep update stopped");
        }
        Publish();
    }

    private void RefreshCharacters()
    {
        Queue(() =>
        {
            if(machine.Blocking) return;
            var rows = host.ReadCharacters();
            Volatile.Write(ref characters, rows);
            message = $"读取到 {rows.Count} 个 AR 已登记角色；未登记/被 AR 排除的角色不会自动发现。";
        });
    }
    private VendorCharacter[] OrderedCharacters()
    {
        var rows = Volatile.Read(ref characters);
        return rows.OrderBy(x => config.CharacterOrder.IndexOf(x.Cid) is var i && i >= 0 ? i : int.MaxValue)
            .ToArray();
    }
    private string? ValidateRules(SaleRules rules)
    {
        var issue = rules.Validate();
        if(issue != null) return issue;
        if(rules.ItemIds.Any(x => !catalog.ContainsKey(x)) || rules.CategoryIds.Any(x => !categories.ContainsKey(x)))
            return "已选规则包含当前游戏资料中无效的物品或品类，请重新选择。";
        return null;
    }
    private void PrepareStart()
    {
        if(configurationIssue != null) { message = configurationIssue; return; }
        var rules = config.Rules.Freeze();
        var issue = ValidateRules(rules);
        var selected = OrderedCharacters().Where(x => !config.ExcludedCharacters.Contains(x.Cid)).ToArray();
        if(issue != null) { message = issue; return; }
        if(selected.Length == 0) { message = "请先刷新并勾选至少一个角色。"; return; }
        confirmation = new(selected, rules, config.Delays.Freeze(), Volatile.Read(ref stopGeneration));
        ImGui.OpenPopup("开始本轮直售？");
    }
    private void Start(PendingStart request)
    {
        Queue(() =>
        {
            if(request.Generation != Volatile.Read(ref stopGeneration)) { message = "尚未执行的开始请求已取消。"; return; }
            if(sales.CheckSupport() is { } unsupported) throw new InvalidOperationException(unsupported);
            var live = host.ReadCharacters();
            if(request.Characters.Any(x => !live.Any(y => y == x)))
                throw new InvalidOperationException("AR 角色资料已经变化，请刷新后重新确认本轮清单。");
            if(!suppression.TryAcquire(out var error)) throw new InvalidOperationException(error);
            machine.Start(request.Characters, request.Rules, request.Delays, Now);
            runToken = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            lastSavedState = null;
            message = "本轮已开始；角色和规则已冻结，完成一遍后停止。";
        });
    }

    private unsafe void RefreshPreview()
    {
        var rules = config.Rules.Freeze();
        Queue(() =>
        {
            if(machine.Blocking || !host.LoggedIn) throw new InvalidOperationException("请在空闲、已登录状态预览当前背包。");
            if(!data.GameData.Repositories.TryGetValue("ffxiv", out var repository)
                || repository.Version?.Trim() != "2026.10.06.0000.0000" || sizeof(InventoryManager) != 0x3730)
                throw new InvalidOperationException("当前客户端未通过库存布局兼容检查。");
            var inventory = InventoryManager.Instance();
            if(inventory == null) throw new InvalidOperationException("角色库存尚未加载。");
            var lines = new List<string>(); long count = 0;
            for(var page = 0; page < 4; page++)
            {
                var bag = inventory->GetInventoryContainer((InventoryType)page);
                if(bag == null || !bag->IsLoaded) throw new InvalidOperationException("角色背包未完全加载。");
                for(var i = 0; i < bag->Size; i++)
                {
                    var slot = bag->GetInventorySlot(i);
                    if(slot == null || slot->Quantity == 0 || !catalog.TryGetValue(slot->ItemId, out var item)) continue;
                    var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                    var candidate = new VendorItem(page, i, item.Id, hq, checked((int)slot->Quantity), item.Category, item.Price,
                        (slot->Flags & InventoryItem.ItemFlags.Collectable) == 0);
                    if(!rules.Matches(candidate)) continue;
                    count = checked(count + candidate.Quantity);
                    lines.Add($"{item.Name}{(hq ? " HQ" : " NQ")} × {candidate.Quantity}（背包 {page + 1}，格 {i + 1}）");
                }
            }
            Volatile.Write(ref preview, new(player.ContentId, $"当前角色匹配 {lines.Count} 格，共 {count} 个。执行时还会核对原生出售资格与价格。", lines.ToArray()));
            message = "预览只读取当前角色；其他角色须登录后读取实际背包。";
        });
    }

    private void Draw()
    {
        if(!visible || disposed) return;
        var state = Volatile.Read(ref view);
        var locked = state.Blocking || Volatile.Read(ref pendingOperation) != 0;
        ImGui.SetNextWindowSize(new Vector2(780, 720), ImGuiCond.FirstUseEver);
        if(!ImGui.Begin("多角色雇员直售", ref visible)) { ImGui.End(); return; }
        try
        {
            // Always above tabs and disabled form fields, including during cleanup.
            if(ImGui.Button("停止本轮")) QueueStop();
            ImGui.SameLine(); ImGui.TextUnformatted("/retainersell stop");
            ImGui.TextWrapped(state.Detail);
            ImGui.TextWrapped(message);
            ImGui.Separator();
            ImGui.TextWrapped("仅出售角色背包中的物品，按 NPC 收购价交给雇员直接出售；保留名单中的物品不卖。手动开始，所有选中角色只处理一遍。");
            ImGui.TextWrapped("各角色须停在可交互的雇员铃旁。开始前关闭其他收取、制作和交易自动流程；本插件不会自动导航到铃。");
            if(ImGui.CollapsingHeader("开始前的 AR 设置"))
                ImGui.TextWrapped("输入 /ays settings → 高级设置 → 专家设置 → 行为设置：将有/无可用派遣任务时访问雇员铃的操作均设为 Disable AutoRetainer。关闭多角色、雇员感知、夜间模式、自动军票与自动启动；暂时禁用雇员收取后制作。每角色开始前会停止 AR 运行开关，结束后不自动重启 AR。");
            ImGui.BeginDisabled(locked);
            try
            {
                if(ImGui.Button("刷新角色清单")) RefreshCharacters();
                ImGui.SameLine(); if(ImGui.Button("预览当前背包")) RefreshPreview();
                ImGui.SameLine(); if(ImGui.Button("开始本轮…")) PrepareStart();
                if(ImGui.BeginTabBar("vendor-settings"))
                {
                    if(ImGui.BeginTabItem("出售规则")) { DrawRules(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("角色与顺序")) { DrawCharacters(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("各步骤延迟")) { DrawDelays(); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
            }
            finally { ImGui.EndDisabled(); }
            var p = Volatile.Read(ref preview);
            if(p != null && ImGui.CollapsingHeader("当前背包预览"))
            {
                ImGui.TextWrapped(p.Detail);
                ImGui.BeginChild("preview", new Vector2(0, 140), true);
                foreach(var line in p.Lines) ImGui.TextWrapped(line);
                ImGui.EndChild();
            }
            if(state.Results.Length > 0 && ImGui.CollapsingHeader("本轮角色结果"))
                foreach(var result in state.Results) ImGui.TextWrapped(result);
            DrawConfirmation();
        }
        finally { ImGui.End(); }
    }

    private void DrawCharacters()
    {
        var rows = OrderedCharacters();
        ImGui.TextWrapped($"已知 {rows.Length} 个，选中 {rows.Count(x => !config.ExcludedCharacters.Contains(x.Cid))} 个。此清单来自 AR 缓存，并非账号角色自动发现；请检查是否齐全。");
        if(ImGui.Button("全选已知角色")) { config.ExcludedCharacters.Clear(); Save(); }
        ImGui.SameLine(); if(ImGui.Button("全部取消")) { config.ExcludedCharacters = rows.Select(x => x.Cid).ToHashSet(); Save(); }
        ImGui.BeginChild("characters", new Vector2(0, 320), true);
        for(var index = 0; index < rows.Length; index++)
        {
            var row = rows[index]; ImGui.PushID(row.Cid.ToString());
            var selected = !config.ExcludedCharacters.Contains(row.Cid);
            if(ImGui.Checkbox($"{index + 1}. {row.Name}@{row.World}", ref selected))
            { if(selected) config.ExcludedCharacters.Remove(row.Cid); else config.ExcludedCharacters.Add(row.Cid); Save(); }
            ImGui.SameLine(); ImGui.BeginDisabled(index == 0);
            if(ImGui.SmallButton("上移")) MoveCharacter(rows, index, -1);
            ImGui.EndDisabled(); ImGui.SameLine(); ImGui.BeginDisabled(index == rows.Length - 1);
            if(ImGui.SmallButton("下移")) MoveCharacter(rows, index, 1);
            ImGui.EndDisabled(); ImGui.PopID();
        }
        ImGui.EndChild();
    }
    private void MoveCharacter(VendorCharacter[] rows, int index, int offset)
    {
        var ids = rows.Select(x => x.Cid).ToList();
        (ids[index], ids[index + offset]) = (ids[index + offset], ids[index]);
        config.CharacterOrder = ids; Save();
    }
    private bool IsProtected(uint id) => config.Rules.ProtectedItemIds.Contains(id)
        || config.Rules.CategoryProtectedItemIds.Values.Any(x => x.Contains(id));
    private void DrawRules()
    {
        if(configurationIssue != null) ImGui.TextWrapped(configurationIssue);
        if(ImGui.Button("清空所有规则并重新设置"))
        {
            // Clear sell and protection sets together: malformed protection must never leave selling enabled.
            config.Rules = new(); config.Version = 1; configurationIssue = null; Save();
            message = "已清空出售与保留规则。至少选择一条出售规则后才可开始。";
        }
        var nq = config.Rules.IncludeNq; var hq = config.Rules.IncludeHq;
        if(ImGui.Checkbox("允许出售 NQ", ref nq)) { config.Rules.IncludeNq = nq; Save(); }
        ImGui.SameLine(); if(ImGui.Checkbox("允许出售 HQ", ref hq)) { config.Rules.IncludeHq = hq; Save(); }
        ImGui.TextWrapped($"出售品类 {config.Rules.CategoryIds.Count} 项，指定物品 {config.Rules.ItemIds.Count} 项；保护名单优先。未勾选出售的物品保持原样。");
        if(ImGui.BeginCombo("品类", category == 0 ? "全部品类（只筛选显示）" : categories.GetValueOrDefault(category, "未知")))
        {
            if(ImGui.Selectable("全部品类（只筛选显示）", category == 0)) category = 0;
            foreach(var pair in categories.OrderBy(x => x.Value))
                if(ImGui.Selectable($"{pair.Value}##category{pair.Key}", pair.Key == category)) category = pair.Key;
            ImGui.EndCombo();
        }
        if(category != 0)
        {
            var sellCategory = config.Rules.CategoryIds.Contains(category);
            if(ImGui.Checkbox("出售这个品类（保留名单除外）", ref sellCategory))
            { if(sellCategory) config.Rules.CategoryIds.Add(category); else config.Rules.CategoryIds.Remove(category); Save(); }
        }
        ImGui.InputText("搜索物品名称或 ID", ref search, 120);
        ImGui.Checkbox("只显示已设规则的物品", ref showSelectedOnly);
        var key = (category, search, showSelectedOnly, rulesRevision);
        if(filterKey != key)
        {
            filteredItems = orderedCatalog.Where(x => (category == 0 || x.Category == category)
                && (string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Id.ToString() == search)
                && (!showSelectedOnly || config.Rules.ItemIds.Contains(x.Id) || IsProtected(x.Id) || config.Rules.CategoryIds.Contains(x.Category))).ToArray();
            filterKey = key;
        }
        var items = filteredItems;
        ImGui.BeginChild("items", new Vector2(0, 240), true);
        foreach(var item in items.Take(200))
        {
            ImGui.PushID((int)item.Id);
            var sell = config.Rules.ItemIds.Contains(item.Id);
            if(ImGui.Checkbox("出售", ref sell)) { if(sell) config.Rules.ItemIds.Add(item.Id); else config.Rules.ItemIds.Remove(item.Id); Save(); }
            ImGui.SameLine();
            var protect = IsProtected(item.Id);
            if(ImGui.Checkbox("保留不卖", ref protect))
            {
                if(protect)
                {
                    if(!config.Rules.CategoryProtectedItemIds.TryGetValue(item.Category, out var set))
                        config.Rules.CategoryProtectedItemIds[item.Category] = set = [];
                    set.Add(item.Id);
                }
                else
                {
                    config.Rules.ProtectedItemIds.Remove(item.Id);
                    foreach(var set in config.Rules.CategoryProtectedItemIds.Values) set.Remove(item.Id);
                }
                Save();
            }
            ImGui.SameLine(); ImGui.TextUnformatted($"{item.Name}  #{item.Id}");
            ImGui.PopID();
        }
        if(items.Length > 200) ImGui.TextWrapped($"共 {items.Length} 项，显示前 200 项；请用名称或品类缩小范围。");
        ImGui.EndChild();
        if(ImGui.CollapsingHeader("检查所有已启用品类与保护名单"))
        {
            foreach(var id in config.Rules.CategoryIds.Order())
                ImGui.TextUnformatted("出售品类：" + categories.GetValueOrDefault(id, $"未知 #{id}"));
            ImGui.InputText("筛选保留名单", ref protectSearch, 120);
            foreach(var id in config.Rules.ProtectedItemIds.Concat(config.Rules.CategoryProtectedItemIds.Values.SelectMany(x => x)).Distinct().Order())
            {
                var name = catalog.GetValueOrDefault(id)?.Name ?? $"未知物品 #{id}";
                if(name.Contains(protectSearch, StringComparison.OrdinalIgnoreCase)) ImGui.TextUnformatted($"保留：{name} #{id}");
            }
        }
    }

    private void DrawDelays()
    {
        ImGui.TextWrapped("每个步骤独立等待，至少 100 毫秒；网络或窗口响应另计超时。一次只发送一个动作。");
        var d = config.Delays;
        Delay("开始前", d.StartMs, x => d.StartMs = x);
        Delay("切换角色前", d.RelogMs, x => d.RelogMs = x);
        Delay("登录后稳定", d.LoginStableMs, x => d.LoginStableMs = x);
        Delay("打开雇员铃", d.BellMs, x => d.BellMs = x);
        Delay("选择雇员", d.SelectRetainerMs, x => d.SelectRetainerMs = x);
        Delay("对话推进", d.TalkMs, x => d.TalkMs = x);
        Delay("打开物品管理", d.OpenInventoryMs, x => d.OpenInventoryMs = x);
        Delay("提交整堆直售", d.ContextMs, x => d.ContextMs = x);
        Delay("确认出售", d.ConfirmMs, x => d.ConfirmMs = x);
        Delay("物品之间", d.ItemIntervalMs, x => d.ItemIntervalMs = x);
        Delay("关闭和返回", d.CloseMs, x => d.CloseMs = x);
        Delay("角色之间", d.NextCharacterMs, x => d.NextCharacterMs = x);
        var timeout = d.TimeoutSeconds;
        if(ImGui.InputInt("界面响应超时（秒）", ref timeout)) { d.TimeoutSeconds = Math.Clamp(timeout, 15, 600); Save(); }
        var relog = d.RelogTimeoutSeconds;
        if(ImGui.InputInt("换角超时（秒）", ref relog)) { d.RelogTimeoutSeconds = Math.Clamp(relog, 15, 600); Save(); }
    }
    private void Delay(string name, int value, System.Action<int> set)
    { if(ImGui.InputInt(name + "（毫秒）", ref value, 100, 500)) { set(Math.Clamp(value, 100, 60_000)); Save(); } }

    private void DrawConfirmation()
    {
        if(!ImGui.BeginPopupModal("开始本轮直售？", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if(confirmation is not { } request) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        ImGui.TextUnformatted($"角色 {request.Characters.Length} 个 · 仅角色背包 · 一遍后停止");
        ImGui.TextWrapped("匹配规则的整堆物品会按 NPC 收购价出售。保留名单始终优先。其他角色的库存将在登录后读取。");
        ImGui.TextUnformatted($"品质：{(request.Rules.IncludeNq ? "NQ " : "")}{(request.Rules.IncludeHq ? "HQ" : "")}");
        ImGui.BeginChild("confirm-rules", new Vector2(620, 240), true);
        foreach(var row in request.Characters) ImGui.TextUnformatted($"角色：{row.Name}@{row.World}");
        foreach(var id in request.Rules.CategoryIds) ImGui.TextUnformatted("出售品类：" + categories.GetValueOrDefault(id, id.ToString()));
        foreach(var id in request.Rules.ItemIds) ImGui.TextUnformatted("出售物品：" + (catalog.GetValueOrDefault(id)?.Name ?? id.ToString()));
        foreach(var id in request.Rules.ProtectedItemIds.Concat(request.Rules.CategoryProtectedItemIds.Values.SelectMany(x => x)).Distinct())
            ImGui.TextUnformatted("保留不卖：" + (catalog.GetValueOrDefault(id)?.Name ?? id.ToString()));
        ImGui.EndChild();
        if(ImGui.Button("确认并开始本轮")) { Start(request); confirmation = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(); if(ImGui.Button("返回修改")) { confirmation = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    public void Dispose()
    {
        if(disposed) return;
        disposed = true; Interlocked.Increment(ref stopGeneration);
        framework.Update -= Update;
        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenConfigUi -= Open; pi.UiBuilder.OpenMainUi -= Open;
        commands.RemoveHandler("/retainersell");
        Cleanup(() => pi.GetIpcProvider<bool>("RetainerVendorSweep.IsBusy").UnregisterFunc());
        // No new native game actions on unload. In-flight requests may still complete on the server.
        Cleanup(sales.Stop); Cleanup(sales.Dispose); Cleanup(suppression.Dispose);
    }
    private void Cleanup(System.Action action)
    {
        try { action(); }
        catch(Exception ex) { log.Error(ex, "RetainerVendorSweep unload cleanup failed; remaining cleanup will still be attempted"); }
    }
}
