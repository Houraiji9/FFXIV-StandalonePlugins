using BatchTrade;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
var item = new ItemKey(100, false);
var hq = new ItemKey(100, true);
var other = new ItemKey(101, false);
var slots = Enumerable.Range(0, 8).Select(i => new BagSlot(0, i, item, 99)).ToList();
slots.Add(new(1, 1, hq, 99));
var plan = TradeRules.Plan(slots, item, 550);
Check(plan.Count == 5 && plan.Sum(p => p.Quantity) == 495, "five-slot maximum");
plan = TradeRules.Plan(slots, item, 201);
Check(plan.Count == 3 && plan[2].Quantity == 3, "partial final stack");
Check(TradeRules.Plan(slots, hq, 20).Single().Source.Item == hq, "quality isolation");
var before = new Dictionary<ItemKey, long> { [item] = 500, [hq] = 10, [other] = 25 };
var after = new Dictionary<ItemKey, long> { [item] = 302, [hq] = 10, [other] = 25 };
Check(TradeRules.SameInventory(before, after, item, 198), "exact completion delta");
Check(!TradeRules.SameInventory(before, after, item, 99), "wrong quantity rejected");
after[other] = 26;
Check(!TradeRules.SameInventory(before, after, item, 198), "unrelated inventory mutation rejected");
Check(!TradeRules.ValidOffer([new(0, 0, hq, 5)], item, 5, 0), "wrong HQ offer rejected");
Check(!TradeRules.ValidOffer([new(0, 0, item, 5)], item, 5, 1), "outbound gil rejected");
Check(!TradeRules.EmptyRemote([new(0, 0, other, 1)], 0), "inbound item rejected");
Check(!TradeRules.EmptyRemote([], 1), "inbound gil rejected");
Check(TradeRules.ValidOffer([new(0, 0, item, 99), new(0, 1, item, 3)], item, 102, 0), "same item multiple stacks accepted");
Check(!TradeRules.ValidOffer(Enumerable.Range(0, 6).Select(i => new BagSlot(0, i, item, 1)), item, 6, 0), "six slots rejected");
foreach (var order in new[] { "RIE", "REI", "IRE", "IER", "ERI", "EIR" })
{
    var receipt = false; var changed = InventoryChange.Unchanged; var ended = false;
    for (var i = 0; i < order.Length; i++)
    {
        if (order[i] == 'R') receipt = true;
        if (order[i] == 'I') changed = InventoryChange.ExpectedDecrease;
        if (order[i] == 'E') ended = true;
        Check(CompletionGate.Evaluate(receipt, changed, ended) == (i == 2 ? CompletionResult.Complete : CompletionResult.Waiting), $"signal order {order} step {i}");
    }
}
Check(CompletionGate.Evaluate(false, InventoryChange.ExpectedDecrease, true) == CompletionResult.Waiting, "inventory plus close without receipt never completes");
Check(CompletionGate.Evaluate(true, InventoryChange.Unchanged, true) == CompletionResult.Waiting, "receipt plus close without inventory never completes");
Check(CompletionGate.Evaluate(false, InventoryChange.Unchanged, true) == CompletionResult.Waiting, "cancelled empty trade never completes");
Check(CompletionGate.Evaluate(true, InventoryChange.PartialExpectedDecrease, true) == CompletionResult.Waiting, "partial per-slot inventory updates wait");
Check(CompletionGate.Evaluate(true, InventoryChange.Unexpected, true) == CompletionResult.Invalid, "unexpected inventory is terminal");
var partial = new Dictionary<ItemKey, long>(before) { [item] = 401 };
Check(TradeRules.ClassifySettlement(before, partial, item, 198) == InventoryChange.PartialExpectedDecrease, "partial expected decrease classified");
partial[item] = 301;
Check(TradeRules.ClassifySettlement(before, partial, item, 198) == InventoryChange.Unexpected, "too much sent rejected");
partial[item] = 501;
Check(TradeRules.ClassifySettlement(before, partial, item, 198) == InventoryChange.Unexpected, "unexpected receiving rejected");
// Per-slot cap: a transaction can use up to five distinct source stacks.
List<int> SimulateBatches(int cap)
{
    var inventory = new List<BagSlot> { new(0, 0, item, 999), new(0, 1, item, 999), new(0, 2, item, 999), new(0, 3, item, 262) };
    var remaining = 3259;
    var results = new List<int>();
    while (remaining > 0)
    {
        var next = TradeRules.Plan(inventory, item, remaining, cap, 999);
        var quantity = next.Sum(x => x.Quantity);
        if (quantity <= 0) throw new Exception("planner did not make progress");
        results.Add(quantity);
        foreach (var slot in next)
        {
            var at = inventory.FindIndex(x => x.Container == slot.Source.Container && x.Slot == slot.Source.Slot);
            inventory[at] = inventory[at] with { Quantity = inventory[at].Quantity - slot.Quantity };
        }
        remaining -= quantity;
    }
    return results;
}
Check(SimulateBatches(999).SequenceEqual(new[] { 3259 }), "3259 fits in four slots of one transaction");
var capped99 = SimulateBatches(99);
Check(capped99.Count == 11 && capped99.Sum() == 3259 && capped99.All(x => x <= 4 * 99), "cap99 limits individual slots rather than transaction total");
var capped1 = SimulateBatches(1);
Check(capped1.Count == 999 && capped1.Sum() == 3259 && capped1.All(x => x is >= 1 and <= 4), "cap1 permits one unit from each distinct source per transaction");
plan = TradeRules.Plan(slots, item, 1000, 150, 99);
Check(plan.Count == 5 && plan.All(x => x.Quantity == 99), "native item stack limit applies independently to all five slots");
plan = TradeRules.Plan(Enumerable.Range(0, 8).Select(i => new BagSlot(0, i, item, 1)).ToList(), item, 8, 999, 1);
Check(plan.Count == 5 && plan.Sum(x => x.Quantity) == 5, "nonstackable items remain limited to five physical slots");
Check(TradeRules.Plan([new(0, 0, item, 999)], item, 999, 999, 99).Single().Quantity == 99, "per-slot stack limit preserved");
Check(TradeRules.Plan(slots, hq, 20, 1, 99).Single().Source.Item == hq, "small cap still isolates quality");
foreach (var invalid in new[] { 0, 1000 })
{
    var rejected = false;
    try { TradeRules.Plan(slots, item, 100, invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, $"out of range cap {invalid} rejected");
}

var popup = new NumericPopupState(true, true, true, PopupOwner.InventoryQuantity, true, true, 1, 999, 999, 9999, 9999, 999);
Check(NumericPopupRules.Check(popup) == NumericCheck.Ready, "numeric max may be 999 while source quantity is larger");
Check(NumericPopupRules.Check(popup with { Amount = 99, SlotQuantityCap = 99 }) == NumericCheck.Ready, "numeric slider can exceed chosen cap while submitted amount cannot");
Check(NumericPopupRules.Check(popup with { Owner = PopupOwner.Missing }) == NumericCheck.OwnerMissing, "missing registration is distinct from wrong ownership");
Check(NumericPopupRules.Check(popup with { Owner = PopupOwner.Other }) == NumericCheck.OwnerUnexpected, "arbitrary or agent-owned numeric dialog rejected");
Check(NumericPopupRules.Check(popup with { Owner = PopupOwner.InventoryFinal }) == NumericCheck.OwnerUnexpected, "final confirmation receiver is not a quantity receiver");
Check(NumericPopupRules.Check(popup with { ActionBound = false }) == NumericCheck.ActionNotBound, "new dialog without current menu generation rejected");
Check(NumericPopupRules.Check(popup with { SourceMatches = false }) == NumericCheck.SourceChanged, "different source slot rejected");
Check(NumericPopupRules.Check(popup with { TradeMatches = false }) == NumericCheck.TradeChanged, "different recipient rejected");
Check(NumericPopupRules.Check(popup with { MappingUnchanged = false }) == NumericCheck.MappingChanged, "changed receiver or event kind rejected");
Check(NumericPopupRules.Check(popup with { PromptMatches = false }) == NumericCheck.PromptMismatch, "non-trade quantity prompt rejected");
Check(NumericPopupRules.Check(popup with { Maximum = 998 }) == NumericCheck.InvalidRange, "planned amount must fit actual max");
Check(NumericPopupRules.Check(popup with { Minimum = 1000 }) == NumericCheck.InvalidRange, "planned amount must meet actual min");
Check(NumericPopupRules.Check(popup with { Amount = 1000 }) == NumericCheck.InvalidRange, "cap999 cannot submit1000");
Check(NumericPopupRules.Check(popup with { SourceQuantity = 998 }) == NumericCheck.InvalidRange, "amount cannot exceed actual source");
Check(NumericPopupRules.Check(popup with { ItemStackLimit = 99 }) == NumericCheck.InvalidRange, "amount cannot exceed item stack limit");
Check(NumericPopupRules.Check(popup with { Minimum = -1 }) == NumericCheck.InvalidRange, "negative bound rejected");
Check(NumericPopupRules.Check(popup with { Amount = 0 }) == NumericCheck.InvalidRange, "zero amount rejected");

Check(GameCompatibility.IsSupported("2026.10.06.0000.0000", 0x3730), "verified client and layout accepted");
Check(!GameCompatibility.IsSupported(null, 0x3730), "unknown client version rejected");
Check(!GameCompatibility.IsSupported("2026.10.07.0000.0000", 0x3730), "future client version rejected");
Check(!GameCompatibility.IsSupported(GameCompatibility.RequiredVersion, 0x4000), "changed SDK layout rejected");
Check(GameCompatibility.IsQuantityReceiver(0x10000, 0x13708, 0), "verified inventory numeric receiver accepted");
Check(!GameCompatibility.IsQuantityReceiver(0x10000, 0x13708, 1), "wrong numeric event kind rejected");
Check(!GameCompatibility.IsQuantityReceiver(0x20000, 0x13708, 0), "receiver from a different inventory manager rejected");
Check(!GameCompatibility.IsQuantityReceiver(0, 0x3708, 0), "null inventory manager rejected");
Check(GameCompatibility.IsFinalReceiver(0x10000, 0x136E8, 0), "verified final confirmation receiver accepted");
Check(!GameCompatibility.IsFinalReceiver(0x10000, 0x13708, 0), "quantity receiver cannot confirm final trade");

var delays = new TradeDelays(123, 234, 345, 456, 567, 678, 789, 890, 901, 12000).Normalize();
Check(delays == new TradeDelays(123, 234, 345, 456, 567, 678, 789, 890, 901, 12000), "all ten independent timings survive request normalization");
Check((delays with { Quantity = 0 }).Normalize().Quantity == 100, "automatic actions have a 100ms minimum");
Check((delays with { FinalConfirm = 60000 }).Normalize().FinalConfirm == 30000, "automatic delay supports up to30 seconds");
var timing = new TradeClock();
timing.BeginDelay(1000, 60000);
Check(!timing.CanAct(60999) && timing.CanAct(61000), "startup delay boundary");
Check(!timing.TimedOut(1000000), "startup delay never runs a batch response timeout");
timing.BeginBatch(61000, 15000);
timing.Schedule(TradeActionKind.OpenMenu, 61000, 30000);
Check(timing.ActionAt == 91000 && timing.Deadline == 106000, "long operation delay excluded from15s response budget");
timing.Schedule(TradeActionKind.OpenMenu, 65000, 30000);
Check(timing.ActionAt == 91000 && timing.Deadline == 106000, "repeated scheduling does not reset delay or add response budget");
Check(!timing.TryDispatch(TradeActionKind.OpenMenu, 90999, 1), "cannot dispatch before configured step delay");
Check(timing.TryDispatch(TradeActionKind.OpenMenu, 91000, 2), "first step dispatches when its own delay is complete");
timing.Schedule(TradeActionKind.SelectMenu, 91000, 20000);
Check(timing.Deadline == 126000 && !timing.TimedOut(111000), "multiple long steps still exclude intentional waits");
Check(!timing.TryDispatch(TradeActionKind.SelectMenu, 111000, 2), "never sends two native actions in one frame");
Check(timing.TryDispatch(TradeActionKind.SelectMenu, 111000, 3), "next step can dispatch on a later frame");
timing.Schedule(TradeActionKind.Quantity, 111000, 0);
Check(!timing.TryDispatch(TradeActionKind.Quantity, 111099, 4), "distinct frames still require100ms minimum");
timing.CancelPending();
Check(!timing.TryDispatch(TradeActionKind.Quantity, 200000, 5), "stop revokes an already due quantity action");
timing.Schedule(TradeActionKind.Cancel, 200000, 300);
Check(!timing.TryDispatch(TradeActionKind.Cancel, 200299, 5) && timing.TryDispatch(TradeActionKind.Cancel, 200300, 6), "cancel also uses a delayed nonnested native step");
Check(!timing.TryDispatch(TradeActionKind.Cancel, 200400, 7), "cancel is consumed only once");
timing.BeginDelay(200300, 600000);
Check(!timing.TimedOut(800300) && !timing.CanAct(800299) && timing.CanAct(800300), "ten minute trade interval has no10s or90s response timeout");
timing.BeginBatch(800300, 15000);
Check(timing.Deadline == 815300, "each batch starts a new response budget");
timing.LimitSettlement(801000, 5000);
Check(!timing.TimedOut(805999) && timing.TimedOut(806000), "settlement signal starts bounded reconciliation window");
var closeTiming = new TradeClock();
closeTiming.BeginBatch(0, 15000);
closeTiming.Schedule(TradeActionKind.CloseMenu, 0, 500);
// Menu disappears by itself at 200ms: production still consumes the same delayed step before advancing.
closeTiming.Schedule(TradeActionKind.CloseMenu, 200, 500);
Check(closeTiming.TryDispatch(TradeActionKind.CloseMenu, 500, 1), "menu disappearing during close delay does not leave a stale action");
closeTiming.Schedule(TradeActionKind.Propose, 500, 700);
Check(closeTiming.PendingAction == TradeActionKind.Propose && closeTiming.ActionAt == 1200, "next phase keeps its own independent delay after a no-op menu close");
// Native Trade slots start as symbolic source links with a temporary quantity of one.
List<BagSlot> Offer(params BagSlot[] values) => values.Concat(Enumerable.Range(values.Length, 5 - values.Length).Select(i => new BagSlot(0, i, default, 0))).ToList();
var partialPlan = TradeRules.Plan([new(0, 0, item, 999)], item, 262, 999, 999);
var temporaryOne = Offer(new BagSlot(0, 0, item, 1));
Check(TradeRules.PendingSlotMatches(temporaryOne, partialPlan, 0, item, 0, false), "temporary symbolic quantity1 is accepted while numeric is pending");
Check(!TradeRules.IsSlotComplete(temporaryOne, partialPlan, 0, item, 0, false), "temporary1 does not complete partial stack262");
var onePlan = TradeRules.Plan([new(0, 0, item, 999)], item, 1, 1, 999);
Check(!TradeRules.IsSlotComplete(temporaryOne, onePlan, 0, item, 0, false), "cap1 still requires own numeric callback when source stack exceeds1");
Check(TradeRules.IsSlotComplete(temporaryOne, onePlan, 0, item, 0, true), "cap1 completes only after quantity submission");
Check(TradeRules.IsSlotComplete(Offer(new BagSlot(0, 0, item, 262)), partialPlan, 0, item, 0, true), "partial262 completes after numeric submission and exact offer");
Check(!TradeRules.PendingSlotMatches(Offer(new BagSlot(0, 0, item, 262)), partialPlan, 0, item, 0, false), "unsubmitted externally changed quantity rejected");
var singlePlan = TradeRules.Plan([new(0, 0, item, 1)], item, 1, 999, 1);
Check(TradeRules.IsSlotComplete(temporaryOne, singlePlan, 0, item, 0, false), "source quantity1 follows verified no-numeric native path");
var twoPlan = TradeRules.Plan([new(0, 0, item, 99), new(0, 1, item, 99)], item, 150, 150, 99);
Check(TradeRules.PendingSlotMatches(Offer(new BagSlot(0, 0, item, 99), new(0, 1, item, 1)), twoPlan, 1, item, 0, false), "previous slot exact and next temporary1 accepted");
Check(!TradeRules.PendingSlotMatches(Offer(new BagSlot(0, 0, item, 98), new(0, 1, item, 1)), twoPlan, 1, item, 0, false), "previously filled slot mutation rejected even during numeric delay");
Check(!TradeRules.PendingSlotMatches(Offer(new BagSlot(0, 0, item, 99), new(0, 1, hq, 1)), twoPlan, 1, item, 0, false), "temporary slot wrong quality rejected");
Check(!TradeRules.PendingSlotMatches(Offer(new BagSlot(0, 0, item, 1), new(0, 1, other, 1)), partialPlan, 0, item, 0, false), "unexpected additional slot rejected");
Check(!TradeRules.PendingSlotMatches(temporaryOne, partialPlan, 0, item, 1, false), "gil cannot hide behind pending numeric");
Check(NumericPopupRules.Check(popup with { Amount = 99, SourceQuantity = 99, ItemStackLimit = 99, Maximum = 999, SlotQuantityCap = 99 }) == NumericCheck.Ready,
    "popup upper range may exceed source but the planned submission never does");
Check(GameCompatibility.IsSupported("2026.10.06.0000.0000\r\n", 0x3730), "version file trailing newline normalized before exact check");
// v0.1.2 reproduces the native dual view: raw HandIn slots are symbolic, local cache is resolved.
TradeOfferSnapshot View(IReadOnlyList<PlannedSlot> expected, params int[] quantities)
{
    var raw = new WorkingTradeSlot[5];
    var cache = Offer().ToArray();
    for (var i = 0; i < quantities.Length; i++)
    {
        var source = expected[i].Source;
        raw[i] = new(source.Item, quantities[i], true, source.Container, source.Slot);
        // The cache's physical container/slot can be copied from the source; no raw-link contract exists here.
        cache[i] = source with { Quantity = quantities[i] };
    }
    return new(true, raw, expected.Select(x => x.Source).ToArray(), cache, Offer().ToArray(), 0, 0, false);
}
var dual = View(partialPlan, 1);
Check(TradeOfferRules.Check(dual, partialPlan, 0, true).Code == OfferCheckCode.QuantityReady,
    "raw symbolic source plus normalized nonsymbolic cache authorizes own quantity submission");
var rawEmpty = View(partialPlan);
Check(TradeOfferRules.Check(rawEmpty, partialPlan, 0, true).Code == OfferCheckCode.WaitingWorking,
    "empty working slot never authorizes quantity even when popup ownership would be valid");
Check(TradeOfferRules.Check(dual with { WorkingAvailable = false }, partialPlan, 0, true).Code == OfferCheckCode.WaitingWorking,
    "unavailable or nonowned working container never authorizes quantity");
var completedDual = View(partialPlan, 262);
Check(TradeOfferRules.Check(completedDual, partialPlan, 0, true, true).Code == OfferCheckCode.Ready,
    "own numeric submission and exact working plus resolved cache complete a partial stack");
Check(TradeOfferRules.Check(completedDual, partialPlan, 0, true).Code == OfferCheckCode.WorkingQuantity,
    "externally changed raw quantity is rejected before own numeric submission");
Check(TradeOfferRules.Check(dual, onePlan, 0, true).Code == OfferCheckCode.QuantityReady,
    "cap1 with a larger source still requires own numeric despite both views already showing1");
Check(TradeOfferRules.Check(dual, onePlan, 0, true, true).Code == OfferCheckCode.Ready,
    "cap1 becomes ready only after its own submission");
Check(TradeOfferRules.Check(View(singlePlan, 1), singlePlan, 0, true).Code == OfferCheckCode.Ready,
    "single-item source skips numeric only with valid raw source and synchronized cache");
var lag = completedDual with { LocalCache = Offer().ToArray() };
Check(TradeOfferRules.Check(lag, partialPlan, 1).Code == OfferCheckCode.WaitingCache,
    "complete working262 plus empty cache cannot propose");
lag = completedDual with { LocalCache = Offer(new BagSlot(0, 0, other, 999)).ToArray() };
Check(TradeOfferRules.Check(lag, partialPlan, 1).Code == OfferCheckCode.WaitingCache,
    "old cache item and quantity wait rather than claim raw tampering");
Check(TradeOfferRules.Check(dual with { LocalCache = lag.LocalCache }, partialPlan, 0, true).Code == OfferCheckCode.QuantityReady,
    "only raw working evidence authorizes numeric while resolved cache lags");
Check(TradeOfferRules.Check(completedDual with { SyncPending = true }, partialPlan, 1).Code == OfferCheckCode.WaitingSync,
    "matching views with pending network synchronization cannot propose or confirm");
Check(TradeOfferRules.Check(completedDual, partialPlan, 1).Code == OfferCheckCode.Ready &&
      TradeOfferRules.Check(lag, partialPlan, 1).Code == OfferCheckCode.WaitingCache,
    "previously ready view never authorizes action after cache regression");
var mutatedView = View(twoPlan, 99, 1);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { Quantity = 98 };
Check(TradeOfferRules.Check(mutatedView with { SyncPending = true }, twoPlan, 1, true).Code == OfferCheckCode.WorkingQuantity,
    "completed raw prefix mutation remains terminal during network delay");
mutatedView = View(partialPlan, 1);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { Symbolic = false };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 0, true).Code == OfferCheckCode.WorkingNotSymbolic,
    "resolved cache accidentally passed as raw working cannot authorize submission");
