using RetainerVendorSweep;

var checks = 0;
void Check(bool value, string description) { checks++; if(!value) throw new Exception(description); }
void Reject(Action action, string description)
{
    var rejected = false;
    try { action(); } catch(InvalidOperationException) { rejected = true; }
    Check(rejected, description);
}
SaleRules Rules() => new() { ItemIds = [100], CategoryIds = [10] };
VendorDelays Fast() => new()
{
    StartMs = 100, RelogMs = 100, LoginStableMs = 100, NextCharacterMs = 100,
    TimeoutSeconds = 15, RelogTimeoutSeconds = 15
};
VendorCharacter Character(ulong cid) => new(cid, $"Character{cid}", "World1");
var item = new VendorItem(0, 5, 100, false, 99, 10, 7, true);
var rules = Rules();
Check(rules.Validate() == null && rules.Matches(item), "explicit NQ rule is valid");
Check(!rules.Matches(item with { Hq = true }), "HQ is excluded by default");
rules.IncludeHq = true;
Check(rules.Matches(item with { Hq = true }), "HQ can be explicitly enabled");
rules.IncludeNq = false;
Check(!rules.Matches(item), "quality filter always applies");
rules.IncludeNq = true;
foreach(var bad in new[] { item with { Container = -1 }, item with { Container = 4 }, item with { Container = 10000 },
    item with { Container = 2001 }, item with { Container = 2005 }, item with { Slot = -1 }, item with { ItemId = 0 },
    item with { Quantity = 0 }, item with { Quantity = -1 }, item with { UnitPrice = 0 }, item with { Sellable = false } })
    Check(!rules.Matches(bad), "nonordinary/empty/unsellable source refused");
for(var bag = 0; bag < 4; bag++) Check(rules.Matches(item with { Container = bag }), "all four ordinary bags supported");
Check(rules.Matches(item with { ItemId = 101 }), "enabled category is a sale rule");
Check(!rules.Matches(item with { ItemId = 101, CategoryId = 11 }), "unselected item/category is excluded");
rules.ProtectedItemIds.Add(100);
Check(!rules.Matches(item), "global protection defeats explicit and category matches");
rules.ProtectedItemIds.Clear(); rules.CategoryProtectedItemIds[999] = [100];
Check(!rules.Matches(item), "protection survives disabled category and explicit item match");
Check(!rules.Matches(item with { CategoryId = 500, Hq = true }), "category protection is item-wide across quality/category");
rules.CategoryProtectedItemIds.Clear();
rules.CategoryProtectedItemIds[10] = [101];
var frozen = rules.Freeze();
rules.ItemIds.Clear(); rules.CategoryIds.Clear(); rules.CategoryProtectedItemIds[10].Clear(); rules.IncludeNq = false;
Check(frozen.Matches(item), "frozen source and category sets unaffected by original changes");
Check(!frozen.Matches(item with { ItemId = 101 }), "nested category protection deep copied");
Check(new SaleRules().Validate() != null, "empty sale selection rejected");
Check(new SaleRules { ItemIds = [100], IncludeHq = false, IncludeNq = false }.Validate() != null, "both qualities disabled rejected");
Check(new SaleRules { ItemIds = [0] }.Validate() != null, "zero item id rejected");
Check(new SaleRules { ItemIds = [100], CategoryProtectedItemIds = new() { [10] = null! } }.Validate() != null,
    "malformed protection cannot silently disappear");
Check(!new SaleRules { ItemIds = [100], CategoryProtectedItemIds = new() { [10] = null! } }.Matches(item),
    "malformed protection fails closed even if Matches called directly");
var delayed = new VendorDelays { StartMs = 0, RelogMs = 999999, LoginStableMs = 1234, ConfirmMs = 5432,
    TimeoutSeconds = 1, RelogTimeoutSeconds = 1000 };
var savedDelay = delayed.Freeze(); delayed.LoginStableMs = 60000;
Check(savedDelay.StartMs == 100 && savedDelay.RelogMs == 60000 && savedDelay.LoginStableMs == 1234 && savedDelay.ConfirmMs == 5432,
    "delays freeze independently and enforce action limits");
