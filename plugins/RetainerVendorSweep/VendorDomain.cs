namespace RetainerVendorSweep;

public sealed record VendorCharacter(ulong Cid, string Name, string World);
// CategoryId is the game's ItemUICategory, not ItemSearchCategory or equip category.
public sealed record VendorItem(int Container, int Slot, uint ItemId, bool Hq, int Quantity,
    uint CategoryId, uint UnitPrice, bool Sellable);

[Serializable]
public sealed class SaleRules
{
    public HashSet<uint> ItemIds { get; set; } = [];
    public HashSet<uint> CategoryIds { get; set; } = [];
    public HashSet<uint> ProtectedItemIds { get; set; } = [];
    public Dictionary<uint, HashSet<uint>> CategoryProtectedItemIds { get; set; } = [];
    public bool IncludeNq { get; set; } = true;
    public bool IncludeHq { get; set; }

    public SaleRules Freeze() => new()
    {
        ItemIds = new(ItemIds), CategoryIds = new(CategoryIds), ProtectedItemIds = new(ProtectedItemIds),
        CategoryProtectedItemIds = CategoryProtectedItemIds.ToDictionary(x => x.Key, x => new HashSet<uint>(x.Value)),
        IncludeNq = IncludeNq, IncludeHq = IncludeHq
    };

    public string? Validate()
    {
        if(ItemIds == null || CategoryIds == null || ProtectedItemIds == null || CategoryProtectedItemIds == null)
            return "出售或保护规则缺失。";
        if(!IncludeNq && !IncludeHq) return "至少选择一种允许出售的品质。";
        if(ItemIds.Count == 0 && CategoryIds.Count == 0) return "请选择至少一个出售物品或物品类别。";
        if(ItemIds.Contains(0) || CategoryIds.Contains(0) || ProtectedItemIds.Contains(0) ||
           CategoryProtectedItemIds.Any(x => x.Key == 0 || x.Value == null || x.Value.Contains(0)))
            return "规则含有无效的物品或类别编号。";
        return null;
    }

    public bool Matches(VendorItem item)
    {
        if(item.Container is < 0 or > 3 || item.Slot < 0 || item.ItemId == 0 || item.Quantity <= 0 ||
           !item.Sellable || item.UnitPrice == 0 || (item.Hq ? !IncludeHq : !IncludeNq) ||
           ItemIds == null || CategoryIds == null || ProtectedItemIds == null || CategoryProtectedItemIds == null)
            return false;
        // Every protected item is protected globally, even if its category is disabled or changed.
        if(ProtectedItemIds.Contains(item.ItemId) || CategoryProtectedItemIds.Values.Any(x => x == null || x.Contains(item.ItemId)))
            return false;
        return ItemIds.Contains(item.ItemId) || CategoryIds.Contains(item.CategoryId);
    }
}

[Serializable]
public sealed class VendorDelays
{
    public int StartMs { get; set; } = 1000;
    public int RelogMs { get; set; } = 2000;
    public int LoginStableMs { get; set; } = 3000;
    public int BellMs { get; set; } = 700;
    public int SelectRetainerMs { get; set; } = 700;
    public int TalkMs { get; set; } = 500;
    public int OpenInventoryMs { get; set; } = 700;
    public int ContextMs { get; set; } = 600;
    public int QuantityMs { get; set; } = 700;
    public int ConfirmMs { get; set; } = 700;
    public int ItemIntervalMs { get; set; } = 1000;
    public int CloseMs { get; set; } = 700;
    public int NextCharacterMs { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 90;
    public int RelogTimeoutSeconds { get; set; } = 180;

    public VendorDelays Freeze() => new()
    {
        StartMs = Step(StartMs), RelogMs = Step(RelogMs), LoginStableMs = Step(LoginStableMs), BellMs = Step(BellMs),
        SelectRetainerMs = Step(SelectRetainerMs), TalkMs = Step(TalkMs), OpenInventoryMs = Step(OpenInventoryMs),
        ContextMs = Step(ContextMs), QuantityMs = Step(QuantityMs), ConfirmMs = Step(ConfirmMs),
        ItemIntervalMs = Step(ItemIntervalMs), CloseMs = Step(CloseMs), NextCharacterMs = Step(NextCharacterMs),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 15, 600), RelogTimeoutSeconds = Math.Clamp(RelogTimeoutSeconds, 15, 600)
    };
    private static int Step(int value) => Math.Clamp(value, 100, 60_000);
}

public enum VendorSessionState { Idle, Running, Completed, Stopping, Stopped, Failed }
public interface IVendorSession
{
    VendorSessionState State { get; }
    bool Blocking { get; }
    string Detail { get; }
    long SoldItems { get; }
    long Gil { get; }
    void Begin(ulong cid, SaleRules rules, VendorDelays delays);
    void Tick();
    void Stop();
}
public interface ISweepHost
{
    ulong CurrentCid { get; }
    bool LoggedIn { get; }
    bool IsSwitching { get; }
    bool SafeForSwitch { get; }
    string? Conflict();
    void PrepareCharacter();
    void ChangeCharacter(VendorCharacter character);
}

// Only use when the provider positively confirms that no switch request was queued.
public sealed class CharacterSwitchRejectedException(string message) : Exception(message);

// This attempt was rejected before any native action was sent or queued. A previous session's
// published state/counters are not a result of the rejected Begin.
public sealed class VendorBeginRejectedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public enum SaleCheck { Waiting, Complete, Invalid }
public static class SaleResultRules
{
    public static bool TryExpectedGil(uint unitPrice, int quantity, long gilBefore, out long gilAfter)
    {
        gilAfter = 0;
        if(unitPrice == 0 || quantity < 1 || gilBefore < 0) return false;
        try { gilAfter = checked(gilBefore + checked((long)unitPrice * quantity)); return true; }
        catch(OverflowException) { return false; }
    }

    // Caller freezes the native confirmed unit quote in before.UnitPrice. A vanished/empty source is valid
    // only for selling its entire original quantity. Other slots and native receipt ownership are caller guards.
    public static SaleCheck ObserveSale(VendorItem before, VendorItem? after, long gilBefore, long gilAfter, int quantity)
    {
        if(before.Container is < 0 or > 3 || before.Slot < 0 || before.ItemId == 0 || !before.Sellable ||
           before.Quantity < 1 || quantity < 1 || quantity > before.Quantity ||
           !TryExpectedGil(before.UnitPrice, quantity, gilBefore, out var expectedGil) || gilAfter < gilBefore || gilAfter > expectedGil)
            return SaleCheck.Invalid;
        var remaining = 0;
        if(after != null)
        {
            if(after.Container != before.Container || after.Slot != before.Slot || after.Quantity < 0 || after.Quantity > before.Quantity)
                return SaleCheck.Invalid;
            remaining = after.Quantity;
            if(remaining > 0 && (after.ItemId != before.ItemId || after.Hq != before.Hq)) return SaleCheck.Invalid;
        }
        var decrease = before.Quantity - remaining;
        if(decrease > quantity) return SaleCheck.Invalid;
        return decrease == quantity && gilAfter == expectedGil ? SaleCheck.Complete : SaleCheck.Waiting;
    }
}