mutatedView = View(partialPlan, 1);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { LinkedContainer = 2 };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 0, true) == new OfferCheck(OfferCheckCode.SourceContainer, 0),
    "wrong raw source container reports exact code and physical slot");
mutatedView = View(partialPlan, 1);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { LinkedSlot = 20 };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 0, true).Code == OfferCheckCode.SourceSlot,
    "wrong raw source slot is distinct from quantity mismatch");
mutatedView = View(partialPlan, 1);
mutatedView.Sources[0] = mutatedView.Sources[0] with { Quantity = 998 };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 0, true).Code == OfferCheckCode.SourceQuantity,
    "mutatedView original source quantity cannot authorize numeric");
mutatedView = View(partialPlan, 1);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { Item = hq };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 0, true).Code == OfferCheckCode.WorkingItem,
    "raw source quality mismatch has its own failure");
mutatedView = View(partialPlan, 262);
mutatedView.RawWorking[0] = mutatedView.RawWorking[0] with { Quantity = 0 };
Check(TradeOfferRules.Check(mutatedView, partialPlan, 1).Code == OfferCheckCode.WorkingQuantity,
    "completed prefix becoming zero is never ignored as an empty cache slot");
mutatedView = View(partialPlan, 262);
mutatedView.RawWorking[1] = new(item, 0, true, 0, 8);
Check(TradeOfferRules.Check(mutatedView, partialPlan, 1).Code == OfferCheckCode.WaitingWorking,
    "zero-quantity raw ghost link blocks next steps until native clear");
