namespace BatchTrade;

// RawWorking is read from HandIn.Items[i], never GetInventorySlot(i), which resolves its link.
// LocalCache is the separately refreshed, resolved item/value view; it has no source-link authority.
public readonly record struct WorkingTradeSlot(ItemKey Item, int Quantity, bool Symbolic, int LinkedContainer, int LinkedSlot);
public sealed record TradeOfferSnapshot(bool WorkingAvailable, WorkingTradeSlot[] RawWorking, BagSlot[] Sources,
    BagSlot[] LocalCache, BagSlot[] RemoteCache, int LocalGil, int RemoteGil, bool SyncPending);
public enum OfferCheckCode
{
    Ready, QuantityReady, WaitingWorking, WaitingQuantity, WaitingCache, WaitingSync,
    InvalidLayout = 100, LocalGil, RemoteOffer, NegativeQuantity, WorkingUnexpectedSlot,
    WorkingNotSymbolic, SourceContainer, SourceSlot, SourceItem, SourceQuantity,
    WorkingItem, WorkingQuantity
}
public readonly record struct OfferCheck(OfferCheckCode Code, int Slot = -1)
{
    public bool Failed => Code >= OfferCheckCode.InvalidLayout;
}

public static class TradeOfferRules
{
    // completed is an exact prefix. pending=true allows only the next slot's known native transition:
    // empty -> linked quantity 1 -> this plugin's submitted quantity. Earlier slots never get that allowance.
    public static OfferCheck Check(TradeOfferSnapshot view, IReadOnlyList<PlannedSlot> plan, int completed,
        bool pending = false, bool quantitySubmitted = false)
    {
        if (view.LocalCache.Length != 5 || view.RemoteCache.Length != 5 || plan.Count > 5 || completed < 0 ||
            completed > plan.Count || pending && completed >= plan.Count || view.Sources.Length != plan.Count)
            return new(OfferCheckCode.InvalidLayout);
        if (view.LocalGil != 0) return new(OfferCheckCode.LocalGil);
        if (!TradeRules.EmptyRemote(view.RemoteCache, view.RemoteGil)) return new(OfferCheckCode.RemoteOffer);
        if (view.LocalCache.Any(x => x.Quantity < 0)) return new(OfferCheckCode.NegativeQuantity);
        if (!view.WorkingAvailable || view.RawWorking.Length != 5) return new(OfferCheckCode.WaitingWorking);
        var waitingWorking = false;
        var activeCount = completed + (pending ? 1 : 0);
        for (var i = 0; i < 5; i++)
        {
            var slot = view.RawWorking[i];
            if (slot.Quantity < 0) return new(OfferCheckCode.NegativeQuantity, i);
            if (i >= activeCount)
            {
                if (slot.Quantity > 0) return new(OfferCheckCode.WorkingUnexpectedSlot, i);
                // Quantity-zero cache entries are empty; a raw ghost link can still occupy a native slot.
                // Wait for raw clearing instead of authorizing the next fill or final confirmation.
                if (slot.Symbolic || slot.Item.Id != 0) waitingWorking = true;
                continue;
            }
            if (i == completed && pending && slot.Quantity == 0 && !quantitySubmitted)
            { waitingWorking = true; continue; }
            if (!slot.Symbolic) return new(OfferCheckCode.WorkingNotSymbolic, i);
            var planned = plan[i];
            if (slot.LinkedContainer != planned.Source.Container) return new(OfferCheckCode.SourceContainer, i);
            if (slot.LinkedSlot != planned.Source.Slot) return new(OfferCheckCode.SourceSlot, i);
            var source = view.Sources[i];
            if (source.Item != planned.Source.Item) return new(OfferCheckCode.SourceItem, i);
            if (source.Quantity != planned.Source.Quantity) return new(OfferCheckCode.SourceQuantity, i);
            if (slot.Item != planned.Source.Item) return new(OfferCheckCode.WorkingItem, i);
            if (i < completed)
            {
                if (slot.Quantity != planned.Quantity) return new(OfferCheckCode.WorkingQuantity, i);
            }
            else if (slot.Quantity != 1 && !(quantitySubmitted && slot.Quantity == planned.Quantity))
                return new(OfferCheckCode.WorkingQuantity, i);
        }
        if (waitingWorking) return new(OfferCheckCode.WaitingWorking);
        if (pending)
        {
            var planned = plan[completed];
            if (!quantitySubmitted && planned.Source.Quantity > 1) return new(OfferCheckCode.QuantityReady, completed);
            if (view.RawWorking[completed].Quantity != planned.Quantity) return new(OfferCheckCode.WaitingQuantity, completed);
        }
        // Every action rechecks these gates. Previously observed agreement is never retained as authorization.
        // Any stale local-cache value can lag native working state; it authorizes no action until equal.
        for (var i = 0; i < 5; i++)
        {
            var raw = view.RawWorking[i]; var cache = view.LocalCache[i];
            if (raw.Quantity != cache.Quantity || raw.Quantity > 0 && raw.Item != cache.Item)
                return new(OfferCheckCode.WaitingCache, i);
        }
        return new(view.SyncPending ? OfferCheckCode.WaitingSync : OfferCheckCode.Ready);
    }
}
