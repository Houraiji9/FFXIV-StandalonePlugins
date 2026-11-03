using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchTrade;

public readonly record struct ItemKey(uint Id, bool HighQuality);
public readonly record struct BagSlot(int Container, int Slot, ItemKey Item, int Quantity);
public readonly record struct PlannedSlot(BagSlot Source, int Quantity);

public enum InventoryChange { Unchanged, PartialExpectedDecrease, ExpectedDecrease, Unexpected }
public enum CompletionResult { Waiting, Complete, Invalid }
public enum PopupOwner { Missing, InventoryQuantity, InventoryFinal, Other }
public enum NumericCheck { Ready, ActionNotBound, SourceChanged, TradeChanged, OwnerMissing, OwnerUnexpected, MappingChanged, PromptMismatch, InvalidRange }

public readonly record struct NumericPopupState(bool ActionBound, bool SourceMatches, bool TradeMatches,
    PopupOwner Owner, bool MappingUnchanged, bool PromptMatches, int Minimum, int Maximum,
    int Amount, int SourceQuantity, int ItemStackLimit, int RemainingBatchCap);

public static class NumericPopupRules
{
    public static NumericCheck Check(NumericPopupState state)
    {
        if (!state.ActionBound) return NumericCheck.ActionNotBound;
        if (!state.SourceMatches) return NumericCheck.SourceChanged;
        if (!state.TradeMatches) return NumericCheck.TradeChanged;
        if (state.Owner == PopupOwner.Missing) return NumericCheck.OwnerMissing;
        if (state.Owner != PopupOwner.InventoryQuantity) return NumericCheck.OwnerUnexpected;
        if (!state.MappingUnchanged) return NumericCheck.MappingChanged;
        if (!state.PromptMatches) return NumericCheck.PromptMismatch;
        var limit = Math.Min(state.SourceQuantity, Math.Min(state.ItemStackLimit, state.RemainingBatchCap));
        return state.Minimum >= 0 && state.Maximum >= state.Minimum && state.Amount >= Math.Max(1, state.Minimum) &&
            state.Amount <= state.Maximum && state.Amount <= limit
                ? NumericCheck.Ready : NumericCheck.InvalidRange;
    }
}

public enum TradeActionKind { Request, OpenMenu, SelectMenu, Quantity, CloseMenu, Propose, FinalConfirm, Cancel }

public readonly record struct TradeDelays(int Startup, int Request, int OpenMenu, int SelectMenu, int Quantity,
    int CloseMenu, int Propose, int FinalConfirm, int Cancel, int BetweenTrades)
{
    public TradeDelays Normalize() => new(Math.Clamp(Startup, 0, 600_000), Step(Request), Step(OpenMenu),
        Step(SelectMenu), Step(Quantity), Step(CloseMenu), Step(Propose), Step(FinalConfirm),
        Step(Cancel), Math.Clamp(BetweenTrades, 0, 600_000));
    private static int Step(int value) => Math.Clamp(value, 100, 30_000);
}

/// <summary>One native action per framework frame, at least 100 ms apart; intentional delay excludes waiting budget.</summary>
public sealed class TradeClock
{
    public long ActionAt { get; private set; }
    public long Deadline { get; private set; }
    public TradeActionKind? PendingAction { get; private set; }
    private bool waiting;
    private long lastFrame = -1;
    private long lastActionAt = long.MinValue / 2;

    public void BeginDelay(long now, int milliseconds)
    {
        CancelPending();
        ActionAt = now + Math.Max(0, milliseconds);
    }

    public void BeginBatch(long now, int timeoutMilliseconds)
    {
        CancelPending();
        waiting = true;
        ActionAt = now;
        Deadline = now + timeoutMilliseconds;
    }

    public void Schedule(TradeActionKind action, long now, int milliseconds)
    {
        if (PendingAction == action) return;
        if (PendingAction != null) throw new InvalidOperationException("A different native step is still pending.");
        PendingAction = action;
        ActionAt = Math.Max(now + Math.Max(100, milliseconds), lastActionAt + 100);
        if (waiting) Deadline += ActionAt - now;
    }

    public bool TryDispatch(TradeActionKind action, long now, long frame)
    {
        if (PendingAction != action || now < ActionAt || frame == lastFrame || now - lastActionAt < 100) return false;
        PendingAction = null;
        lastFrame = frame;
        lastActionAt = now;
        return true;
    }

    public void CancelPending()
    {
        PendingAction = null;
        waiting = false;
        Deadline = 0;
        ActionAt = 0;
    }

    public void LimitSettlement(long now, int milliseconds) => Deadline = Math.Min(Deadline, now + milliseconds);
    public bool CanAct(long now) => now >= ActionAt;
    public bool TimedOut(long now) => waiting && now >= Deadline;
    public int DelayRemaining(long now) => (int)Math.Clamp(ActionAt - now, 0, int.MaxValue);
}

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
    public static bool PendingSlotMatches(IReadOnlyList<BagSlot> offered, IReadOnlyList<PlannedSlot> plan,
        int filling, ItemKey selected, int gil, bool quantitySubmitted)
    {
        if (gil != 0 || offered.Count != 5 || filling < 0 || filling >= plan.Count || plan.Count > 5) return false;
        for (var i = 0; i < offered.Count; i++)
        {
            var slot = offered[i];
            if (i < filling)
            {
                if (slot.Item != selected || slot.Quantity != plan[i].Quantity) return false;
            }
            else if (i == filling)
            {
                if (slot.Item.Id == 0 && slot.Quantity == 0) continue;
                // MoveItemSlot creates a symbolic Trade slot with Quantity=1 before opening InputNumeric.
                if (slot.Item != selected || (slot.Quantity != 1 && !(quantitySubmitted && slot.Quantity == plan[i].Quantity))) return false;
            }
            else if (slot.Item.Id != 0 || slot.Quantity != 0) return false;
        }
        return true;
    }

    public static bool IsSlotComplete(IReadOnlyList<BagSlot> offered, IReadOnlyList<PlannedSlot> plan,
        int filling, ItemKey selected, int gil, bool quantitySubmitted) =>
        PendingSlotMatches(offered, plan, filling, selected, gil, quantitySubmitted) &&
        (quantitySubmitted || plan[filling].Source.Quantity == 1) &&
        offered[filling].Item == selected && offered[filling].Quantity == plan[filling].Quantity;

    public static List<PlannedSlot> Plan(IReadOnlyList<BagSlot> inventory, ItemKey item, int remaining,
        int batchCap = 999, int itemStackLimit = 999)
    {
        if (item.Id == 0 || remaining <= 0) throw new ArgumentOutOfRangeException(nameof(remaining));
        if (batchCap is < 1 or > 999 || itemStackLimit < 1) throw new ArgumentOutOfRangeException(nameof(batchCap));
        remaining = Math.Min(remaining, batchCap);
        var result = new List<PlannedSlot>(5);
        foreach (var slot in inventory)
        {
            if (slot.Item != item || slot.Quantity <= 0) continue;
            var amount = Math.Min(slot.Quantity, Math.Min(remaining, itemStackLimit));
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