Check(savedDelay.TimeoutSeconds == 15 && savedDelay.RelogTimeoutSeconds == 600, "timeouts clamp to supported bounds");

Check(SaleResultRules.TryExpectedGil(7, 99, 100, out var quoted) && quoted == 793, "native quote multiplied in wide arithmetic");
Check(!SaleResultRules.TryExpectedGil(uint.MaxValue, int.MaxValue, long.MaxValue, out _), "gil addition overflow cannot authorize sale");
Check(!SaleResultRules.TryExpectedGil(0, 99, 100, out _) && !SaleResultRules.TryExpectedGil(7, 0, 100, out _), "zero quote/quantity rejected");
Check(SaleResultRules.ObserveSale(item, item, 100, 100, 99) == SaleCheck.Waiting, "no signals never completes");
Check(SaleResultRules.ObserveSale(item, null, 100, 100, 99) == SaleCheck.Waiting, "source disappears before gil waits");
Check(SaleResultRules.ObserveSale(item, item, 100, 793, 99) == SaleCheck.Waiting, "gil before inventory waits");
Check(SaleResultRules.ObserveSale(item, null, 100, 793, 99) == SaleCheck.Complete, "whole stack disappearance plus exact gil completes");
Check(SaleResultRules.ObserveSale(item, item with { Quantity = 0, ItemId = 0 }, 100, 793, 99) == SaleCheck.Complete,
    "cleared source slot can settle whole stack");
Check(SaleResultRules.ObserveSale(item, item with { Quantity = 49 }, 100, 450, 50) == SaleCheck.Complete, "partial sale exact quantity and gil");
Check(SaleResultRules.ObserveSale(item, item with { Quantity = 80 }, 100, 120, 50) == SaleCheck.Waiting, "bounded partial server signals wait");
Check(SaleResultRules.ObserveSale(item, null, 100, 450, 50) == SaleCheck.Invalid, "source vanished beyond partial planned quantity refused");
Check(SaleResultRules.ObserveSale(item, item with { Quantity = 99, ItemId = 101 }, 100, 100, 99) == SaleCheck.Invalid,
    "source replacement cannot settle");
Check(SaleResultRules.ObserveSale(item, item with { Slot = 6 }, 100, 100, 99) == SaleCheck.Invalid, "different slot is not sale receipt");
Check(SaleResultRules.ObserveSale(item, null, 100, 794, 99) == SaleCheck.Invalid, "excess gil cannot be counted as sale");
Check(SaleResultRules.ObserveSale(item, null, 100, 99, 99) == SaleCheck.Invalid, "gil decrease stops");
Check(SaleResultRules.ObserveSale(item, null, 100, 793, 100) == SaleCheck.Invalid, "cannot sell more than frozen source");
Check(SaleResultRules.ObserveSale(item with { Container = 2001 }, null, 100, 793, 99) == SaleCheck.Invalid,
    "receipt helper also restricts inventory source");
Check(SaleResultRules.ObserveSale(item with { UnitPrice = 10 }, null, 100, 1090, 99) == SaleCheck.Complete,
    "verified final native quote can replace sheet quote");

Check(VendorNativeRules.TryUnitPrice(1, true, 1, out var twiceRounded) && twiceRounded == 3,
    "HQ and materia ceilings are applied separately rather than combined");
Check(VendorNativeRules.TryUnitPrice(11, true, 1, out var rounded11) && rounded11 == 15,
    "each native multiplier preserves its independent upward rounding");
for(var m = 0; m <= 5; m++)
{
    Check(VendorNativeRules.TryUnitPrice(100, false, m, out var normalPrice) && normalPrice == 100 + 10 * m,
        "all zero-to-five materia counts use native NQ price");
    Check(VendorNativeRules.TryUnitPrice(100, true, m, out var highPrice) && highPrice == 110 + 11 * m,
        "all zero-to-five materia counts use native HQ price");
}
Check(!VendorNativeRules.TryUnitPrice(0, true, 5, out _), "zero base price never becomes sellable via rounding");
Check(!VendorNativeRules.TryUnitPrice(100, false, -1, out _) && !VendorNativeRules.TryUnitPrice(100, false, 6, out _),
    "materia count outside actual five slots rejected");