mutatedView = View(partialPlan, 262);
mutatedView.LocalCache[4] = new(2, 20, other, 0);
mutatedView.RemoteCache[3] = new(2, 21, other, 0);
Check(TradeOfferRules.Check(mutatedView, partialPlan, 1).Code == OfferCheckCode.Ready,
    "stale IDs in zero-quantity resolved cache are semantically empty");
Check(TradeRules.ValidOffer([new(0, 0, item, 262), new(1, 5, other, 0)], item, 262, 0) &&
      TradeRules.EmptyRemote([new(1, 7, other, 0)], 0), "generic offer validators use quantity-zero empty semantics");
Check(!TradeRules.EmptyRemote([new(0, 0, default, -1)], 0) &&
      !TradeRules.ValidOffer([new(0, 0, item, -1)], item, -1, 0), "negative quantity never counts as empty");
Check(TradeOfferRules.Check(completedDual with { LocalGil = 1 }, partialPlan, 1).Code == OfferCheckCode.LocalGil &&
      TradeOfferRules.Check(completedDual with { RemoteGil = 1 }, partialPlan, 1).Code == OfferCheckCode.RemoteOffer,
    "cache lag exception never relaxes either gil safety gate");
var diagnostic = System.Text.Json.JsonSerializer.Serialize(new { Plan = partialPlan, Observation = completedDual });
Check(diagnostic.Contains("RawWorking") && diagnostic.Contains("LocalCache") && diagnostic.Contains("Sources") &&
      !diagnostic.Contains("Entity") && !diagnostic.Contains("Pointer") && !diagnostic.Contains("Name"),
    "offer diagnostic exposes both views and source evidence without identity or pointer fields");
