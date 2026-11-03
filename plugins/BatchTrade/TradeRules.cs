using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchTrade;

public readonly record struct ItemKey(uint Id, bool HighQuality);
public readonly record struct BagSlot(int Container, int Slot, ItemKey Item, int Quantity);
public readonly record struct PlannedSlot(BagSlot Source, int Quantity);

public enum InventoryChange { Unchanged, PartialExpectedDecrease, ExpectedDecrease, Unexpected }
public enum CompletionResult { Waiting, Complete, Invalid }

public static class CompletionGate
{
    // Signals may arrive in any order. Nothing here can authorize a retry.
    public static CompletionResult Evaluate(bool itemSentReceipt, InventoryChange inventory, bool originalTradeEnded) =>
        inventory == InventoryChange.Unexpected ? CompletionResult.Invalid :
        itemSentReceipt && inventory == InventoryChange.ExpectedDecrease && originalTradeEnded ? CompletionResult.Complete :
        CompletionResult.Waiting;
}

/// <summary>Pure planning and validation; contains no game access.</summary>
public static class TradeRules
{
    public static List<PlannedSlot> Plan(IReadOnlyList<BagSlot> inventory, ItemKey item, int remaining)
    {
        if (item.Id == 0 || remaining <= 0) throw new ArgumentOutOfRangeException(nameof(remaining));
        var result = new List<PlannedSlot>(5);
        foreach (var slot in inventory)
        {
            if (slot.Item != item || slot.Quantity <= 0) continue;
            var amount = Math.Min(slot.Quantity, remaining);
            result.Add(new(slot, amount));
            remaining -= amount;
            if (remaining == 0 || result.Count == 5) break;
        }
        return result;
    }

    public static Dictionary<ItemKey, long> Totals(IEnumerable<BagSlot> slots) =>
        slots.Where(s => s.Item.Id != 0 && s.Quantity > 0)
             .GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => (long)s.Quantity));

    public static bool SameInventory(IReadOnlyDictionary<ItemKey, long> before,
        IReadOnlyDictionary<ItemKey, long> after, ItemKey selected, long expectedDecrease = 0)
    {
        foreach (var key in before.Keys.Concat(after.Keys).Distinct())
        {
            var expected = before.GetValueOrDefault(key) - (key == selected ? expectedDecrease : 0);
            if (expected < 0 || after.GetValueOrDefault(key) != expected) return false;
        }
        return true;
    }

    public static InventoryChange ClassifySettlement(IReadOnlyDictionary<ItemKey, long> before,
        IReadOnlyDictionary<ItemKey, long> after, ItemKey selected, int expectedDecrease)
    {
        foreach (var key in before.Keys.Concat(after.Keys).Distinct().Where(k => k != selected))
            if (before.GetValueOrDefault(key) != after.GetValueOrDefault(key)) return InventoryChange.Unexpected;
        var decrease = before.GetValueOrDefault(selected) - after.GetValueOrDefault(selected);
        if (decrease < 0 || decrease > expectedDecrease) return InventoryChange.Unexpected;
        return decrease == 0 ? InventoryChange.Unchanged : decrease == expectedDecrease
            ? InventoryChange.ExpectedDecrease : InventoryChange.PartialExpectedDecrease;
    }

    public static bool ValidOffer(IEnumerable<BagSlot> offered, ItemKey selected, int quantity, int gil)
    {
        var list = offered.Where(s => s.Item.Id != 0 || s.Quantity != 0).ToArray();
        return gil == 0 && list.Length <= 5 && list.All(s => s.Item == selected && s.Quantity > 0)
            && list.Sum(s => (long)s.Quantity) == quantity;
    }

    public static bool EmptyRemote(IEnumerable<BagSlot> offered, int gil) =>
        gil == 0 && offered.All(s => s.Item.Id == 0 && s.Quantity == 0);
}
