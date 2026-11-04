using System.Globalization;
using System.Text.Json;
using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

public sealed class LegacyImportResult
{
    private readonly WorkshopConfiguration snapshot;
    private readonly string[] preview;
    public WorkshopConfiguration Configuration => snapshot.Freeze();
    public string[] Preview => (string[])preview.Clone();
    public bool HasBridge { get; }
    public bool HasVendor { get; }
    internal LegacyImportResult(WorkshopConfiguration configuration, string[] lines, bool bridge, bool vendor)
    { snapshot = configuration.Freeze(); preview = (string[])lines.Clone(); HasBridge = bridge; HasVendor = vendor; }
}

// Data-only import: never deserialize old plugin CLR types, resolve $type, read files, or start work.
public static class LegacyImport
{
    public static bool TryPreview(WorkshopConfiguration current, string? bridgeJson, string? vendorJson,
        out LegacyImportResult? result, out string error)
    {
        result = null; error = "";
        try
        {
            if(bridgeJson == null && vendorJson == null) throw new FormatException("未提供可导入的旧配置。");
            var candidate = current.Freeze();
            var lines = new List<string>();
            if(bridgeJson != null)
            {
                using var document = Parse(bridgeJson); var root = document.RootElement;
                if(Int(root, "Version", 2, 2) != 2) throw new FormatException("仅支持旧制作桥接配置 v2。");
                candidate.FirstCharacterCid = Ulong(root, "FirstCharacterCid");
                candidate.CraftCharacterCid = Ulong(root, "CraftCharacterCid");
                candidate.RecipeId = Int(root, "RecipeId", 0, ushort.MaxValue);
                candidate.CraftCount = Int(root, "CraftCount", 1, 9999);
                candidate.RestockMaterials = Bool(root, "RestockMaterials");
                candidate.ArtisanAutoModeConfirmed = Bool(root, "ArtisanAutoModeConfirmed");
                candidate.RecipeModes = ReadModes(Field(root, "RecipeModes"));
                candidate.MaterialDelays = ReadMaterialDelays(Field(root, "MaterialDelays"));
                candidate.CraftDelays = ReadCraftDelays(Field(root, "CraftDelays"));
                lines.Add($"制作桥接 v2：导入开头／制作角色选择、配方 #{candidate.RecipeId}、次数 {candidate.CraftCount}、{candidate.RecipeModes.Count} 项配方模式与取料／制作延迟。");
                lines.Add("旧 Enabled 不导入；是否启用制作保持当前选择。收尾角色须在新界面单独设置。");
            }
            if(vendorJson != null)
            {
                using var document = Parse(vendorJson); var root = document.RootElement;
                _ = Int(root, "Version", 1, 1);
                candidate.Sales = ReadSales(Field(root, "Rules"));
                PreserveProtection(candidate.Sales, current.Sales);
                candidate.SaleDelays = ReadVendorDelays(Field(root, "Delays"));
                candidate.SaleCharacterOrder = ReadIds(Field(root, "CharacterOrder"), true).ToList();
                candidate.ExcludedSaleCharacters = ReadIds(Field(root, "ExcludedCharacters"), true).ToHashSet();
                var protectedCount = candidate.Sales.ProtectedItemIds.Concat(candidate.Sales.CategoryProtectedItemIds.Values.SelectMany(x => x)).Distinct().Count();
                lines.Add($"直售 v1：导入 {candidate.Sales.ItemIds.Count} 个指定物品、{candidate.Sales.CategoryIds.Count} 个品类，保留名单共 {protectedCount} 个物品（优先不售）。");
                lines.Add($"导入直售品质、动作延迟、{candidate.SaleCharacterOrder.Count} 项角色顺序和 {candidate.ExcludedSaleCharacters.Count} 个排除角色。");
                lines.Add("保留名单合并当前与旧配置，不删除新界面中已经设置的保护。");
            }
            if(candidate.ValidateShape() is { } issue) throw new FormatException(issue);
            lines.Add("日／周周期、每轮停止数、收尾角色及轮换延迟不覆盖；只预览，不开始任何任务，不修改旧文件。");
            result = new(candidate, lines.ToArray(), bridgeJson != null, vendorJson != null);
            return true;
        }
        catch(Exception ex) when(ex is JsonException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        { error = "旧配置未导入：" + ex.Message; return false; }
    }

    // Merge the exact previewed legacy fields into the latest configuration. Unrelated settings may
    // have changed since preview, and must not be rolled back when the user explicitly applies it.
    public static WorkshopConfiguration Apply(WorkshopConfiguration current, LegacyImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var merged = current.Freeze(); var imported = result.Configuration;
        if(result.HasBridge)
        {
            merged.FirstCharacterCid = imported.FirstCharacterCid; merged.CraftCharacterCid = imported.CraftCharacterCid;
            merged.RecipeId = imported.RecipeId; merged.CraftCount = imported.CraftCount;
            merged.RestockMaterials = imported.RestockMaterials; merged.MaterialDelays = imported.MaterialDelays;
            merged.CraftDelays = imported.CraftDelays; merged.RecipeModes = imported.RecipeModes;
            merged.ArtisanAutoModeConfirmed = imported.ArtisanAutoModeConfirmed;
        }
        if(result.HasVendor)
        {
            PreserveProtection(imported.Sales, merged.Sales);
            merged.Sales = imported.Sales; merged.SaleDelays = imported.SaleDelays;
            merged.SaleCharacterOrder = imported.SaleCharacterOrder; merged.ExcludedSaleCharacters = imported.ExcludedSaleCharacters;
        }
        return merged.Freeze();
    }

    private static void PreserveProtection(SaleRules destination, SaleRules current)
    {
        destination.ProtectedItemIds.UnionWith(current.ProtectedItemIds);
        foreach(var pair in current.CategoryProtectedItemIds)
        {
            if(!destination.CategoryProtectedItemIds.TryGetValue(pair.Key, out var existing))
                destination.CategoryProtectedItemIds[pair.Key] = existing = [];
            existing.UnionWith(pair.Value);
        }
    }

    private static JsonDocument Parse(string text)
    {
        if(string.IsNullOrWhiteSpace(text) || text.Length > 4 * 1024 * 1024) throw new FormatException("配置为空或超出导入大小限制。");
        var document = JsonDocument.Parse(text, new() { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        try { Object(document.RootElement); CheckDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if(value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject())
            { if(!names.Add(field.Name)) throw new FormatException("配置含重复字段。"); CheckDuplicates(field.Value); }
        }
        else if(value.ValueKind == JsonValueKind.Array) foreach(var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static void Object(JsonElement value)
    { if(value.ValueKind != JsonValueKind.Object) throw new FormatException("应为配置对象，不能为 null 或其他类型。"); }
    private static JsonElement Field(JsonElement value, string name)
    {
        Object(value);
        if(!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) throw new FormatException($"缺少必需字段 {name}，不会用空保护名单替代。");
        return field;
    }
    private static int Int(JsonElement parent, string name, int min, int max)
    {
        var value = Field(parent, name);
        if(!value.TryGetInt32(out var number) || number < min || number > max) throw new FormatException($"{name} 的整数值超出范围。");
        return number;
    }
    private static ulong Ulong(JsonElement parent, string name)
    { if(!Field(parent, name).TryGetUInt64(out var value)) throw new FormatException($"{name} 必须是非负整数。"); return value; }
    private static bool Bool(JsonElement parent, string name)
    { var value = Field(parent, name); if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"{name} 必须是布尔值。"); return value.GetBoolean(); }
    private static IEnumerable<ulong> ReadIds(JsonElement value, bool nonzero)
    {
        if(value.ValueKind != JsonValueKind.Array) throw new FormatException("名单必须为数组且不能缺失。");
        var seen = new HashSet<ulong>();
        foreach(var item in value.EnumerateArray())
        {
            if(!item.TryGetUInt64(out var id) || (nonzero && id == 0) || !seen.Add(id)) throw new FormatException("名单含无效或重复编号。");
            yield return id;
        }
    }
    private static HashSet<uint> ItemIds(JsonElement value) => ReadIds(value, true).Select(x => checked((uint)x)).ToHashSet();
    private static Dictionary<int, CraftMode> ReadModes(JsonElement value)
    {
        Object(value); var result = new Dictionary<int, CraftMode>();
        foreach(var field in value.EnumerateObject())
        {
            if(field.Name == "$type") continue; // Dalamud metadata is inert data; never resolve a CLR type.
            if(!int.TryParse(field.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var recipe) || recipe is < 1 or > ushort.MaxValue)
                throw new FormatException("配方模式编号无效。");
            CraftMode mode;
            if(field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var numeric)) mode = (CraftMode)numeric;
            else if(field.Value.ValueKind == JsonValueKind.String && Enum.TryParse<CraftMode>(field.Value.GetString(), false, out var parsed)) mode = parsed;
            else throw new FormatException("配方模式无效。");
            if(!Enum.IsDefined(mode) || !result.TryAdd(recipe, mode)) throw new FormatException("配方模式无效或重复。");
        }
        return result;
    }
    private static SaleRules ReadSales(JsonElement value)
    {
        var result = new SaleRules
        {
            ItemIds = ItemIds(Field(value, "ItemIds")), CategoryIds = ItemIds(Field(value, "CategoryIds")),
            ProtectedItemIds = ItemIds(Field(value, "ProtectedItemIds")), IncludeNq = Bool(value, "IncludeNq"), IncludeHq = Bool(value, "IncludeHq")
        };
        var groups = Field(value, "CategoryProtectedItemIds"); Object(groups);
        foreach(var field in groups.EnumerateObject())
        {
            if(field.Name == "$type") continue;
            if(!uint.TryParse(field.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key == 0 ||
               !result.CategoryProtectedItemIds.TryAdd(key, ItemIds(field.Value))) throw new FormatException("品类保护名单无效或重复。");
        }
        return result;
    }
    private static MaterialDelays ReadMaterialDelays(JsonElement x) => new()
    { BellMs = Int(x, "BellMs", 100, 30000), SelectRetainerMs = Int(x, "SelectRetainerMs", 100, 30000), OpenInventoryMs = Int(x, "OpenInventoryMs", 100, 30000),
      WithdrawMs = Int(x, "WithdrawMs", 100, 30000), SubmitQuantityMs = Int(x, "SubmitQuantityMs", 100, 30000), CloseUiMs = Int(x, "CloseUiMs", 100, 30000), TimeoutSeconds = Int(x, "TimeoutSeconds", 15, 600) };
    private static CraftDelays ReadCraftDelays(JsonElement x) => new()
    { OpenRecipeMs = Int(x, "OpenRecipeMs", 100, 30000), FillMaterialsMs = Int(x, "FillMaterialsMs", 100, 30000), StartCraftMs = Int(x, "StartCraftMs", 100, 30000),
      SubmitQuickMs = Int(x, "SubmitQuickMs", 100, 30000), CancelQuickMs = Int(x, "CancelQuickMs", 100, 30000), CloseRecipeMs = Int(x, "CloseRecipeMs", 100, 30000),
      BetweenCraftsMs = Int(x, "BetweenCraftsMs", 100, 30000), TimeoutSeconds = Int(x, "TimeoutSeconds", 15, 600) };
    private static VendorDelays ReadVendorDelays(JsonElement x) => new()
    { StartMs = Int(x, "StartMs", 100, 60000), RelogMs = Int(x, "RelogMs", 100, 60000), LoginStableMs = Int(x, "LoginStableMs", 100, 60000),
      BellMs = Int(x, "BellMs", 100, 60000), SelectRetainerMs = Int(x, "SelectRetainerMs", 100, 60000), TalkMs = Int(x, "TalkMs", 100, 60000),
      OpenInventoryMs = Int(x, "OpenInventoryMs", 100, 60000), ContextMs = Int(x, "ContextMs", 100, 60000), QuantityMs = Int(x, "QuantityMs", 100, 60000),
      ConfirmMs = Int(x, "ConfirmMs", 100, 60000), ItemIntervalMs = Int(x, "ItemIntervalMs", 100, 60000), CloseMs = Int(x, "CloseMs", 100, 60000),
      NextCharacterMs = Int(x, "NextCharacterMs", 100, 60000), TimeoutSeconds = Int(x, "TimeoutSeconds", 15, 600), RelogTimeoutSeconds = Int(x, "RelogTimeoutSeconds", 15, 600) };
}