// v0.1.3: the user's 999 limit applies to each physical trade slot, not their sum.
var mixedSources = new List<BagSlot> { new(0, 0, item, 518), new(0, 1, item, 999), new(0, 2, item, 999) };
var wholeTrade = TradeRules.Plan(mixedSources, item, 2516, 999, 999);
Check(wholeTrade.Select(x => x.Quantity).SequenceEqual(new[] { 999, 999, 518 }), "2516 is sent as999+999+518 in one transaction");
Check(wholeTrade.Select(x => x.Source.Slot).SequenceEqual(new[] { 1, 2, 0 }), "larger stacks first preserve exact original bag source mapping");
var tieSources = new List<BagSlot> { new(1, 5, item, 999), new(0, 2, item, 999), new(0, 1, item, 999) };
Check(TradeRules.Plan(tieSources, item, 2997).Select(x => (x.Source.Container, x.Source.Slot))
    .SequenceEqual(new[] { (0, 1), (0, 2), (1, 5) }), "equal stack sizes use stable bag and slot order independent of input order");
var sixSources = Enumerable.Range(0, 6).Select(i => new BagSlot(0, i, item, 999)).ToList();
var firstFive = TradeRules.Plan(sixSources, item, 5994);
Check(firstFive.Count == 5 && firstFive.Sum(x => x.Quantity) == 4995, "five full999 slots share one4995 transaction");
var afterFive = sixSources.Select(s => s with { Quantity = s.Slot < 5 ? 0 : s.Quantity }).ToList();
Check(TradeRules.Plan(afterFive, item, 999).Single().Source.Slot == 5, "sixth source remains for a subsequent transaction");
var threeAt99 = TradeRules.Plan(mixedSources, item, 2516, 99, 999);
Check(threeAt99.Count == 3 && threeAt99.Sum(x => x.Quantity) == 297 && threeAt99.All(x => x.Quantity == 99),
    "cap99 allows three independent99 slots in one transaction");
