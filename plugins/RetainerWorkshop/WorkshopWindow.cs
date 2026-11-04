using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

// Runtime publishes fresh array snapshots; the UI never reads game pointers or invokes IPC directly.
public sealed record WorkshopUiState(bool Blocking, string Detail, string[] Results, string PeriodDetail,
    VendorCharacter[] Characters, string[] Preview, string[] ImportPreview, bool CanApplyImport,
    bool InterruptionPending = false, bool OperationPending = false);

public interface IWorkshopActions
{
    WorkshopUiState State { get; }
    void SaveConfiguration();
    void Start();
    void Stop();
    void ManualSale();
    void RefreshCharacters();
    void PreviewInventory();
    void PreviewImport();
    void ApplyImport();
    void AcknowledgeInterruption();
}

public sealed class WorkshopWindow
{
    private WorkshopConfiguration config;
    private readonly IWorkshopActions actions;
    private readonly Dictionary<uint, string> categories;
    private readonly Dictionary<uint, CatalogItem> catalog;
    private readonly CatalogItem[] orderedItems;
    private readonly RecipeItem[] recipes;
    private readonly TimeZoneInfo[] zones;
    private string itemSearch = "", protectSearch = "", recipeSearch = "";
    private uint category;
    private bool selectedOnly;
    private int rulesRevision;
    private (uint Category, string Search, bool Selected, int Revision)? filterKey;
    private CatalogItem[] filtered = [];
    private bool? confirmManualSale;
    private sealed record CatalogItem(uint Id, string Name, uint Category);
    private sealed record RecipeItem(int Id, string Label, int Amount, bool Quick);
    public bool IsOpen { get; set; }