Check(VendorNativeRules.TryUnitPrice(int.MaxValue, false, 0, out var largestPrice) && largestPrice == int.MaxValue,
    "signed native quote boundary is representable");
Check(!VendorNativeRules.TryUnitPrice(int.MaxValue, true, 0, out _) && !VendorNativeRules.TryUnitPrice(uint.MaxValue, false, 0, out _),
    "quote overflow cannot wrap into a valid sale price");
Check(VendorNativeRules.CancelResponseComplete(true, true, false, false, 0, true, true), "all owned cancellation proofs permit cleanup");
Check(!VendorNativeRules.CancelResponseComplete(false, true, false, false, 0, true, true), "idle window close without own cancel is not cancellation proof");
Check(!VendorNativeRules.CancelResponseComplete(true, false, false, false, 0, true, true), "own callback without real finalize still pending");
Check(!VendorNativeRules.CancelResponseComplete(true, true, true, false, 0, true, true), "starting sell prevents cancellation completion");
Check(!VendorNativeRules.CancelResponseComplete(true, true, false, true, 0, true, true), "pending transaction prevents cancellation completion");
Check(!VendorNativeRules.CancelResponseComplete(true, true, false, false, 42, true, true), "live proxy window prevents cancellation completion");
Check(!VendorNativeRules.CancelResponseComplete(true, true, false, false, 0, false, true), "changed bags cannot be claimed cancelled");
Check(!VendorNativeRules.CancelResponseComplete(true, true, false, false, 0, true, false), "changed gil cannot be claimed cancelled");

var host = new FakeHost { CurrentCid = 1 };
var session = new FakeSession { AutoComplete = true, NextCount = 2, NextGil = 14 };
var machine = new SweepMachine(host, session);
long now = 0;
void Tick(SweepMachine current, int milliseconds = 100) { now += milliseconds; current.Tick(now); }
void Until(SweepMachine current, Func<bool> condition, int maximum = 1000)
{
    for(var i = 0; i < maximum && !condition(); i++) Tick(current);
    Check(condition(), "bounded state-machine progress");
}
machine.Tick(now);
Check(!machine.Running && !machine.Blocking && session.BeginCalls == 0 && host.Changes.Count == 0,
    "new machine never resumes or starts automatically");
Reject(() => machine.Start([], Rules(), Fast(), now), "empty known role list rejected");
Reject(() => machine.Start([Character(0)], Rules(), Fast(), now), "unknown CID rejected");
Reject(() => machine.Start([new(1, "", "World1")], Rules(), Fast(), now), "missing identity label rejected");
Reject(() => machine.Start([Character(1), new(1, "Other", "World2")], Rules(), Fast(), now), "duplicate CID rejected");
Reject(() => machine.Start([Character(1), new(2, "character1", "world1")], Rules(), Fast(), now), "duplicate name/world rejected ignoring case");
host.SafeForSwitch = false;
Reject(() => machine.Start([Character(1)], Rules(), Fast(), now), "unsafe first role cannot start");
host.SafeForSwitch = true; host.ConflictReason = "another controller";
Reject(() => machine.Start([Character(1)], Rules(), Fast(), now), "known conflict checked before any action");
host.ConflictReason = null;
var selected = Enumerable.Range(1, 30).Select(x => Character((ulong)x)).ToList();
var queueRules = Rules(); var queueDelays = Fast();
machine.Start(selected, queueRules, queueDelays, now);
selected[0] = Character(500); queueRules.ItemIds.Clear(); queueRules.CategoryIds.Clear(); queueDelays.StartMs = 60000;
for(var i = 0; i < 1000 && machine.Blocking; i++)
{
    if(host.Pending is { } arriving) host.Arrive(arriving.Cid);
    Tick(machine);
}
Check(machine.State == SweepState.Completed && machine.Results.Count == 30 && machine.Results.All(x => x.Status == VendorCharacterStatus.Completed),
    "thirty selected roles complete one finite round");