var threeAt1 = TradeRules.Plan(mixedSources, item, 2516, 1, 999);
Check(threeAt1.Count == 3 && threeAt1.Sum(x => x.Quantity) == 3 && threeAt1.All(x => x.Quantity == 1),
    "cap1 permits one unit per distinct source slot");
Check(TradeRules.Plan([new(0, 0, item, 999)], item, 495, 99, 999).Single().Quantity == 99,
    "one source cannot be reused to fill five trade slots with99 each");
Check(TradeRules.Plan([new(0, 0, item, 999), new(0, 0, item, 999)], item, 1998).Count == 1,
    "duplicate input references to one physical source are deduplicated");
Check(TradeRules.Plan(mixedSources, item, 1200).Select(x => x.Quantity).SequenceEqual(new[] { 999, 201 }),
    "goal remainder controls the final slot independently of slot cap");
Check(TradeRules.Plan(mixedSources.Append(new BagSlot(2, 10, hq, 999)).ToArray(), item, 2516).All(x => x.Source.Item == item),
    "descending source order never crosses selected quality");
Check(TradeRules.Plan([new(2, 10, hq, 999), new(0, 0, item, 999)], hq, 500).Single().Quantity == 500,
    "HQ partial source stays independent of NQ inventory");
var numericEachSlot = wholeTrade.Select(x => NumericPopupRules.Check(popup with
{
    Amount = x.Quantity, SourceQuantity = x.Source.Quantity, SlotQuantityCap = 999, ItemStackLimit = 999
})).ToArray();
Check(numericEachSlot.SequenceEqual(new[] { NumericCheck.Ready, NumericCheck.Ready, NumericCheck.Ready }),
    "second and third numeric submissions retain full per-slot allowance after999 or1998 already filled");