    public WorkshopWindow(WorkshopConfiguration config, IDataManager data, IWorkshopActions actions)
    {
        this.config = config; this.actions = actions;
        categories = data.GetExcelSheet<ItemUICategory>().Where(x => x.RowId != 0)
            .ToDictionary(x => x.RowId, x => x.Name.ExtractText());
        catalog = data.GetExcelSheet<Item>().Where(x => x.RowId != 0 && x.PriceLow > 0 && x.ItemUICategory.RowId != 0)
            .Select(x => new CatalogItem(x.RowId, x.Name.ExtractText(), x.ItemUICategory.RowId))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Id);
        orderedItems = catalog.Values.OrderBy(x => x.Name).ToArray();
        recipes = data.GetExcelSheet<Recipe>().Where(x => x.RowId is > 0 and <= ushort.MaxValue && x.Number != 0 && x.ItemResult.RowId != 0)
            .Select(x => new RecipeItem((int)x.RowId, $"{x.ItemResult.Value.Name} · {x.CraftType.Value.Name} · 配方 {x.RowId}", x.AmountResult, x.CanQuickSynth)).ToArray();
        zones = TimeZoneInfo.GetSystemTimeZones().OrderBy(x => x.BaseUtcOffset).ThenBy(x => x.Id).ToArray();
    }

    public void Open() => IsOpen = true;
    // The runtime must call this after replacing its configuration object (for example after ApplyImport).
    public void SetConfiguration(WorkshopConfiguration value)
    { config = value ?? throw new ArgumentNullException(nameof(value)); rulesRevision++; filterKey = null; confirmManualSale = null; }
    private void Save() { rulesRevision++; actions.SaveConfiguration(); }

    public void Draw()
    {
        if(!IsOpen) return;
        var state = actions.State;
        var locked = state.Blocking || state.OperationPending;
        var open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(820, 760), ImGuiCond.FirstUseEver);
        if(!ImGui.Begin("雇员工作坊", ref open)) { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;
        try
        {
            // Stop remains independent of disabled settings, pending starts and native cleanup.
            if(ImGui.Button("停止所有后续操作")) { confirmManualSale = null; actions.Stop(); }
            ImGui.SameLine(); ImGui.TextUnformatted("已发送请求仍须等待结果与界面收尾");
            ImGui.TextWrapped(state.Detail);
            ImGui.TextWrapped(state.PeriodDetail);
            if(state.InterruptionPending)
            {
                ImGui.TextWrapped("发现未核实的中断记录。请先检查实际物品、金币和游戏界面；确认只解除账本阻塞，不会续跑旧任务或重新出售。");
                ImGui.BeginDisabled(locked);
                if(ImGui.Button("我已人工核对中断结果")) actions.AcknowledgeInterruption();
                ImGui.EndDisabled();
            }
            ImGui.Separator();
            if(config.ValidateShape() is { } shape)
            { ImGui.TextWrapped(shape); ImGui.TextWrapped("配置结构损坏，未启用自动操作。请保留原配置并修复后重新加载。"); return; }
            ImGui.BeginDisabled(locked);
            try
            {
                if(ImGui.Button("刷新 AR 已知角色")) actions.RefreshCharacters();
                ImGui.SameLine(); if(ImGui.Button("开始工作坊…")) { confirmManualSale = false; ImGui.OpenPopup("确认本次手动开始"); }
                ImGui.SameLine(); if(ImGui.Button("手动直售一遍…")) { confirmManualSale = true; ImGui.OpenPopup("确认本次手动开始"); }
                if(ImGui.BeginTabBar("workshop-tabs"))
                {
                    if(ImGui.BeginTabItem("角色与轮换")) { DrawRotation(state); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("制作与取料")) { DrawCraft(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("出售与保留名单")) { DrawSales(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("直售角色与延迟")) { DrawSaleCharacters(state); DrawSaleDelays(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("日／周计划")) { DrawSchedule(); ImGui.EndTabItem(); }
                    if(ImGui.BeginTabItem("导入旧配置")) { DrawImport(state); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
            }
            finally { ImGui.EndDisabled(); }
            if(state.Preview.Length > 0 && ImGui.CollapsingHeader("当前角色背包预览"))
            {
                ImGui.BeginChild("inventory-preview", new Vector2(0, 140), true);
                foreach(var line in state.Preview) ImGui.TextWrapped(line);
                ImGui.EndChild();
            }
            if(state.Results.Length > 0 && ImGui.CollapsingHeader("本次运行结果"))
                foreach(var line in state.Results) ImGui.TextWrapped(line);
            DrawConfirmation(state);
        }
        finally { ImGui.End(); }
    }

    private void DrawRotation(WorkshopUiState state)
    {
        ImGui.TextWrapped("开头与收尾角色用于识别一轮的边界；中间角色仍由 AR 原生轮换。已知角色来自 AR 缓存，不代表自动发现全账号。加载插件不会自动开始。");
        CharacterPicker("开头角色", config.FirstCharacterCid, x => config.FirstCharacterCid = x, state.Characters);
        CharacterPicker("收尾角色", config.LastCharacterCid, x => config.LastCharacterCid = x, state.Characters);
        CharacterPicker("制作角色", config.CraftCharacterCid, x => config.CraftCharacterCid = x, state.Characters);
        var stop = config.StopAfterRounds;
        if(ImGui.InputInt("本次运行完成 M 轮后停止（0 = 关闭）", ref stop)) { config.StopAfterRounds = stop; Save(); }
        if(stop < 0) ImGui.TextWrapped("停止轮数不能为负数。");
        ImGui.TextWrapped("停止轮数仅用于本次手动运行，不是定时启动设置。完成、停止和重新加载均不会自动开始下一次运行。");
        if(ImGui.CollapsingHeader("工作坊轮换边界延迟（毫秒）"))
        {
            var d = config.RotationDelays;
            Delay("手动开始前", d.StartMs, x => d.StartMs = x, 60000);
            Delay("工作坊发起换角前", d.RelogMs, x => d.RelogMs = x, 60000);
            Delay("目标角色登录后稳定", d.LoginStableMs, x => d.LoginStableMs = x, 60000);
            Delay("角色边界之间", d.NextCharacterMs, x => d.NextCharacterMs = x, 60000);
            Timeout("边界响应超时（秒）", d.TimeoutSeconds, x => d.TimeoutSeconds = x);
            Timeout("工作坊换角超时（秒）", d.RelogTimeoutSeconds, x => d.RelogTimeoutSeconds = x);
            ImGui.TextWrapped("这些设置只控制工作坊自身的边界操作，不改变 AR 内部收取、对话或派遣步骤的速度。");
        }
    }
    private void CharacterPicker(string label, ulong selected, Action<ulong> assign, VendorCharacter[] characters)
    {
        var current = characters.FirstOrDefault(x => x.Cid == selected);
        var text = current != null ? $"{current.Name}@{current.World}" : selected == 0 ? "请选择角色" : "已选角色不在当前清单，请刷新核对";
        if(!ImGui.BeginCombo(label, text)) return;
        foreach(var row in characters)
            if(ImGui.Selectable($"{row.Name}@{row.World}##{label}{row.Cid}", row.Cid == selected)) { assign(row.Cid); Save(); }
        ImGui.EndCombo();
    }

    private void DrawCraft()
    {
        var enabled = config.CraftEnabled;
        if(ImGui.Checkbox("启用本轮制作阶段（不会自动启动工作坊）", ref enabled)) { config.CraftEnabled = enabled; Save(); }
        var selected = recipes.FirstOrDefault(x => x.Id == config.RecipeId);
        if(ImGui.BeginCombo("标准配方", selected?.Label ?? "请选择配方"))
        {
            ImGui.InputText("搜索物品／职业／配方编号", ref recipeSearch, 120);
            var shown = 0;
            foreach(var recipe in recipes)
            {
                if(!recipe.Label.Contains(recipeSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if(shown++ >= 150) { ImGui.TextDisabled("请继续输入以缩小结果。"); break; }
                if(ImGui.Selectable(recipe.Label, recipe.Id == config.RecipeId)) { config.RecipeId = recipe.Id; Save(); }
            }
            ImGui.EndCombo();
        }
        var count = config.CraftCount;
        if(ImGui.InputInt("每个制作时段次数上限（1–9999）", ref count)) { config.CraftCount = count; Save(); }
        if(selected != null) ImGui.TextWrapped($"最多预计 {Math.Max(0L, count) * selected.Amount} 个成品；这是尝试次数，不保证成功产量。{(selected.Quick ? "支持简易制作，角色仍须已完成过该配方。" : "本配方不支持简易制作。")}");
        var mode = config.RecipeModes.GetValueOrDefault(config.RecipeId, CraftMode.Standard);
        ImGui.BeginDisabled(config.RecipeId is < 1 or > ushort.MaxValue);
        if(ImGui.BeginCombo("此配方的制作方式", ModeLabel(mode)))
        {
            foreach(var option in new[] { CraftMode.Standard, CraftMode.Quick, CraftMode.Inherit })
                if(ImGui.Selectable(ModeLabel(option), mode == option)) { config.RecipeModes[config.RecipeId] = option; Save(); }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if(mode == CraftMode.Standard)
        {
            var confirmed = config.ArtisanAutoModeConfirmed;
            if(ImGui.Checkbox("我已在 Artisan 开启自动模式", ref confirmed)) { config.ArtisanAutoModeConfirmed = confirmed; Save(); }
            ImGui.TextWrapped("沿用 Artisan 求解器／宏，不保证 HQ。制作角色须已穿好装备并处于正确职业；停止后不再开下一件，当前工艺须完成或手动结束。");
        }
        else if(mode == CraftMode.Quick) ImGui.TextWrapped("每批最多 99 次，不满足简易条件时停止，不暗中回退到普通制作。");
        else ImGui.TextWrapped("兼容方式沿用 Artisan 全局设置；尚未接收的私有预处理请求不能由公开接口撤回，停止可能需要人工清理。");
        var restock = config.RestockMaterials;
        if(ImGui.Checkbox("制作前从本角色雇员补齐缺少的材料与水晶", ref restock)) { config.RestockMaterials = restock; Save(); }
        ImGui.TextWrapped("按全部计划次数准备材料；须已在附近雇员铃，空间不足或缺料时停止，不降低次数、不反复提交未知请求。");
        if(ImGui.CollapsingHeader("制作动作延迟（毫秒）"))
        {
            var d = config.CraftDelays;
            Delay("打开配方", d.OpenRecipeMs, x => d.OpenRecipeMs = x); Delay("选择材料", d.FillMaterialsMs, x => d.FillMaterialsMs = x);
            Delay("开始制作", d.StartCraftMs, x => d.StartCraftMs = x); Delay("提交简易次数", d.SubmitQuickMs, x => d.SubmitQuickMs = x);
            Delay("取消简易制作", d.CancelQuickMs, x => d.CancelQuickMs = x); Delay("退出配方", d.CloseRecipeMs, x => d.CloseRecipeMs = x);
            Delay("两次制作间隔", d.BetweenCraftsMs, x => d.BetweenCraftsMs = x); Timeout("制作响应超时（秒）", d.TimeoutSeconds, x => d.TimeoutSeconds = x);
        }
        if(ImGui.CollapsingHeader("取料动作延迟（毫秒）"))
        {
            var d = config.MaterialDelays;
            Delay("取料：打开铃", d.BellMs, x => d.BellMs = x); Delay("取料：选择雇员", d.SelectRetainerMs, x => d.SelectRetainerMs = x);
            Delay("取料：打开仓库", d.OpenInventoryMs, x => d.OpenInventoryMs = x); Delay("取料：请求取出", d.WithdrawMs, x => d.WithdrawMs = x);
            Delay("取料：提交数量", d.SubmitQuantityMs, x => d.SubmitQuantityMs = x); Delay("取料：关闭界面", d.CloseUiMs, x => d.CloseUiMs = x);
            Timeout("取料响应超时（秒）", d.TimeoutSeconds, x => d.TimeoutSeconds = x);
        }
    }
    private static string ModeLabel(CraftMode mode) => mode switch
    { CraftMode.Standard => "标准制作", CraftMode.Quick => "简易制作", CraftMode.Inherit => "跟随 Artisan 设置（兼容）", _ => "无效模式" };

    private void DrawSales()
    {
        var rules = config.Sales;
        ImGui.TextWrapped("仅直售角色普通背包中匹配的整堆物品；白名单是保留不售名单，优先于出售品类和物品。不会出售雇员仓库，也不在这里输入部分数量。");
        var nq = rules.IncludeNq; var hq = rules.IncludeHq;
        if(ImGui.Checkbox("允许 NQ", ref nq)) { rules.IncludeNq = nq; Save(); }
        ImGui.SameLine(); if(ImGui.Checkbox("允许 HQ", ref hq)) { rules.IncludeHq = hq; Save(); }
        if(ImGui.Button("预览当前角色背包")) actions.PreviewInventory();
        if(ImGui.BeginCombo("品类筛选", category == 0 ? "全部品类（只筛选显示）" : categories.GetValueOrDefault(category, "未知品类")))
        {
            if(ImGui.Selectable("全部品类（只筛选显示）", category == 0)) category = 0;
            foreach(var pair in categories.OrderBy(x => x.Value))
                if(ImGui.Selectable($"{pair.Value}##category{pair.Key}", pair.Key == category)) category = pair.Key;
            ImGui.EndCombo();
        }
        if(category != 0)
        {
            var selected = rules.CategoryIds.Contains(category);
            if(ImGui.Checkbox("出售此品类（保留名单除外）", ref selected)) { if(selected) rules.CategoryIds.Add(category); else rules.CategoryIds.Remove(category); Save(); }
        }
        ImGui.InputText("搜索物品名称或 ID", ref itemSearch, 120); ImGui.Checkbox("只显示已设规则的物品", ref selectedOnly);
        var key = (category, itemSearch, selectedOnly, rulesRevision);
        if(filterKey != key)
        {
            filtered = orderedItems.Where(x => (category == 0 || x.Category == category) &&
                (x.Name.Contains(itemSearch, StringComparison.OrdinalIgnoreCase) || x.Id.ToString() == itemSearch) &&
                (!selectedOnly || rules.ItemIds.Contains(x.Id) || rules.CategoryIds.Contains(x.Category) || Protected(x.Id))).ToArray();
            filterKey = key;
        }
        ImGui.BeginChild("sale-items", new Vector2(0, 240), true);
        foreach(var item in filtered.Take(200))
        {
            ImGui.PushID((int)item.Id);
            var sell = rules.ItemIds.Contains(item.Id);
            if(ImGui.Checkbox("出售", ref sell)) { if(sell) rules.ItemIds.Add(item.Id); else rules.ItemIds.Remove(item.Id); Save(); }
            ImGui.SameLine(); var protect = Protected(item.Id);
            if(ImGui.Checkbox("保留不售", ref protect))
            {
                if(protect)
                { if(!rules.CategoryProtectedItemIds.TryGetValue(item.Category, out var list)) rules.CategoryProtectedItemIds[item.Category] = list = []; list.Add(item.Id); }
                else { rules.ProtectedItemIds.Remove(item.Id); foreach(var list in rules.CategoryProtectedItemIds.Values) list.Remove(item.Id); }
                Save();
            }
            ImGui.SameLine(); ImGui.TextUnformatted($"{item.Name} #{item.Id}"); ImGui.PopID();
        }
        if(filtered.Length > 200) ImGui.TextWrapped($"匹配 {filtered.Length} 项，显示前 200 项；请缩小搜索范围。");
        ImGui.EndChild();
        if(ImGui.CollapsingHeader("核对全部出售品类与保留名单"))
        {
            foreach(var id in rules.CategoryIds.Order()) ImGui.TextUnformatted("出售品类：" + categories.GetValueOrDefault(id, $"未知 #{id}"));
            ImGui.InputText("筛选保留名单", ref protectSearch, 120);
            foreach(var id in rules.ProtectedItemIds.Concat(rules.CategoryProtectedItemIds.Values.SelectMany(x => x)).Distinct().Order())
            {
                var label = catalog.GetValueOrDefault(id)?.Name ?? $"未知物品 #{id}";
                if(label.Contains(protectSearch, StringComparison.OrdinalIgnoreCase) || id.ToString() == protectSearch) ImGui.TextUnformatted($"保留不售：{label} #{id}");
            }
        }
    }
    private bool Protected(uint id) => config.Sales.ProtectedItemIds.Contains(id) || config.Sales.CategoryProtectedItemIds.Values.Any(x => x.Contains(id));

    private void DrawSaleCharacters(WorkshopUiState state)
    {
        var rows = state.Characters.OrderBy(x => config.SaleCharacterOrder.IndexOf(x.Cid) is var i && i >= 0 ? i : int.MaxValue).ToArray();
        ImGui.TextWrapped($"直售已知角色 {rows.Length} 个，选中 {rows.Count(x => !config.ExcludedSaleCharacters.Contains(x.Cid))} 个。各角色须在附近雇员铃；不自动导航。角色选择用于直售，不改变 AR 内部收取名单。");
        if(ImGui.Button("全选已知角色")) { config.ExcludedSaleCharacters.Clear(); Save(); }
        ImGui.SameLine(); if(ImGui.Button("取消所有已知角色")) { config.ExcludedSaleCharacters.UnionWith(rows.Select(x => x.Cid)); Save(); }
        ImGui.BeginChild("sale-characters", new Vector2(0, 220), true);
        for(var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; ImGui.PushID(row.Cid.ToString());
            var selected = !config.ExcludedSaleCharacters.Contains(row.Cid);
            if(ImGui.Checkbox($"{i + 1}. {row.Name}@{row.World}", ref selected))
            { if(selected) config.ExcludedSaleCharacters.Remove(row.Cid); else config.ExcludedSaleCharacters.Add(row.Cid); Save(); }
            ImGui.SameLine(); ImGui.BeginDisabled(i == 0); if(ImGui.SmallButton("上移")) Move(rows, i, -1); ImGui.EndDisabled();
            ImGui.SameLine(); ImGui.BeginDisabled(i == rows.Length - 1); if(ImGui.SmallButton("下移")) Move(rows, i, 1); ImGui.EndDisabled(); ImGui.PopID();
        }
        ImGui.EndChild();
    }
    private void Move(VendorCharacter[] rows, int index, int delta)
    { var ids = rows.Select(x => x.Cid).ToList(); (ids[index], ids[index + delta]) = (ids[index + delta], ids[index]); config.SaleCharacterOrder = ids; Save(); }
    private void DrawSaleDelays()
    {
        if(!ImGui.CollapsingHeader("直售步骤延迟（毫秒）")) return;
        var d = config.SaleDelays;
        Delay("直售：开始前", d.StartMs, x => d.StartMs = x, 60000); Delay("直售：换角前", d.RelogMs, x => d.RelogMs = x, 60000);
        Delay("直售：登录稳定", d.LoginStableMs, x => d.LoginStableMs = x, 60000); Delay("直售：打开铃", d.BellMs, x => d.BellMs = x, 60000);
        Delay("直售：选择雇员", d.SelectRetainerMs, x => d.SelectRetainerMs = x, 60000); Delay("直售：对话推进", d.TalkMs, x => d.TalkMs = x, 60000);
        Delay("直售：打开物品管理", d.OpenInventoryMs, x => d.OpenInventoryMs = x, 60000); Delay("直售：提交整堆", d.ContextMs, x => d.ContextMs = x, 60000);
        Delay("直售：确认出售", d.ConfirmMs, x => d.ConfirmMs = x, 60000); Delay("直售：物品间隔", d.ItemIntervalMs, x => d.ItemIntervalMs = x, 60000);
        Delay("直售：关闭界面", d.CloseMs, x => d.CloseMs = x, 60000); Delay("直售：角色间隔", d.NextCharacterMs, x => d.NextCharacterMs = x, 60000);
        Timeout("直售界面响应超时（秒）", d.TimeoutSeconds, x => d.TimeoutSeconds = x); Timeout("直售换角超时（秒）", d.RelogTimeoutSeconds, x => d.RelogTimeoutSeconds = x);
    }
    private void Delay(string label, int value, Action<int> assign, int maximum = 30000)
    { if(ImGui.InputInt(label, ref value, 100, 500)) { assign(Math.Clamp(value, 100, maximum)); Save(); } }
    private void Timeout(string label, int value, Action<int> assign)
    { if(ImGui.InputInt(label, ref value)) { assign(Math.Clamp(value, 15, 600)); Save(); } }

    private void DrawSchedule()
    {
        var s = config.Schedule; var enabled = s.Enabled;
        if(ImGui.Checkbox("启用日／周完成 N 轮后自动直售一次", ref enabled)) { s.Enabled = enabled; Save(); }
        ImGui.TextWrapped("只在手动运行工作坊期间累计已确认的轮次；不是按时启动插件。每个周期最多生成一次自动直售任务，中断结果未核实时不会自动重发。");
        if(ImGui.BeginCombo("周期", s.Mode == PeriodMode.Daily ? "每日" : "每周"))
        {
            foreach(var mode in new[] { PeriodMode.Daily, PeriodMode.Weekly })
                if(ImGui.Selectable(mode == PeriodMode.Daily ? "每日" : "每周", s.Mode == mode)) { s.Mode = mode; Save(); }
            ImGui.EndCombo();
        }
        var rounds = s.RoundsBeforeSale;
        if(ImGui.InputInt("本周期完成 N 轮后直售", ref rounds)) { s.RoundsBeforeSale = rounds; Save(); }
        if(ImGui.BeginCombo("周期时区", s.TimeZoneId))
        {
            foreach(var zone in zones) if(ImGui.Selectable($"{zone.DisplayName}##{zone.Id}", s.TimeZoneId == zone.Id)) { s.TimeZoneId = zone.Id; Save(); }
            ImGui.EndCombo();
        }
        var hour = s.ResetMinuteOfDay / 60; var minute = s.ResetMinuteOfDay % 60;
        if(ImGui.InputInt("重置时间：小时（0–23）", ref hour)) { s.ResetMinuteOfDay = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59); Save(); }
        if(ImGui.InputInt("重置时间：分钟（0–59）", ref minute)) { s.ResetMinuteOfDay = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59); Save(); }
        if(s.Mode == PeriodMode.Weekly && ImGui.BeginCombo("每周起始日", DayLabel(s.WeekStartsOn)))
        {
            foreach(var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                if(ImGui.Selectable(DayLabel(day), s.WeekStartsOn == day)) { s.WeekStartsOn = day; Save(); }
            ImGui.EndCombo();
        }
        if(s.Validate() is { } issue) ImGui.TextWrapped(issue);
        ImGui.TextWrapped("更改周期设置不会清除已发任务或重新获得当前周期的直售机会；生效时间以顶部周期状态为准。");
    }
    private static string DayLabel(DayOfWeek day) => day switch
    { DayOfWeek.Monday => "星期一", DayOfWeek.Tuesday => "星期二", DayOfWeek.Wednesday => "星期三", DayOfWeek.Thursday => "星期四", DayOfWeek.Friday => "星期五", DayOfWeek.Saturday => "星期六", DayOfWeek.Sunday => "星期日", _ => "无效起始日" };

    private void DrawImport(WorkshopUiState state)
    {
        ImGui.TextWrapped("只读旧制作桥接 v2 与直售 v1 配置，先预览再明确应用。不会加载旧插件类型、不删除或修改旧文件、不导入旧 Enabled，也不会自动开始任务。");
        ImGui.TextWrapped("保护名单缺失或损坏会拒绝导入。新周期、停止轮数、收尾角色、制作启用选择及轮换延迟保持不变。");
        if(ImGui.Button("预览旧配置迁移")) actions.PreviewImport();
        foreach(var line in state.ImportPreview) ImGui.TextWrapped(line);
        ImGui.BeginDisabled(!state.CanApplyImport);
        if(ImGui.Button("确认应用已预览的旧配置")) actions.ApplyImport();
        ImGui.EndDisabled();
    }
    private void DrawConfirmation(WorkshopUiState state)
    {
        if(!ImGui.BeginPopupModal("确认本次手动开始", ImGuiWindowFlags.AlwaysAutoResize)) return;
        try
        {
            if(confirmManualSale is not { } manual || state.Blocking || state.OperationPending)
            { ImGui.CloseCurrentPopup(); return; }
            ImGui.TextWrapped(manual ? "本次只对选定角色执行一遍直售；匹配物品按整堆出售，保留名单优先。" : "本次手动开启工作坊，按所选开头／收尾角色和周期配置运行。不会恢复旧的中断请求。");
            var selected = state.Characters.Where(x => !config.ExcludedSaleCharacters.Contains(x.Cid))
                .OrderBy(x => config.SaleCharacterOrder.IndexOf(x.Cid) is var i && i >= 0 ? i : int.MaxValue).ToArray();
            if(manual)
            {
                ImGui.TextWrapped($"角色 {selected.Length} 个；出售物品规则 {config.Sales.ItemIds.Count} 项、品类 {config.Sales.CategoryIds.Count} 项；品质 {(config.Sales.IncludeNq ? "NQ " : "")}{(config.Sales.IncludeHq ? "HQ" : "")}。");
                ImGui.BeginChild("confirm-sale-plan", new Vector2(640, 240), true);
                foreach(var character in selected) ImGui.TextUnformatted($"{character.Name}@{character.World}");
                foreach(var id in config.Sales.CategoryIds.Order()) ImGui.TextUnformatted("出售品类：" + categories.GetValueOrDefault(id, $"未知 #{id}"));
                foreach(var id in config.Sales.ItemIds.Order()) ImGui.TextUnformatted("出售物品：" + (catalog.GetValueOrDefault(id)?.Name ?? $"未知 #{id}"));
                foreach(var id in config.Sales.ProtectedItemIds.Concat(config.Sales.CategoryProtectedItemIds.Values.SelectMany(x => x)).Distinct().Order())
                    ImGui.TextUnformatted("保留不售：" + (catalog.GetValueOrDefault(id)?.Name ?? $"未知 #{id}"));
                ImGui.EndChild();
            }
            else
            {
                string Role(ulong cid) => state.Characters.FirstOrDefault(x => x.Cid == cid) is { } row ? $"{row.Name}@{row.World}" : "尚未核对的角色";
                ImGui.TextUnformatted($"开头：{Role(config.FirstCharacterCid)}　收尾：{Role(config.LastCharacterCid)}");
                ImGui.TextWrapped(config.CraftEnabled ? $"制作：{Role(config.CraftCharacterCid)}，配方 #{config.RecipeId}，最多 {config.CraftCount} 次，{ModeLabel(config.RecipeModes.GetValueOrDefault(config.RecipeId, CraftMode.Standard))}。" : "本次不启用制作阶段。");
                ImGui.TextWrapped(config.Schedule.Enabled ? $"自动直售：{(config.Schedule.Mode == PeriodMode.Daily ? "每日" : "每周")}完成 {config.Schedule.RoundsBeforeSale} 轮后最多一次，按直售角色与保留规则执行。" : "日／周自动直售已关闭。");
                ImGui.TextUnformatted(config.StopAfterRounds == 0 ? "本次不按轮数停止。" : $"本次完成 {config.StopAfterRounds} 轮后停止。");
            }
            var issue = config.Validate(manual);
            if(manual && selected.Length == 0) issue = "请先刷新并勾选至少一个直售角色。";
            if(issue != null) ImGui.TextWrapped(issue);
            ImGui.BeginDisabled(issue != null || state.InterruptionPending);
            if(ImGui.Button("确认开始")) { if(manual) actions.ManualSale(); else actions.Start(); confirmManualSale = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled(); ImGui.SameLine();
            if(ImGui.Button("返回修改")) { confirmManualSale = null; ImGui.CloseCurrentPopup(); }
        }
        finally { ImGui.EndPopup(); }
    }
}