Check(host.Changes.Count == 29 && host.Changes.Select(x => x.Cid).Distinct().Count() == 29,
    "current first role skips relog and each remaining role switches once");
Check(session.Begun.SequenceEqual(Enumerable.Range(1, 30).Select(x => (ulong)x)), "queue order and frozen role identities preserved");
Check(host.PrepareCalls == 30, "each stable character receives exactly one handoff before native sale");
Check(session.CapturedRules.All(x => x.Matches(item)), "all sessions receive frozen rules even after UI config changed");
Check(machine.Results.Sum(x => x.Count) == 60 && machine.Results.Sum(x => x.Gil) == 420, "per-role verified counts retained without duplication");
Check(machine.Detail.Contains("选定") && machine.Detail.Contains("已知"), "completion does not claim all account characters");
var completedBegins = session.BeginCalls; for(var i = 0; i < 20; i++) Tick(machine);
Check(session.BeginCalls == completedBegins, "completed queue never starts another cycle");

host = new FakeHost { CurrentCid = 99 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now); machine.Stop(now);
for(var i = 0; i < 10; i++) Tick(machine);
Check(!machine.Blocking && host.Changes.Count == 0 && session.BeginCalls == 0, "stop during startup revokes all future requests");
Check(machine.Results.All(x => x.Status == VendorCharacterStatus.Stopped), "unstarted roles marked stopped not completed");

host = new FakeHost { CurrentCid = 99 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), new VendorDelays { StartMs = 1000, RelogMs = 2000, LoginStableMs = 3000 }, now);
machine.Tick(999); Check(host.Changes.Count == 0, "startup delay honored");
machine.Tick(1000); machine.Tick(1001); machine.Tick(3000); Check(host.Changes.Count == 0, "relog has its own delay");
machine.Tick(3001); machine.Tick(3001); Check(host.Changes.Count == 1, "repeat same-time Tick never repeats relog");
host.Arrive(2); machine.Tick(4000); Check(session.BeginCalls == 0, "wrong CID cannot begin sale");
host.Arrive(1); machine.Tick(5000); machine.Tick(7999); Check(session.BeginCalls == 0, "target login stability independently delayed");
machine.Tick(8000); machine.Tick(8000); Check(session.BeginCalls == 1, "begin happens once after stable target identity");

host = new FakeHost { CurrentCid = 99, MarkSwitchBusy = false }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => host.Changes.Count == 1);
machine.Stop(now); var stoppedAt = now;
machine.Stop(now + 10_000); machine.Tick(stoppedAt + 15_101);
Check(machine.State == SweepState.Failed && machine.Blocking, "stopping before busy acknowledgement stays blocked; repeated stop does not reset timeout");
Check(host.Changes.Count == 1 && session.BeginCalls == 0, "no relog or native session is retried while stopping");
host.Arrive(1); machine.Tick(stoppedAt + 16_000); machine.Tick(stoppedAt + 16_100);
Check(!machine.Blocking && machine.State == SweepState.Failed && session.BeginCalls == 0,
    "late target arrival cleans up timed-out stop without starting sale");

host = new FakeHost { CurrentCid = 1 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => session.BeginCalls == 1);
session.SoldItems = 3; session.Gil = 21; Tick(machine);
machine.Stop(now); machine.Stop(now + 500);
Check(session.StopCalls == 1 && machine.Blocking && !machine.Running, "stop revokes following actions immediately and requests own cleanup once");
session.Finish(VendorSessionState.Stopped); Tick(machine);
Check(!machine.Blocking && host.Changes.Count == 0 && session.BeginCalls == 1, "session cleanup does not start next role");
Check(machine.Results[0].Count == 3 && machine.Results[0].Gil == 21 && machine.Results[1].Count == 0, "confirmed partial totals survive stop");