Check(NumericPopupRules.Check(popup with { Amount = 100, SlotQuantityCap = 99 }) == NumericCheck.InvalidRange,
    "each later slot still rejects amount above its own configured cap");
var threeSlots = View(wholeTrade, 999, 999, 1);
Check(TradeOfferRules.Check(threeSlots, wholeTrade, 2, true).Code == OfferCheckCode.QuantityReady,
    "third sorted source retains raw link authority and temporary1 before its own numeric submission");
threeSlots = View(wholeTrade, 999, 999, 518);
Check(TradeOfferRules.Check(threeSlots, wholeTrade, 2, true, true).Code == OfferCheckCode.Ready &&
      TradeOfferRules.Check(threeSlots, wholeTrade, 3).Code == OfferCheckCode.Ready,
    "all three sorted slots reconcile through unchanged dual-view rules");
var largerBefore = new Dictionary<ItemKey, long> { [item] = 2516, [hq] = 25 };
var largerPartial = new Dictionary<ItemKey, long> { [item] = 1517, [hq] = 25 };
var largerAfter = new Dictionary<ItemKey, long> { [item] = 0, [hq] = 25 };
Check(TradeRules.ClassifySettlement(largerBefore, largerPartial, item, 2516) == InventoryChange.PartialExpectedDecrease &&
      TradeRules.ClassifySettlement(largerBefore, largerAfter, item, 2516) == InventoryChange.ExpectedDecrease,
    "multi-slot2516 settlement accepts partial per-slot updates then exact total delta");
Check(CompletionGate.Evaluate(true, TradeRules.ClassifySettlement(largerBefore, largerAfter, item, 2516), false) == CompletionResult.Waiting &&
      CompletionGate.Evaluate(true, TradeRules.ClassifySettlement(largerBefore, largerAfter, item, 2516), true) == CompletionResult.Complete,
    "larger transaction still needs all three independent completion signals");
