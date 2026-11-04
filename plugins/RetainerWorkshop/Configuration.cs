using Dalamud.Configuration;
using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

[Serializable]
public sealed class WorkshopConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public ulong FirstCharacterCid { get; set; }
    public ulong LastCharacterCid { get; set; }
    public ulong CraftCharacterCid { get; set; }
    public bool CraftEnabled { get; set; }
    public int RecipeId { get; set; }
    public int CraftCount { get; set; } = 1;
    public bool RestockMaterials { get; set; }
    public MaterialDelays MaterialDelays { get; set; } = new();
    public CraftDelays CraftDelays { get; set; } = new();
    public Dictionary<int, CraftMode> RecipeModes { get; set; } = [];
    public bool ArtisanAutoModeConfirmed { get; set; }
    public SaleRules Sales { get; set; } = new();
    public VendorDelays SaleDelays { get; set; } = new();
    public VendorDelays RotationDelays { get; set; } = new();
    public List<ulong> SaleCharacterOrder { get; set; } = [];
    public HashSet<ulong> ExcludedSaleCharacters { get; set; } = [];
    public ScheduleSettings Schedule { get; set; } = new();
    // This manual run only; zero disables the round limit. Never enables automatic startup.
    public int StopAfterRounds { get; set; }

    public string? Validate(bool forManualSale = false)
    {
        if(ValidateShape() is { } shape) return shape;
        if(Schedule.Validate() is { } schedule) return schedule;
        if(StopAfterRounds < 0) return "停止轮数不能为负数，0 表示不按轮数停止。";
        if(forManualSale) return Sales.Validate();
        if(FirstCharacterCid == 0 || LastCharacterCid == 0) return "请选择开头角色和收尾角色。";
        if(Schedule.Enabled && Sales.Validate() is { } sale) return "自动直售规则：" + sale;
        if(CraftEnabled)
        {
            if(CraftCharacterCid == 0) return "启用制作时须选择制作角色。";
            if(RecipeId is < 1 or > ushort.MaxValue || CraftCount is < 1 or > 9999)
                return "请选择标准配方，制作次数须为 1–9999。";
            if(RecipeModes.GetValueOrDefault(RecipeId, CraftMode.Standard) == CraftMode.Standard && !ArtisanAutoModeConfirmed)
                return "标准制作需要在 Artisan 开启自动模式并在界面确认。";
        }
        return null;
    }

    // Shape validation does not require a runnable setup. Defaults remain editable and cannot auto-start.
    public string? ValidateShape()
    {
        if(Version != 1) return "不支持此工作坊配置版本。";
        if(MaterialDelays == null || CraftDelays == null || RecipeModes == null || Sales == null || SaleDelays == null ||
           RotationDelays == null || SaleCharacterOrder == null || ExcludedSaleCharacters == null || Schedule == null)
            return "配置字段缺失；不会自动用空规则覆盖已有保护设置。";
        if(Sales.ItemIds == null || Sales.CategoryIds == null || Sales.ProtectedItemIds == null || Sales.CategoryProtectedItemIds == null ||
           Sales.CategoryProtectedItemIds.Any(x => x.Value == null))
            return "出售或保留名单缺失；请修复配置，不能丢弃保护后继续。";
        if(Sales.ItemIds.Contains(0) || Sales.CategoryIds.Contains(0) || Sales.ProtectedItemIds.Contains(0) ||
           Sales.CategoryProtectedItemIds.Any(x => x.Key == 0 || x.Value.Contains(0))) return "出售或保护规则含无效编号。";
        if(RecipeModes.Any(x => x.Key is < 1 or > ushort.MaxValue || !Enum.IsDefined(x.Value))) return "配方制作方式配置无效。";
        if(SaleCharacterOrder.Contains(0) || SaleCharacterOrder.Distinct().Count() != SaleCharacterOrder.Count ||
           ExcludedSaleCharacters.Contains(0)) return "直售角色顺序含无效或重复角色。";
        return null;
    }

    public WorkshopConfiguration Freeze()
    {
        if(ValidateShape() is { } issue) throw new InvalidOperationException(issue);
        return new()
        {
            Version = Version, FirstCharacterCid = FirstCharacterCid, LastCharacterCid = LastCharacterCid,
            CraftCharacterCid = CraftCharacterCid, CraftEnabled = CraftEnabled, RecipeId = RecipeId,
            CraftCount = CraftCount, RestockMaterials = RestockMaterials, MaterialDelays = MaterialDelays.Freeze(),
            CraftDelays = CraftDelays.Freeze(), RecipeModes = new(RecipeModes), ArtisanAutoModeConfirmed = ArtisanAutoModeConfirmed,
            Sales = Sales.Freeze(), SaleDelays = SaleDelays.Freeze(), RotationDelays = RotationDelays.Freeze(),
            SaleCharacterOrder = new(SaleCharacterOrder), ExcludedSaleCharacters = new(ExcludedSaleCharacters),
            Schedule = Schedule.Freeze(), StopAfterRounds = StopAfterRounds
        };
    }
}