host = new FakeHost { CurrentCid = 1 }; session = new() { AutoComplete = true, NextCount = 5, NextGil = 35 }; machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => machine.Results[0].Status == VendorCharacterStatus.Completed);
host.ConflictReason = "foreign task started"; Tick(machine);
Check(machine.State == SweepState.Failed && !machine.Blocking && host.Changes.Count == 0, "conflict between roles halts before next relog");
Check(machine.Results[0].Count == 5 && machine.Results[1].Count == 0, "failure between roles never copies previous role count");

host = new FakeHost { CurrentCid = 1 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now); Until(machine, () => session.BeginCalls == 1);
session.SoldItems = 2; session.Gil = 14; Tick(machine);
session.SoldItems = 1; Tick(machine);
Check(machine.State == SweepState.Failed && session.StopCalls == 1, "regressing receipts stop the round");
session.SoldItems = 2; session.Finish(VendorSessionState.Stopped); Tick(machine);
Check(machine.Results[0].Count == 2 && !machine.Blocking, "last verified count survives invalid regressing observation");

host = new FakeHost { CurrentCid = 1 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now); Until(machine, () => session.BeginCalls == 1);
session.SoldItems = 4; session.Gil = 28; session.Finish(VendorSessionState.Failed); Tick(machine);
Check(machine.State == SweepState.Failed && !machine.Blocking && session.StopCalls == 0,
    "released failed session is not resurrected by an unnecessary stop call");
Check(machine.Results[0].Count == 4 && session.BeginCalls == 1 && host.Changes.Count == 0,
    "terminal failure preserves confirmed amounts and prevents following role");

// Reproduce a reused native service exposing A's old terminal snapshot when B rejects before reset.
host = new FakeHost { CurrentCid = 1 }; session = new() { AutoComplete = true, NextCount = 5, NextGil = 35 }; machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2), Character(3)], Rules(), Fast(), now);
Until(machine, () => machine.Results[0].Status == VendorCharacterStatus.Completed);
session.RejectBeforeReset = true;
for(var i = 0; i < 50 && machine.Running; i++) { if(host.Pending is { } pending) host.Arrive(pending.Cid); Tick(machine); }
Check(machine.State == SweepState.Failed && !machine.Blocking, "known unqueued Begin rejection finishes without owning old terminal session");
Check(machine.Results[0].Count == 5 && machine.Results[0].Gil == 35 && machine.Results[1].Count == 0 && machine.Results[1].Gil == 0,
    "A's five items and thirty-five gil are never attributed to rejected B");
Check(machine.Results.Sum(x => x.Count) == 5 && machine.Results.Sum(x => x.Gil) == 35, "rejected Begin does not double aggregate receipts");
Check(machine.Results[1].Status == VendorCharacterStatus.Failed && machine.Results[1].Detail == "B preflight rejected" && machine.Detail == "B preflight rejected",
    "B preserves its actual rejection instead of A's completed detail");
Check(session.Begun.SequenceEqual(new ulong[] { 1, 2 }) && session.StopCalls == 0 && host.Changes.Count == 1 &&
    machine.Results[2].Status == VendorCharacterStatus.Stopped, "explicit rejection neither stops the old service nor starts C");
session.RejectBeforeReset = false; session.NextCount = session.NextGil = 0;
machine.Start([Character(2)], Rules(), Fast(), now); Until(machine, () => !machine.Blocking);
Check(machine.State == SweepState.Completed && machine.Results[0].Count == 0 && machine.Results[0].Gil == 0,
    "manual reuse after explicit rejection accepts a real zero-item completion");
Check(session.BeginCalls == 3 && session.StopCalls == 0, "manual restart submits the new attempt exactly once");

host = new FakeHost { CurrentCid = 1 }; session = new() { AutoComplete = true, NextCount = 5, NextGil = 35 }; machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2), Character(3)], Rules(), Fast(), now);
Until(machine, () => machine.Results[0].Status == VendorCharacterStatus.Completed);
session.ThrowBeforeReset = true;
for(var i = 0; i < 50 && machine.Running; i++) { if(host.Pending is { } pending) host.Arrive(pending.Cid); Tick(machine); }
Check(machine.State == SweepState.Failed && machine.Blocking && session.StopCalls == 1,
    "unknown Begin failure retains cleanup ownership and requests stop once");