Check(TradeRules.MigrateSlotQuantityCap(2, 999, 99) == 99, "legacy user cap migrates to per-slot setting");
Check(TradeRules.MigrateSlotQuantityCap(2, 999, 1) == 1, "legacy minimum cap is preserved");
Check(TradeRules.MigrateSlotQuantityCap(3, 500, 99) == 500, "migrated config never lets stale legacy field overwrite new setting");
Check(TradeRules.MigrateSlotQuantityCap(1, 999, 0) == 1 && TradeRules.MigrateSlotQuantityCap(2, 999, 1500) == 999,
    "legacy invalid caps normalize to supported range");
var crossBagPlan = TradeRules.Plan([new(2, 9, item, 518), new(1, 4, item, 999), new(0, 20, item, 999)], item, 2516);
Check(crossBagPlan.Select(x => (x.Source.Container, x.Source.Slot, x.Quantity))
    .SequenceEqual(new[] { (0, 20, 999), (1, 4, 999), (2, 9, 518) }),
    "sorted trade positions retain cross-bag source coordinates rather than natural inventory indices");
var crossBagView = View(crossBagPlan, 999, 999, 1);
Check(TradeOfferRules.Check(crossBagView, crossBagPlan, 2, true).Code == OfferCheckCode.QuantityReady,
    "third trade position correctly binds original bag2 slot9 after sorted fills");
crossBagView.RawWorking[2] = crossBagView.RawWorking[2] with { LinkedContainer = 0, LinkedSlot = 2 };
Check(TradeOfferRules.Check(crossBagView, crossBagPlan, 2, true).Code == OfferCheckCode.SourceContainer,
    "using target trade index as a source bag slot still fails source authority check");
// Final confirmation may be completed or dismissed outside this plugin; observation never sends a replacement click.
Check(TradeRules.CanReconcileFinal(true, true, true, 5, 5, 4995, 4995), "verified five-slot final phase permits read-only observation");
Check(!TradeRules.CanReconcileFinal(false, true, true, 5, 5, 4995, 4995), "user stop or another phase cannot restart reconciliation");
Check(!TradeRules.CanReconcileFinal(true, false, true, 5, 5, 4995, 4995), "unverified final offer cannot enter observation as an authorized batch");
Check(!TradeRules.CanReconcileFinal(true, true, false, 5, 5, 4995, 4995), "unknown trade lifecycle cannot be reconciled");
Check(!TradeRules.CanReconcileFinal(true, true, true, 4, 5, 3996, 4995), "partial filling cannot qualify as a completed offer");
Check(!TradeRules.CanReconcileFinal(true, true, true, 5, 5, 4994, 4995), "filled quantity must equal exact planned quantity");
Check(!TradeRules.CanReconcileFinal(true, true, true, 0, 0, 0, 0), "empty offer cannot arm final observation");
var reconciliationClock = new TradeClock();
reconciliationClock.BeginBatch(0, 90000);
reconciliationClock.Schedule(TradeActionKind.FinalConfirm, 0, 30000);
reconciliationClock.BeginBatch(500, 5000); // Production BeginReadOnlySettlement replaces the pending action budget.
Check(!reconciliationClock.TryDispatch(TradeActionKind.FinalConfirm, 30000, 1) && reconciliationClock.PendingAction == null,
    "popup closure cancels pending final click even after its former delay expires");
Check(!reconciliationClock.TimedOut(5499) && reconciliationClock.TimedOut(5500), "read-only reconciliation is bounded to five seconds");
Check(CompletionGate.Evaluate(false, InventoryChange.Unchanged, true) == CompletionResult.Waiting,
    "closed confirmation and trade without receipt or inventory never counts as completed");
Check(CompletionGate.Evaluate(true, InventoryChange.PartialExpectedDecrease, true) == CompletionResult.Waiting,
    "external confirmation with only partial inventory decrease still waits");
Check(CompletionGate.Evaluate(false, InventoryChange.ExpectedDecrease, true) == CompletionResult.Waiting,
    "external inventory change plus close still requires the send receipt");
Check(CompletionGate.Evaluate(true, InventoryChange.ExpectedDecrease, true) == CompletionResult.Complete,
    "all three proofs permit completion without requiring this plugin to own the final click");
Check(CompletionGate.Evaluate(true, InventoryChange.Unexpected, true) == CompletionResult.Invalid,
    "external final path never accepts unrelated inventory change");
const string finalText = "以该条件进行交易吗？";
Check(FinalPromptRules.Check(finalText, true, finalText) == FinalPromptCheck.Ready,
    "verified native prompt authorizes text gate independently of rendered node decoration");
Check(FinalPromptRules.Check("  " + finalText + "\r\n", true, finalText) == FinalPromptCheck.Ready,
    "native string prompt normalizes surrounding whitespace only");
Check(FinalPromptRules.Check("其他确认", true, finalText) == FinalPromptCheck.TextMismatch,
    "unknown native prompt remains blocked even if a rendered node would match");
Check(FinalPromptRules.Check(finalText, false, finalText) == FinalPromptCheck.UnsupportedType,
    "nonstring native value cannot be treated as a matching prompt");
