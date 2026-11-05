using System.Text.Json;
using RetainerWorkshop;
using RetainerVendorSweep;

internal static class InventoryPreviewTests
{
    public static void Run(Action<bool, string> check)
    {
        var input = new List<InventoryPreviewEntry>
        {
            new(new(0, 0, 100, false, 500, 10, 5, true), "材料"),
            new(new(1, 1, 100, false, 500, 10, 5, true), "材料"),
            new(new(2, 2, 100, true, 99, 10, 5, true), "材料"),
            new(new(3, 3, 200, false, 12, 20, 3, true), "另一物品"),
            new(new(0, 4, 300, false, 1, 30, 0, false), "不可出售物品"),
            new(new(0, 5, 400, true, 2, 0, 0, false), "未知物品 #400"),
        };
        var snapshot = new InventoryPreviewSnapshot(10, input, DateTimeOffset.UtcNow);
        input.Clear();
        var rules = new SaleRules();
        InventoryPreviewView View() => InventoryPreviewRules.Project(snapshot, rules);
        InventoryPreviewRow Row(uint id) => View().Rows.Single(x => x.ItemId == id);
        check(snapshot.Items.Count == 6, "snapshot freezes input collection");
        check(View().Rows.Length == 4 && View().Slots == 6 && View().Quantity == 1114, "empty rules still show every carried item and stack");
        check(View().MatchedSlots == 0 && View().MatchedQuantity == 0, "empty sale list means zero matching, not empty inventory");
        check(Row(100).NqQuantity == 1000 && Row(100).HqQuantity == 99 && Row(100).Slots == 3, "same item aggregates separate stacks and qualities");
        check(!Row(100).Selected && Row(100).Status.Contains("未选择出售"), "unselected inventory explains why it will not sell");
        check(!Row(300).CanSelect && !Row(400).CanSelect, "unsupported and unknown items remain visible without selection permission");
        check(!InventoryPreviewRules.SetSaleSelection(rules, Row(300), true) && !rules.ItemIds.Contains(300), "unsupported item cannot be added from inventory");
        check(InventoryPreviewRules.SetSaleSelection(rules, Row(100), true) && rules.ItemIds.SetEquals([100u]), "inventory checkbox adds item ID to real sale rules");
        check(!InventoryPreviewRules.SetSaleSelection(rules, Row(100), true), "repeated selection is idempotent");
        check(View().MatchedSlots == 2 && View().MatchedQuantity == 1000, "NQ-only rules count both normal stacks but no HQ");
        check(Row(100).Selected && Row(100).Status.Contains("HQ 未允许"), "excluded quality is explained while ID stays selected");
        rules.IncludeNq = false;
        check(View().MatchedQuantity == 0 && View().Rows.Length == 4, "quality changes do not discard inventory contents");
        rules.IncludeHq = true;
        check(View().MatchedSlots == 1 && View().MatchedQuantity == 99, "HQ-only matches the HQ stack");
        rules.IncludeNq = true;
        check(View().MatchedQuantity == 1099, "both qualities use the same item ID selection");
        check(InventoryPreviewRules.SetProtection(rules, 100, true), "explicit protect click records a global keep rule");
        check(Row(100).Protected && !Row(100).Selected && View().MatchedQuantity == 0, "protected item is visibly excluded despite existing sale ID");
        check(!InventoryPreviewRules.SetSaleSelection(rules, Row(100), true) && rules.ProtectedItemIds.Contains(100), "sale click cannot silently clear protection");
        check(InventoryPreviewRules.SetProtection(rules, 100, false) && View().MatchedQuantity == 1099, "explicit unprotect restores existing sale eligibility");
        check(InventoryPreviewRules.SetSaleSelection(rules, Row(100), false) && !rules.ItemIds.Contains(100) && View().MatchedQuantity == 0, "uncheck removes explicit ID selection");
        rules.CategoryIds.Add(10);
        check(Row(100).Selected && View().MatchedQuantity == 1099, "category selection is reflected by inventory checkbox");
        check(InventoryPreviewRules.SetSaleSelection(rules, Row(100), false) && rules.ProtectedItemIds.Contains(100), "uncheck category-selected item explicitly protects it");
        check(View().MatchedQuantity == 0 && !Row(100).Selected && rules.CategoryIds.Contains(10), "category remains enabled but unchecked item cannot sell");
        check(!InventoryPreviewRules.SetSaleSelection(rules, Row(100), false), "repeated category uncheck changes nothing");
        rules.CategoryProtectedItemIds[99] = [100];
        rules.ProtectedItemIds.Clear();
        check(Row(100).Protected && View().MatchedQuantity == 0, "protection from another category remains globally authoritative");
        check(!InventoryPreviewRules.SetSaleSelection(rules, Row(100), true) && rules.CategoryProtectedItemIds[99].Contains(100), "selection does not remove category protection either");
        check(InventoryPreviewRules.SetProtection(rules, 100, false) && !rules.CategoryProtectedItemIds[99].Contains(100), "explicit unprotect clears all protection entries for this ID");
        check(InventoryPreviewRules.SetSaleSelection(rules, Row(200), true) && rules.ItemIds.Contains(200), "another carried item can be selected independently");
        InventoryPreviewRules.SetSaleSelection(rules, Row(100), false);
        var saved = JsonSerializer.Serialize(rules);
        var restored = JsonSerializer.Deserialize<SaleRules>(saved)!;
        check(restored.ItemIds.Contains(200) && restored.ProtectedItemIds.Contains(100), "inventory selections and category exclusions persist through save/reload");
        check(InventoryPreviewRules.Project(snapshot, restored).MatchedQuantity == 12, "restored selection computes the same actual matches");
        check(snapshot.Items.Count == 6 && View().Quantity == 1114, "all rule edits leave the raw inventory snapshot intact");
        foreach(var entry in snapshot.Items)
            check(restored.Matches(entry.Item) == (entry.Item.ItemId == 200), "preview selection retains actual SaleRules matching semantics");
        var empty = InventoryPreviewRules.Project(new(10, [], DateTimeOffset.UtcNow), restored);
        check(empty.Slots == 0 && empty.Rows.Length == 0 && empty.Quantity == 0, "successful empty bag has its own empty snapshot");
        check(View().Slots > 0 && View().Rows.Any(x => x.ItemId == 400), "unknown item is not silently treated as an empty bag");
        var large = InventoryPreviewRules.Project(new(10, [new(new(0,0,100,false,int.MaxValue,10,5,true), "材料"), new(new(0,1,100,false,int.MaxValue,10,5,true), "材料")], DateTimeOffset.UtcNow), new());
        check(large.Quantity == 2L * int.MaxValue, "totals use long arithmetic across stacks");
    }
}