Check(machine.Results[1].Count == 0 && machine.Results[1].Gil == 0 && machine.Results[1].Detail != "completed previous session",
    "unknown Begin never borrows old terminal counters or detail");
session.Finish(VendorSessionState.Stopped); Tick(machine);
Check(machine.Blocking, "rewriting an old terminal snapshot is not evidence this unknown attempt ended");
machine.Stop(now); Tick(machine, 16_000);
Check(machine.Blocking && !machine.Running && session.StopCalls == 1 && session.BeginCalls == 2 && host.Changes.Count == 1,
    "unknown Begin remains blocked after timeout without retry or subsequent character");
Check(machine.Results.Sum(x => x.Count) == 5 && machine.Results.Sum(x => x.Gil) == 35,
    "unknown attempt's timeout also preserves only A's verified receipts");

host = new FakeHost { CurrentCid = 1 }; session = new() { ThrowBegin = true }; machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), Fast(), now); Until(machine, () => session.BeginCalls == 1);
Check(machine.State == SweepState.Failed && session.StopCalls == 1 && machine.Blocking, "begin exception after submission still owns cleanup");
session.Finish(VendorSessionState.Stopped); Tick(machine);
Check(!machine.Blocking && session.BeginCalls == 1, "uncertain begin is not blindly retried");

host = new FakeHost { CurrentCid = 99, ThrowChange = true }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), Fast(), now); Until(machine, () => host.Changes.Count == 1);
Check(machine.State == SweepState.Failed && machine.Blocking, "throwing relog call can still have pending effect");
host.Arrive(1); Tick(machine); Tick(machine);
Check(!machine.Blocking && session.BeginCalls == 0 && host.Changes.Count == 1, "late relog after exception only drains");

host = new FakeHost { CurrentCid = 99, RejectChange = true }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), Fast(), now); Until(machine, () => host.Changes.Count == 1);
Check(machine.State == SweepState.Failed && !machine.Blocking && host.Pending == null && session.BeginCalls == 0,
    "positive unqueued relog rejection can finish cleanup without awaiting impossible target");
host.RejectChange = false; machine.Start([Character(1)], Rules(), Fast(), now);
Check(machine.Running, "known rejected request permits a fresh manual start"); machine.Stop(now);

host = new FakeHost { CurrentCid = 1, ThrowPrepare = true }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => host.PrepareCalls == 1);
Check(machine.State == SweepState.Failed && !machine.Blocking && session.BeginCalls == 0 && host.Changes.Count == 0,
    "failed character handoff does not submit native session or proceed to another role");

host = new FakeHost { CurrentCid = 1 }; session = new() { AutoComplete = true, NextCount = long.MaxValue, NextGil = 1 }; machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => machine.Results[0].Status == VendorCharacterStatus.Completed);
session.NextCount = 1;
for(var i = 0; i < 50 && machine.Running; i++) { if(host.Pending is { } pending) host.Arrive(pending.Cid); Tick(machine); }
Check(machine.State == SweepState.Failed && machine.Results[1].Count == 0,
    "aggregate quantity overflow stops before publishing an overflowing result");
Check(machine.Results.Sum(x => x.Count) == long.MaxValue, "published totals remain representable after bad session report");

host = new FakeHost { CurrentCid = 99 }; session = new() { AutoComplete = true }; machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), new VendorDelays
    { StartMs = 60000, RelogMs = 60000, LoginStableMs = 60000, TimeoutSeconds = 15, RelogTimeoutSeconds = 15 }, now);
machine.Tick(60000); machine.Tick(60001); machine.Tick(120000);
Check(machine.Running && host.Changes.Count == 0, "long startup and relog delays do not consume response budget");
machine.Tick(120001); host.Arrive(1); machine.Tick(120002); machine.Tick(180001);
Check(machine.Running && session.BeginCalls == 0, "long login stabilization does not cause early response timeout");
machine.Tick(180002); machine.Tick(180003);
Check(machine.State == SweepState.Completed && session.BeginCalls == 1, "zero matching sales can finish after long configured waits");