Check(FinalPromptRules.Check(null, false, finalText) == FinalPromptCheck.ValueUnavailable,
    "missing native values remain a distinct initialization state");
Check(FinalPromptRules.Check("", true, "") == FinalPromptCheck.ExpectedTextMissing,
    "missing sheet text cannot authorize empty native prompt");
// Groups describe an item's real stack size; slot caps and saved individual counts do not redefine a group.
Check(TradeQuantityRules.TryParseCount("10", out var parsedCount) && parsedCount == 10,
    "quantity text parses the complete positive integer");
Check(TradeQuantityRules.TryParseCount("00010", out var leadingZeroCount) && leadingZeroCount == 10,
    "leading zeroes preserve the exact numeric quantity");
foreach (var invalidText in new string?[] { null, "", "0", "-1", "1.5", "4294967297", new('9', 128), "1e3", " 10", "+10", "１０", "10\0" })
    Check(!TradeQuantityRules.TryParseCount(invalidText, out var rejectedCount) && rejectedCount == 0,
        $"invalid numeric text cannot become a different positive quantity: {invalidText ?? "<null>"}");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 2516, 10, 999, out var groupTotal) && groupTotal == 9990,
    "ten groups of a 999-stack item resolve to 9990 items");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 2516, 10, 99, out var groups99) && groups99 == 990,
    "changing to a 99-stack item keeps ten groups and recalculates 990 items");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 2516, 10, 1, out var groups1) && groups1 == 10,
    "nonstackable items count as one item per group");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Items, 2516, 10, 999, out var savedItems) && savedItems == 2516,
    "switching back to individual mode retains the independent saved count");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Items, 2516, -1, 0, out var itemsOnly) && itemsOnly == 2516,
    "invalid inactive group input does not replace an individual quantity");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, -1, 2, 999, out var groupsOnly) && groupsOnly == 1998,
    "group mode ignores inactive individual quantity");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 0, 999, out _), "zero groups are rejected");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, -3, 999, out _), "negative groups are rejected");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 10, 0, out _), "unknown stack size cannot authorize group trading");
Check(!TradeQuantityRules.TryResolve((QuantityInputMode)99, 1, 1, 999, out _), "unknown quantity mode is rejected at execution");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Items, 0, 10, 999, out _), "zero individual amount remains invalid");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 10_000_000, 1, out var exactLimit) && exactLimit == 10_000_000,
    "group conversion can reach the exact total safety limit");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 10_000_001, 1, out var rejectedTotal) && rejectedTotal == 0,
    "over-limit groups are refused rather than clamped into an authorized total");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 10010, 999, out var belowLimit) && belowLimit == 9_999_990,
    "largest whole 999-stack group count below the limit is accepted");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 10011, 999, out _), "next 999-stack group exceeds total limit");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, int.MaxValue, uint.MaxValue, out _),
    "wide group multiplication cannot wrap into a valid small total");
Check(TradeQuantityRules.TryResolve(QuantityInputMode.Groups, 1, 2, 9999, out var largeStack) && largeStack == 19998,
    "group size is not silently limited to the per-trade-slot maximum");
Check(!TradeQuantityRules.TryResolve(QuantityInputMode.Items, 10_000_001, 1, 999, out _), "execution also rejects oversized individual totals");
Check(TradeQuantityRules.MigrateMode(3, QuantityInputMode.Groups) == QuantityInputMode.Items,
    "old configurations retain individual-count semantics");
Check(TradeQuantityRules.MigrateMode(4, QuantityInputMode.Groups) == QuantityInputMode.Groups,
    "new configurations preserve group mode");
Check(TradeQuantityRules.MigrateMode(4, (QuantityInputMode)42) == QuantityInputMode.Items,
    "invalid saved mode loads as the legacy individual mode");
var groupedKey = new ItemKey(42, false);
var groupBags = Enumerable.Range(0, 10).Select(i => new BagSlot(0, i, groupedKey, 999)).ToArray();
var groupFirst = TradeRules.Plan(groupBags, groupedKey, groupTotal, 999, 999);
var groupSecond = TradeRules.Plan(groupBags.Skip(5).ToArray(), groupedKey, groupTotal - groupFirst.Sum(x => x.Quantity), 999, 999);
Check(groupFirst.Count == 5 && groupSecond.Count == 5 && groupFirst.Sum(x => x.Quantity) + groupSecond.Sum(x => x.Quantity) == groupTotal,
    "ten full groups retain five slots per transaction across two planned transactions");
var restrictedGroup = TradeRules.Plan(groupBags, groupedKey, groupTotal, 99, 999);
Check(restrictedGroup.Sum(x => x.Quantity) == 495 && groupTotal == 9990,
    "a smaller slot cap affects batch distribution without shrinking group quantity");
var partialGroup = TradeRules.Plan([new(0, 0, groupedKey, 600), new(0, 1, groupedKey, 399)], groupedKey, 999, 999, 999);
Check(partialGroup.Sum(x => x.Quantity) == 999 && partialGroup.Count == 2,
    "one group's total may come from multiple partially filled source stacks");
Console.WriteLine($"PASS {checks} checks");
