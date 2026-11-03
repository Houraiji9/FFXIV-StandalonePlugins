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
// User report: 3,259 units must be separated by total-per-trade cap, not per-slot cap.
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
Check(SimulateBatches(999).SequenceEqual(new[] { 999, 999, 999, 262 }), "3259 splits into 999 999 999 262");
var capped99 = SimulateBatches(99);
Check(capped99.Count == 33 && capped99.Take(32).All(x => x == 99) && capped99[^1] == 91, "cap99 exact remainder");
var capped1 = SimulateBatches(1);
Check(capped1.Count == 3259 && capped1.All(x => x == 1), "cap1 never batches multiple units");
plan = TradeRules.Plan(slots, item, 1000, 150, 99);
Check(plan.Count == 2 && plan.Sum(x => x.Quantity) == 150 && plan[1].Quantity == 51, "cap is total across all physical slots");
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
Check(NumericPopupRules.Check(popup with { Amount = 99, RemainingBatchCap = 99 }) == NumericCheck.Ready, "numeric slider can exceed chosen cap while submitted amount cannot");
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
Check(NumericPopupRules.Check(popup with { Amount = 99, SourceQuantity = 99, ItemStackLimit = 99, Maximum = 999, RemainingBatchCap = 99 }) == NumericCheck.Ready,
    "popup upper range may exceed source but the planned submission never does");
Check(GameCompatibility.IsSupported("2026.10.06.0000.0000\r\n", 0x3730), "version file trailing newline normalized before exact check");
Console.WriteLine($"PASS {checks} checks");