host = new FakeHost { CurrentCid = 1 }; session = new(); machine = new(host, session); now = 0;
machine.Start([Character(1)], Rules(), Fast(), now); Until(machine, () => session.BeginCalls == 1);
session.SoldItems = -1; session.Gil = -1; Tick(machine);
Check(machine.State == SweepState.Failed && machine.Results[0].Count == 0 && machine.Results[0].Gil == 0,
    "negative service counters never leak into results");

host = new FakeHost { CurrentCid = 1 }; session = new() { AutoComplete = true, NextCount = 0, NextGil = long.MaxValue }; machine = new(host, session); now = 0;
machine.Start([Character(1), Character(2)], Rules(), Fast(), now);
Until(machine, () => machine.Results[0].Status == VendorCharacterStatus.Completed); session.NextGil = 1;
for(var i = 0; i < 50 && machine.Running; i++) { if(host.Pending is { } pending) host.Arrive(pending.Cid); Tick(machine); }
Check(machine.State == SweepState.Failed && machine.Results[1].Gil == 0 && machine.Results.Sum(x => x.Gil) == long.MaxValue,
    "aggregate gil overflow cannot wrap or publish a misleading amount");
Console.WriteLine($"RetainerVendorSweep: {checks} checks passed.");

sealed class FakeHost : ISweepHost
{
    public ulong CurrentCid { get; set; }
    public bool LoggedIn { get; set; } = true;
    public bool IsSwitching { get; set; }
    public bool SafeForSwitch { get; set; } = true;
    public string? ConflictReason;
    public bool MarkSwitchBusy = true, ThrowChange, RejectChange, ThrowPrepare;
    public int PrepareCalls;
    public VendorCharacter? Pending;
    public List<VendorCharacter> Changes = [];
    public string? Conflict() => ConflictReason;
    public void PrepareCharacter()
    {
        PrepareCalls++;
        if(ThrowPrepare) throw new InvalidOperationException("handoff refused");
    }
    public void ChangeCharacter(VendorCharacter character)
    {
        Changes.Add(character);
        if(RejectChange) throw new CharacterSwitchRejectedException("explicit provider rejection");
        Pending = character;
        if(MarkSwitchBusy) { IsSwitching = true; SafeForSwitch = false; }
        if(ThrowChange) throw new InvalidOperationException("request may already be queued");
    }
    public void Arrive(ulong cid) { CurrentCid = cid; LoggedIn = SafeForSwitch = true; IsSwitching = false; Pending = null; }
}
sealed class FakeSession : IVendorSession
{
    public VendorSessionState State { get; private set; }
    public bool Blocking { get; private set; }
    public string Detail { get; private set; } = "fake";
    public long SoldItems { get; set; }
    public long Gil { get; set; }
    public bool AutoComplete, ThrowBegin, RejectBeforeReset, ThrowBeforeReset;
    public long NextCount, NextGil;
    public int BeginCalls, StopCalls, TickCalls;
    public List<ulong> Begun = [];
    public List<SaleRules> CapturedRules = [];
    public void Begin(ulong cid, SaleRules rules, VendorDelays delays)
    {
        BeginCalls++; Begun.Add(cid); CapturedRules.Add(rules);
        if(RejectBeforeReset) throw new VendorBeginRejectedException("B preflight rejected");
        if(ThrowBeforeReset) throw new InvalidOperationException("unknown failure before state publication");
        SoldItems = Gil = 0; Detail = "current session";
        State = VendorSessionState.Running; Blocking = true;
        if(ThrowBegin) throw new InvalidOperationException("queued before throwing");
    }
    public void Tick()
    {
        TickCalls++;
        if(AutoComplete && State == VendorSessionState.Running)
        { SoldItems = NextCount; Gil = NextGil; Finish(VendorSessionState.Completed); }
    }
    public void Stop() { StopCalls++; State = VendorSessionState.Stopping; }
    public void Finish(VendorSessionState state)
    {
        State = state; Blocking = false;
        if(state == VendorSessionState.Completed) Detail = "completed previous session";
    }
}
