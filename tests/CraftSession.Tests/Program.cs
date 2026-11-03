using RetainerCraftBridge;

var count = 0;
void Check(bool value, string name) { count++; if(!value) throw new Exception(name); }
void Reject(Action action, string name)
{
    var rejected = false;
    try { action(); } catch(InvalidOperationException) { rejected = true; }
    Check(rejected, name);
}
var ingredients = new CraftIngredient[] { new(100, 3), new(200, 2), new(2, 4) };
var stock = new Dictionary<uint, CraftStock> { [100] = new(2, 10), [200] = new(20, 0), [2] = new(100, 0) };
var allocation = CraftRules.Assign(ingredients, stock);
Check(allocation[0] == new CraftAssignment(100, 2, 1), "NQ first and exact HQ remainder");
Check(allocation[1] == new CraftAssignment(200, 2, 0), "normal-only ingredient");
Check(allocation[2] == new CraftAssignment(2, 4, 0), "crystals included");
Check(stock[100] == new CraftStock(2, 10), "assignment never mutates input stock");
var repeated = CraftRules.Assign([new(100, 3), new(100, 4)], stock);
Check(repeated[1] == new CraftAssignment(100, 0, 4), "duplicate rows reserve prior consumption");
Reject(() => CraftRules.Assign([new(200, 21)], stock), "insufficient stock cannot underfill");
Reject(() => CraftRules.Assign([new(100, 256)], stock), "native byte range enforced");
Reject(() => CraftRules.Assign([new(0, 1)], stock), "invalid nonempty ingredient");
Reject(() => CraftRules.Assign([new(100, 1)], new Dictionary<uint, CraftStock> { [100] = new(-1, 5) }), "negative stock rejected");
Check(CraftRules.Assign([new(0, 0)], stock)[0] == default, "empty ingredient stays empty");
var target = new CraftAssignment[] { new(100, 2, 3), new(200, 2, 0) };
CraftMaterialSelection[] Selection(int hq) => [new(100, 5, 2, hq), new(200, 2, 2, 0)];
Check(CraftRules.NextMaterialStep(Selection(0), target, -1, 0) == new CraftMaterialStep(CraftMaterialAction.AddHighQuality, 0, 1), "HQ increments exactly once");
Check(CraftRules.NextMaterialStep(Selection(0), target, 0, 1).Action == CraftMaterialAction.Waiting, "pending HQ not repeated before response");
Check(CraftRules.NextMaterialStep(Selection(1), target, 0, 1) == new CraftMaterialStep(CraftMaterialAction.AddHighQuality, 0, 2), "next HQ only after previous observed");
Check(CraftRules.NextMaterialStep(Selection(3), target, 0, 3).Action == CraftMaterialAction.Complete, "fully assigned after final response");
Check(CraftRules.NextMaterialStep(Selection(4), target, -1, 0).Action == CraftMaterialAction.Invalid, "overfilled HQ never authorized");
Check(CraftRules.NextMaterialStep(Selection(2), target, 0, 1).Action == CraftMaterialAction.Invalid, "unexpected multiple HQ increments stop");
var changed = Selection(0); changed[1] = new(201, 2, 2, 0);
Check(CraftRules.NextMaterialStep(changed, target, 0, 1).Action == CraftMaterialAction.Invalid, "other slot identity checked even while waiting");
changed = Selection(0); changed[1] = new(200, 2, 1, 1);
Check(CraftRules.NextMaterialStep(changed, target, 0, 1).Action == CraftMaterialAction.Invalid, "other slot quality checked while waiting");
Check(CraftRules.NextMaterialStep(Selection(0), target, 9, 1).Action == CraftMaterialAction.Invalid, "pending material slot bounded");
Check(CraftRules.NextMaterialStep([], target, -1, 0).Action == CraftMaterialAction.Invalid, "missing material slots rejected");
Check(CraftRules.ActiveRecipeMatches(CraftMode.Standard, 100, 100, 200), "standard uses actual crafting id");
Check(!CraftRules.ActiveRecipeMatches(CraftMode.Standard, 100, 200, 100), "standard ignores stale active selection");
Check(CraftRules.ActiveRecipeMatches(CraftMode.Quick, 100, 200, 100), "quick uses active recipe id");
Check(!CraftRules.ActiveRecipeMatches(CraftMode.Quick, 100, 100, 200), "quick ignores stale standard id");
Check(!CraftRules.ActiveRecipeMatches(CraftMode.Inherit, 100, 100, 100), "inherit cannot authorize native start");
Check(!CraftRules.ActiveRecipeMatches(CraftMode.Standard, 0, 0, 0), "zero recipe cannot authorize native");
Check(CraftRules.QuickBatch(99, ingredients, stock) == 4, "quick uses limiting material incl HQ");
Check(CraftRules.QuickBatch(2, ingredients, stock) == 2, "remaining attempt limit");
Check(CraftRules.QuickBatch(99, [new(100, 3), new(100, 4)], stock) == 1, "duplicate quick material combined");
Check(CraftRules.QuickBatch(1000, [new(200, 1)], new Dictionary<uint, CraftStock> { [200] = new(5000, 0) }) == 99, "quick native 99 maximum");
Check(CraftRules.QuickBatch(5, ingredients, new Dictionary<uint, CraftStock>()) == 0, "missing stock never starts");
Check(CraftRules.QuickBatch(0, ingredients, stock) == 0, "completed count never starts another");
Check(CraftRules.QuickBatch(10, ingredients, withStock(2, new(3, 0))) == 0, "crystal deficit blocks");

