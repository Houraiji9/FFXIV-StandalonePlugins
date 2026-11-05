using RetainerVendorSweep;

namespace RetainerWorkshop;

public sealed record InventoryPreviewEntry(VendorItem Item, string Name);
public sealed record InventoryPreviewRow(uint ItemId, uint CategoryId, string Name, long NqQuantity,
    long HqQuantity, int Slots, long MatchedQuantity, bool Selected, bool Protected, bool CanSelect, string Status);
public sealed record InventoryPreviewView(InventoryPreviewRow[] Rows, int Slots, long Quantity,
    int MatchedSlots, long MatchedQuantity);

// A successful read contains every occupied ordinary bag slot, independently of sale rules.
// The UI re-evaluates this immutable snapshot when rules change; selecting an item never rereads game memory.
public sealed class InventoryPreviewSnapshot
{
    public ulong ContentId { get; }
    public DateTimeOffset ReadAt { get; }
    public IReadOnlyList<InventoryPreviewEntry> Items { get; }
    public InventoryPreviewSnapshot(ulong contentId, IEnumerable<InventoryPreviewEntry> items, DateTimeOffset readAt)
    {
        ContentId = contentId; ReadAt = readAt; Items = Array.AsReadOnly(items.ToArray());
    }
}

public static class InventoryPreviewRules
{
    public static bool IsProtected(SaleRules rules, uint id) => rules.ProtectedItemIds.Contains(id)
        || rules.CategoryProtectedItemIds.Values.Any(x => x.Contains(id));
    private static bool Supported(VendorItem item) => item.Container is >= 0 and <= 3 && item.Slot is >= 0 and < 35
        && item.ItemId != 0 && item.Quantity > 0 && item.Sellable && item.UnitPrice > 0;
    private static string Reason(SaleRules rules, VendorItem item)
    {
        if(IsProtected(rules, item.ItemId)) return "白名单保留";
        if(!Supported(item)) return "不可直售或物品资料不可用";
        if(item.Hq ? !rules.IncludeHq : !rules.IncludeNq) return item.Hq ? "HQ 未允许" : "NQ 未允许";
        if(rules.Matches(item)) return "匹配出售规则（执行时再次核验）";
        return "未选择出售";
    }
    public static InventoryPreviewView Project(InventoryPreviewSnapshot snapshot, SaleRules rules)
    {
        var rows = snapshot.Items.GroupBy(x => x.Item.ItemId).Select(group =>
        {
            var first = group.First(); var id = group.Key;
            var protectedItem = IsProtected(rules, id);
            return new InventoryPreviewRow(id, first.Item.CategoryId, first.Name,
                group.Where(x => !x.Item.Hq).Sum(x => (long)x.Item.Quantity),
                group.Where(x => x.Item.Hq).Sum(x => (long)x.Item.Quantity), group.Count(),
                group.Where(x => rules.Matches(x.Item)).Sum(x => (long)x.Item.Quantity),
                !protectedItem && (rules.ItemIds.Contains(id) || rules.CategoryIds.Contains(first.Item.CategoryId)),
                protectedItem, group.Any(x => Supported(x.Item)),
                string.Join("；", group.Select(x => Reason(rules, x.Item)).Distinct()));
        }).ToArray();
        var matched = snapshot.Items.Where(x => rules.Matches(x.Item)).ToArray();
        return new(rows, snapshot.Items.Count, snapshot.Items.Sum(x => (long)x.Item.Quantity),
            matched.Length, matched.Sum(x => (long)x.Item.Quantity));
    }
    public static bool SetSaleSelection(SaleRules rules, InventoryPreviewRow row, bool selected)
    {
        if(selected)
        {
            // A sale click never silently removes a previously protected item.
            if(!row.CanSelect || IsProtected(rules, row.ItemId)) return false;
            return rules.ItemIds.Add(row.ItemId);
        }
        var changed = rules.ItemIds.Remove(row.ItemId);
        // Removing an explicit ID is insufficient when a category still selects the item.
        if(rules.CategoryIds.Contains(row.CategoryId)) changed |= rules.ProtectedItemIds.Add(row.ItemId);
        return changed;
    }
    public static bool SetProtection(SaleRules rules, uint id, bool protect)
    {
        if(protect) return rules.ProtectedItemIds.Add(id);
        var changed = rules.ProtectedItemIds.Remove(id);
        foreach(var list in rules.CategoryProtectedItemIds.Values) changed |= list.Remove(id);
        return changed;
    }
}
