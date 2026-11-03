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
Console.WriteLine($"PASS {checks} checks");