Dictionary<uint, CraftStock> withStock(uint id, CraftStock value)
{
    var copy = stock.ToDictionary(x => x.Key, x => x.Value); copy[id] = value; return copy;
}

// Standard requires start, end, exact product delta, and completed progress. Window disappearance alone is insufficient.
Check(CraftRules.Reconcile(false, true, 1, 1, 3, 10, 13, true, true, false) == CraftReconciliation.Waiting, "no actual start no completion");
Check(CraftRules.Reconcile(true, false, 1, 1, 3, 10, 13, true, true, false) == CraftReconciliation.Waiting, "inventory before end waits");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 10, true, true, false) == CraftReconciliation.Waiting, "end before inventory waits");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 13, true, false, false) == CraftReconciliation.Waiting, "inventory alone insufficient");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 13, true, true, false) == CraftReconciliation.Complete, "standard three signals");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 10, true, false, true) == CraftReconciliation.Failed, "failed real attempt not retried as success");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 11, true, true, false) == CraftReconciliation.Invalid, "partial product amount rejected");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 16, true, true, false) == CraftReconciliation.Invalid, "excess product rejected");
Check(CraftRules.Reconcile(true, true, 1, 1, 3, 10, 9, true, true, false) == CraftReconciliation.Invalid, "product decrease rejected");
Check(CraftRules.Reconcile(true, true, 2, 1, 3, 10, 16, true, true, false) == CraftReconciliation.Invalid, "attempt count cannot exceed request");
Check(CraftRules.Reconcile(true, true, 0, 99, 2, 10, 10, false, false, false) == CraftReconciliation.Complete, "cancel quick before first result can settle zero");
Check(CraftRules.Reconcile(true, true, 3, 99, 2, 10, 16, false, false, false) == CraftReconciliation.Complete, "cancelled partial quick exact progress");
Check(CraftRules.Reconcile(true, true, 3, 99, 2, 10, 14, false, false, false) == CraftReconciliation.Waiting, "quick failed/late output not replenished");
Check(CraftRules.Reconcile(true, true, 100, 99, 2, 10, 210, false, false, false) == CraftReconciliation.Invalid, "quick cannot overrun requested batch");
foreach(var order in new[] { new[] {0,1,2}, new[] {0,2,1}, new[] {1,0,2}, new[] {1,2,0}, new[] {2,0,1}, new[] {2,1,0} })
{
    var signals = new bool[3];
    for(var i = 0; i < 3; i++)
    {
        signals[order[i]] = true;
        var result = CraftRules.Reconcile(true, signals[0], 1, 1, 1, 100, signals[1] ? 101 : 100, true, signals[2], false);
        Check(result == (i == 2 ? CraftReconciliation.Complete : CraftReconciliation.Waiting), "standard signal permutations");
    }
}

var delays = new CraftDelays { OpenRecipeMs = 111, FillMaterialsMs = 222, StartCraftMs = 333, SubmitQuickMs = 444,
    CancelQuickMs = 555, CloseRecipeMs = 666, BetweenCraftsMs = 777, TimeoutSeconds = 88 };
var frozen = delays.Freeze(); delays.OpenRecipeMs = 9999;
Check(frozen.OpenRecipeMs == 111 && frozen.FillMaterialsMs == 222 && frozen.StartCraftMs == 333 &&
    frozen.SubmitQuickMs == 444 && frozen.CancelQuickMs == 555 && frozen.CloseRecipeMs == 666 &&
    frozen.BetweenCraftsMs == 777 && frozen.TimeoutSeconds == 88, "all delays frozen independently");
var clamped = new CraftDelays { OpenRecipeMs = 0, CancelQuickMs = 999999, TimeoutSeconds = 1 }.Freeze();
Check(clamped.OpenRecipeMs == 100 && clamped.CancelQuickMs == 30000 && clamped.TimeoutSeconds == 15, "safe delay and timeout clamps");
var clock = new CraftClock(); clock.Begin(0, 15);
Check(!clock.Dispatch("open", 1000, 0, 1), "schedule never dispatches immediately");
Check(!clock.Dispatch("open", 1000, 999, 2) && clock.Remaining(999) == 1, "delay does not slide on polling");
Check(clock.Dispatch("open", 1000, 1000, 3), "exact due dispatch");
Check(!clock.Dispatch("fill", 100, 1000, 3), "next native step not same tick");
Check(!clock.Dispatch("fill", 100, 1100, 3), "same frame prohibited even if time advances");
Check(clock.Dispatch("fill", 100, 1100, 4), "next frame at safe minimum");
Check(!clock.Dispatch("start", 30000, 1100, 4), "long intentional delay scheduled");
Check(!clock.Expired(30000), "intentional delay excluded from timeout");
clock.Cancel();
Check(clock.Pending == null && clock.Remaining(40000) == 0, "stop removes unsent action");
Check(!clock.Dispatch("close", 100, 1100, 4), "stop cleanup still delays");
Check(!clock.Dispatch("close", 100, 1199, 5), "cleanup cannot bypass minimum spacing");
Check(clock.Dispatch("close", 100, 1200, 5), "cleanup is one rate-limited action");
clock.Begin(2000, 15);
Check(!clock.Dispatch("quick", 10000, 2000, 6), "first custom delay");
Check(clock.Dispatch("quick", 10000, 12000, 7), "first delayed action");
Check(!clock.Dispatch("quantity", 10000, 12000, 7), "second independent delay");
Check(!clock.Expired(21000), "multiple artificial delays extend deadline once each");
Check(clock.Dispatch("quantity", 10000, 22000, 8), "second delayed action");
Check(!clock.Expired(36999) && clock.Expired(37001), "response timeout remains bounded after delays");
clock.Begin(40000, 15); Check(!clock.Dispatch("start", 500, 40000, 9), "pending start created");
clock.Cancel(); clock.Begin(40100, 15);
Check(!clock.Dispatch("close", 500, 40100, 10), "cancel replaces pending start with cleanup");
Check(clock.Pending == "close", "old start never resurrected");
Check(!clock.Dispatch("close", 500, 40599, 11), "new close uses own delay");
Check(clock.Dispatch("close", 500, 40600, 12), "new close dispatched once");
Console.WriteLine($"CraftSession: {count} checks passed.");
