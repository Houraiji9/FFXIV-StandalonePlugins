using System.Text.Json;
using RetainerWorkshop;
using RetainerVendorSweep;
using RetainerCraftBridge;

var checks = 0;
void Check(bool condition, string description) { checks++; if(!condition) throw new Exception(description); }
void Reject(Action action, string description)
{
    var rejected = false;
    try { action(); } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException) { rejected = true; }
    Check(rejected, description);
}
var root = Path.Combine(Path.GetTempPath(), "RetainerWorkshopTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fileNumber = 0;
string FilePath() => Path.Combine(root, (++fileNumber).ToString(), "state.json");
ScheduleSettings Schedule(int threshold) => new() { Enabled = true, RoundsBeforeSale = threshold, TimeZoneId = "UTC" };
VendorCharacter Character(ulong cid) => new(cid, "Character" + cid, "World");
SaleRules Rules() => new() { ItemIds = [100] };
VendorDelays Fast() => new() { StartMs = 100, RelogMs = 100, LoginStableMs = 100, NextCharacterMs = 100 };
try
{
    using(var f = new Fixture(FilePath()))
    {
        Check(!f.C.Blocking && f.R.BeginCollections == 0, "construction never begins or resumes work");
        f.C.Start(Schedule(2), 2, true);
        f.CollectionDone();
        Check(f.C.CompletedRounds == 1 && f.C.Stage == WorkshopStage.Crafting && f.R.BeginCrafts == 1,
            "N=M first complete round permits one idle craft period");
        f.R.FirstReady = true; f.C.Tick(); f.C.Tick();
        Check(f.R.CraftStops == 1 && f.R.BeginCollections == 2, "first readiness stops crafting before next collection");
        f.R.FirstReady = false; f.CollectionDone();
        Check(f.C.CompletedRounds == 2 && f.C.StopLatched && f.C.Stage == WorkshopStage.Selling && f.R.BeginSales.Count == 1,
            "N=M final collection latches limit and starts its required sale");
        f.R.FirstReady = true; f.C.Tick();
        Check(f.R.PauseRequests.Last() == false, "limit-latched sale is not paused for a new collection");
        f.SaleDone();
        Check(f.C.Stage == WorkshopStage.LimitReached && !f.C.Blocking && f.R.BeginCrafts == 1,
            "same-round sale finishes before overall standby without another craft");
        for(var i = 0; i < 10; i++) f.C.Tick();
        Check(f.R.BeginCollections == 2 && f.R.BeginSales.Count == 1, "readiness after quota cannot reopen rotation or sale");
        Check(f.L.Tasks.Single().Status == SaleTaskStatus.Completed, "completed required sale consumes exactly its durable period task");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.R.FirstReady = true; f.C.Start(Schedule(2), 3, false);
        f.CollectionDone(); f.CollectionDone();
        Check(f.C.Stage == WorkshopStage.Selling && !f.C.StopLatched, "N<M triggers sale before rotation quota");
        f.SaleDone(); f.CollectionDone();
        Check(f.C.Stage == WorkshopStage.LimitReached && f.C.CompletedRounds == 3 && f.R.BeginSales.Count == 1,
            "N<M counts the remaining collection without selling twice in the period");
        Check(f.R.BeginCollections == 3 && f.R.BeginCrafts == 0, "disabled crafting and rotation limit both respected");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.R.FirstReady = true; f.C.Start(Schedule(3), 2, true);
        f.CollectionDone(); f.CollectionDone();
        Check(f.C.Stage == WorkshopStage.LimitReached && f.R.BeginSales.Count == 0 && f.L.Tasks.Count == 0,
            "N>M stops rotation without manufacturing a not-yet-earned sale task");
        Check(f.R.BeginCrafts == 0 && f.C.CompletedRounds == 2, "quota takes priority over beginning idle crafting");
    }
    using(var f = new Fixture(FilePath()))
    {
        var settings = Schedule(1); f.C.Start(settings, 1, true); settings.RoundsBeforeSale = 99;
        f.CollectionDone();
        Check(f.R.BeginSales.Count == 1, "coordinator freezes schedule before caller edits settings");
        f.R.SaleState = SaleBatchState.Failed; f.R.Blocking = false; f.R.Detail = "partial sale B failed"; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Stopping && f.R.StopCalls == 1 && f.L.State == LedgerState.NeedsAttention,
            "partial sale failure revokes other stages and quarantines sale intent");
        f.R.FirstReady = true; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Failed && f.R.BeginCollections == 1 && f.R.BeginCrafts == 0,
            "sale failure at limit cannot restore rotation or crafting");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.Start(Schedule(1), 1, true); f.C.Stop();
        f.R.CollectionState = SessionStatus.Completed; f.R.Blocking = false; f.R.FirstReady = true; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Stopped && f.C.CompletedRounds == 0 && f.R.BeginSales.Count == 0 && f.R.BeginCrafts == 0,
            "manual stop wins over a late collection completion and revokes sale/craft");
        Check(f.L.Tasks.Count == 0, "late completion after stop cannot allocate automatic task");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.Start(Schedule(1), 1, true); f.CollectionDone(); f.C.Stop();
        Check(f.R.StopCalls == 1 && f.L.Tasks.Single().Status == SaleTaskStatus.NeedsAttention,
            "manual stop interrupts a limit-triggered sale instead of forcing it to finish");
        f.R.Blocking = false; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Stopped && f.R.BeginCollections == 1 && f.R.BeginCrafts == 0,
            "manual sale cancellation cannot restart automatic work");
        Reject(() => f.C.Start(Schedule(1), 1, true), "unresolved sale prevents a fresh automatic start");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.Start(Schedule(1), 2, false); f.CollectionDone();
        var old = f.R.BeginSales.Single();
        f.R.FirstReady = true; f.R.SaleState = SaleBatchState.Paused; f.R.Blocking = false; f.C.Tick();
        Check(f.R.BeginCollections == 2 && f.R.BeginSales.Count == 1, "paused old sale returns to one collection without restarting its cursor");
        f.Now = f.Now.AddDays(1); f.CollectionDone();
        Check(f.C.StopLatched && f.R.Resumes == 1 && f.R.BeginSales.Count == 1 && f.L.Tasks.Count == 2,
            "cross-day completion queues new task but resumes old sale first");
        f.SaleDone();
        Check(f.R.BeginSales.Count == 2 && f.R.BeginSales[1] != old && f.C.Stage == WorkshopStage.Selling,
            "new-day sale dispatch waits for the old task's verified completion");
        f.SaleDone();
        Check(f.C.Stage == WorkshopStage.LimitReached && f.L.Tasks.All(x => x.Status == SaleTaskStatus.Completed)
            && f.R.BeginCollections == 2, "both already-triggered tasks finish before quota standby");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.StartManualSale();
        Check(f.R.BeginSales.Single().StartsWith("manual-", StringComparison.Ordinal) && f.L.Tasks.Count == 0,
            "manual sale is separate from automatic period allowance");
        f.R.FirstReady = true; f.SaleDone();
        Check(f.C.Stage == WorkshopStage.Stopped && f.R.BeginCollections == 0 && f.R.BeginCrafts == 0,
            "manual sale never enters automatic rotation or craft afterward");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.Start(Schedule(1), 1, true); f.FailSave = true; f.CollectionDone();
        Check(f.R.StopCalls > 0 && f.R.BeginSales.Count == 0 && f.R.BeginCrafts == 0,
            "checkpoint failure after counting collection stops before dispatching earned sale");
        Check(f.C.Blocking && f.C.Stage == WorkshopStage.Stopping && f.L.State == LedgerState.NeedsAttention,
            "storage failure retains blocking stop state and task quarantine");
        f.C.Tick();
        Check(f.C.Blocking && f.C.Stage == WorkshopStage.Stopping, "failed terminal checkpoint cannot claim released coordinator");
        f.FailSave = false; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Failed && !f.C.Blocking, "only a committed terminal record releases failed cleanup");
    }
    using(var f = new Fixture(FilePath()))
    {
        f.C.Start(Schedule(2), 3, false); f.R.CollectionState = SessionStatus.Failed; f.R.Blocking = false; f.C.Tick(); f.C.Tick();
        Check(f.C.CompletedRounds == 0 && f.C.Stage == WorkshopStage.Failed && f.L.Tasks.Count == 0,
            "failed collection never counts a completed round");
    }
    foreach(var manualStart in new[] { false, true })
    {
        using(var f = new Fixture(FilePath()))
        {
            f.FailSave = true;
            if(manualStart) f.C.StartManualSale(); else f.C.Start(Schedule(1), 1, true);
            Check(f.C.Stage == WorkshopStage.Stopping && f.R.StopCalls == 1
                && f.R.BeginCollections == 0 && f.R.BeginSales.Count == 0,
                "initial checkpoint failure revokes start before any game-facing begin");
            f.C.Tick(); Check(f.C.Blocking, "initial save failure stays blocked until terminal journal can be written");
            f.FailSave = false; f.C.Tick();
            Check(f.C.Stage == WorkshopStage.Failed && !f.C.Blocking, "initial journal failure can finish cleanup once durable storage recovers");
        }
        using(var f = new Fixture(FilePath()))
        {
            f.R.ThrowAfterBegin = true;
            if(manualStart) f.C.StartManualSale(); else f.C.Start(Schedule(1), 1, true);
            Check(f.C.Stage == WorkshopStage.Stopping && f.R.StopCalls == 1 && f.C.Blocking,
                "unknown exception after native begin acquired ownership always requests cancellation");
            f.C.Tick();
            Check(f.C.Stage == WorkshopStage.Failed && f.R.BeginCrafts == 0
                && f.R.BeginCollections + f.R.BeginSales.Count == 1,
                "unknown initial begin drains without retry or switching to another stage");
        }
    }
    using(var f = new Fixture(FilePath()))
    {
        f.R.Blocking = true;
        Reject(() => f.C.Start(Schedule(1), 1, true), "busy runtime refuses new run before acquiring it");
        Check(f.R.StopCalls == 0 && f.R.BeginCollections == 0, "rejected initialization does not stop an unrelated runtime activity");
    }

    // Use the same readiness adapter as the IPC host: an observed head can legitimately have
    // no in-progress ventures immediately after collection. It is waiting, not an IPC failure.
    foreach(var scenario in new[] { (Threshold: 1, Limit: 0), (Threshold: 2, Limit: 3) })
    {
        using var f = new Fixture(FilePath());
        ReadinessCharacter head = new(true, false, [0]);
        var timing = new RetainerTiming { AdvanceTimer = 0, MultiWaitForAll = true };
        f.R.FirstReadyProbe = () => WorkshopReadiness.IsFirstReady(head, timing);
        f.C.Start(Schedule(scenario.Threshold), scenario.Limit, false);
        for(var completed = 1; completed <= scenario.Threshold; completed++)
        {
            head = new(true, false, []); f.CollectionDone();
            if(completed < scenario.Threshold)
            {
                Check(f.C.Stage == WorkshopStage.Waiting && f.R.StopCalls == 0,
                    "empty head after a below-threshold collection waits without fault");
                head = new(true, false, [0]); f.C.Tick();
            }
        }
        Check(f.C.Stage == WorkshopStage.Selling && f.R.BeginSales.Count == 1 && f.C.CompletedRounds == scenario.Threshold,
            "empty head after threshold collection still starts the earned automatic sale");
        for(var i = 0; i < 3; i++) f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Selling && f.R.StopCalls == 0 && f.R.PauseRequests.All(x => !x)
            && f.L.State == LedgerState.Ready && f.L.Tasks.Single().Status == SaleTaskStatus.InProgress,
            "real empty-head readiness probe neither interrupts nor quarantines the active sale");
        f.SaleDone();
        Check(f.C.Stage == WorkshopStage.Waiting && f.L.Tasks.Single().Status == SaleTaskStatus.Completed && !f.C.StopLatched,
            "after earned sale finishes, empty head reaches Waiting for unbounded and below-limit runs");
        for(var i = 0; i < 3; i++) f.C.Tick();
        Check(f.R.BeginSales.Count == 1 && f.R.BeginCollections == scenario.Threshold,
            "continued empty head cannot duplicate sale or open another collection");
        head = new(true, false, [600]); f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Waiting, "new selected venture still in the future keeps waiting");
        head = new(true, false, [0]); f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Collecting && f.R.BeginCollections == scenario.Threshold + 1
            && f.R.BeginSales.Count == 1 && f.L.Tasks.Count == 1,
            "only a newly selected due venture resumes collection and does not reissue the prior sale");
    }
    using(var f = new Fixture(FilePath()))
    {
        ReadinessCharacter head = new(true, false, [0]);
        var timing = new RetainerTiming { AdvanceTimer = 30, MultiWaitForAll = false };
        f.R.FirstReadyProbe = () => WorkshopReadiness.IsFirstReady(head, timing);
        var disabledSale = Schedule(1); disabledSale.Enabled = false;
        f.C.Start(disabledSale, 0, true);
        head = new(true, false, []); f.CollectionDone();
        Check(f.C.Stage == WorkshopStage.Crafting && f.R.BeginCrafts == 1 && f.R.BeginSales.Count == 0,
            "empty head permits optional crafting after collection when automatic sale is disabled");
        for(var i = 0; i < 3; i++) f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Crafting && f.R.CraftStops == 0 && f.R.StopCalls == 0,
            "empty head is not readiness and cannot prematurely stop the current craft period");
        f.R.CraftState = SessionStatus.Completed; f.R.Blocking = false; f.C.Tick();
        for(var i = 0; i < 3; i++) f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Waiting && f.R.BeginCrafts == 1 && f.R.BeginCollections == 1 && f.L.Tasks.Count == 0,
            "completed crafting with an empty head remains Waiting without restarting craft or sale");
        head = new(true, false, [30]); f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Collecting && f.R.BeginCollections == 2,
            "real helper still honors configured advance window for a newly due selected venture");
    }
    foreach(var invalidHead in new[] { new ReadinessCharacter(false, false, []), new ReadinessCharacter(true, true, []) })
    {
        using var f = new Fixture(FilePath());
        ReadinessCharacter head = new(true, false, [0]);
        var timing = new RetainerTiming();
        f.R.FirstReadyProbe = () => WorkshopReadiness.IsFirstReady(head, timing);
        f.C.Start(Schedule(1), 0, false); head = new(true, false, []); f.CollectionDone();
        head = invalidHead; f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Stopping && f.R.StopCalls == 1
            && f.L.Tasks.Single().Status == SaleTaskStatus.NeedsAttention,
            "disabled or excluded head remains a real readiness fault and stops automatic sale");
        f.C.Tick();
        Check(f.C.Stage == WorkshopStage.Failed && f.R.BeginCollections == 1 && f.R.BeginSales.Count == 1,
            "invalid head cannot silently wait, resume rotation or retry sale after cleanup");
    }

    var host = new FakeHost { CurrentCid = 1 }; var session = new FakeSession();
    var progress = new List<string>(); long now = 0;
    var batch = new SaleBatch(host, session, progress.Add);
    var characters = Enumerable.Range(1, 25).Select(x => Character((ulong)x)).ToList();
    var selectedRules = Rules(); var selectedDelays = Fast();
    void Advance(SaleBatch target, bool pause = false)
    {
        if(host.Pending is { } arriving) host.Arrive(arriving.Cid);
        now += 100; target.Tick(now, pause);
    }
    void Until(SaleBatch target, Func<bool> done, bool pause = false)
    {
        for(var i = 0; i < 1000 && !done(); i++) Advance(target, pause);
        Check(done(), "bounded sale-batch progress");
    }
    batch.Begin("task-many", characters, selectedRules, selectedDelays, now);
    characters[0] = Character(900); selectedRules.ItemIds.Clear(); selectedDelays.NextCharacterMs = 60_000;
    Until(batch, () => batch.State == SaleBatchState.Paused, true);
    Check(batch.Results.Count == 1 && batch.Results[0].Character.Cid == 1 && session.Begun.SequenceEqual(new ulong[] { 1 }),
        "25-role task pauses only after current role finishes and preserves first confirmed receipt");
    for(var i = 0; i < 10; i++) Advance(batch, true);
    Check(session.Begun.Count == 1 && host.Changes.Count == 0, "paused batch does not emit another relog or session");
    Reject(() => batch.Begin("replacement", [Character(2)], Rules(), Fast(), now), "paused task cannot be overwritten");
    batch.Resume(now); Until(batch, () => batch.State == SaleBatchState.Completed);
    Check(session.Begun.SequenceEqual(Enumerable.Range(1, 25).Select(x => (ulong)x)), "resume handles all 25 roles once without restarting the first");
    Check(batch.Results.Count == 25 && batch.Results.Sum(x => x.Count) == 125 && batch.Results.Sum(x => x.Gil) == 875,
        "resumed task retains all confirmed per-role item and gil totals");
    Check(session.CapturedRules.All(x => x.ItemIds.Contains(100)) && host.Changes.Count == 24 && host.PrepareCalls == 25,
        "sale batch freezes rules/order/delays and performs one handoff per role");
    Check(progress.Any(x => x.Contains("已处理 1/25", StringComparison.Ordinal)) && progress.Last().Contains("Completed", StringComparison.Ordinal),
        "role progress and final completion are persisted");

    host = new() { CurrentCid = 1 }; session = new(); now = 0;
    batch = new(host, session, _ => { }); batch.Begin("stop-paused", [Character(1), Character(2)], Rules(), Fast(), now);
    Until(batch, () => batch.State == SaleBatchState.Paused, true); batch.Stop(now);
    Reject(() => batch.Resume(now), "manually stopped paused task cannot resume its remaining role");
    Check(batch.Results.Single().Count == 5 && session.StopCalls == 0 && session.Begun.Count == 1,
        "stop of released paused task retains A's result without canceling the old completed session");

    host = new() { CurrentCid = 1 }; session = new(); now = 0;
    batch = new(host, session, _ => { }); batch.Begin("stop-before-first", [Character(1), Character(2)], Rules(), Fast(), now);
    batch.Stop(now); for(var i = 0; i < 10; i++) Advance(batch);
    Check(batch.State == SaleBatchState.Stopped && !batch.Blocking && session.Begun.Count == 0 && host.Changes.Count == 0,
        "stopping during initial delay revokes all native sale and relog requests");

    host = new() { CurrentCid = 1 }; session = new(); now = 0;
    batch = new(host, session, text => { if(text.Contains("已处理 1/2", StringComparison.Ordinal)) throw new IOException("disk full"); });
    batch.Begin("save-fails", [Character(1), Character(2)], Rules(), Fast(), now);
    Until(batch, () => batch.State == SaleBatchState.Failed);
    Check(session.Begun.SequenceEqual(new ulong[] { 1 }) && host.Changes.Count == 0 && batch.Results.Single().Count == 5,
        "progress write failure preserves confirmed first role but forbids next-role request");

    foreach(var knownRejection in new[] { true, false })
    {
        host = new() { CurrentCid = 1 }; session = new(); now = 0;
        batch = new(host, session, _ => { }); batch.Begin("begin-failure", [Character(1), Character(2), Character(3)], Rules(), Fast(), now);
        Until(batch, () => batch.State == SaleBatchState.Paused, true);
        session.RejectBeforeReset = knownRejection; session.ThrowBeforeReset = !knownRejection;
        batch.Resume(now); Until(batch, () => batch.State == SaleBatchState.Failed);
        Check(batch.Results.Count == 2 && batch.Results[0].Count == 5 && batch.Results[0].Gil == 35
            && batch.Results[1].Count == 0 && batch.Results[1].Gil == 0,
            "failed B Begin never borrows A's old completed five/thirty-five receipt");
        Check(session.Begun.SequenceEqual(new ulong[] { 1, 2 }) && host.Changes.Count == 1,
            "Begin failure does not retry B or dispatch C");
        Check(knownRejection ? !batch.Blocking : batch.Blocking, "known rejection releases; unknown Begin keeps conservative ownership");
        if(!knownRejection)
        {
            session.Finish(VendorSessionState.Stopped); batch.Stop(now);
            for(var i = 0; i < 10; i++) Advance(batch);
            Check(batch.Blocking && batch.Results.Sum(x => x.Count) == 5 && session.StopCalls == 1,
                "unknown stale terminal remains blocked without replay or borrowed totals");
        }
    }

    host = new() { CurrentCid = 1 }; session = new() { AutoComplete = false }; now = 0;
    batch = new(host, session, _ => { }); batch.Begin("stop-partial", [Character(1), Character(2)], Rules(), Fast(), now);
    Until(batch, () => session.Begun.Count == 1);
    session.SoldItems = 3; session.Gil = 21; Advance(batch);
    batch.Stop(now); session.Finish(VendorSessionState.Stopped); Advance(batch);
    Check(!batch.Blocking && batch.Results.Single().Count == 3 && batch.Results.Single().Gil == 21 && host.Changes.Count == 0,
        "manual stop retains verified partial receipt while draining and never moves to next role");

    var journalPath = FilePath(); var journal = new WorkshopRunJournal(journalPath);
    Check(!journal.NeedsAttention && journal.Last == null, "absent journal never resumes an old run");
    var checkpoint = new WorkshopCheckpoint(Guid.NewGuid(), WorkshopStage.Selling.ToString(), 2, true, "old-task", ["queued-task"], "active", DateTimeOffset.UtcNow);
    journal.Save(checkpoint); journal = new(journalPath);
    Check(journal.NeedsAttention && journal.Last?.SaleTaskId == "old-task" && journal.Last.PendingSaleTasks.Single() == "queued-task",
        "reload preserves in-flight identifiers but requires manual check instead of resuming");
    journal.Acknowledge(); journal = new(journalPath);
    Check(!journal.NeedsAttention && journal.Last?.Stage == WorkshopStage.Stopped.ToString(), "manual journal check persists safe no-resume state");
    foreach(var terminal in new[] { WorkshopStage.Stopped, WorkshopStage.LimitReached, WorkshopStage.Failed })
    {
        journal.Save(checkpoint with { Stage = terminal.ToString() });
        Check(!new WorkshopRunJournal(journalPath).NeedsAttention, "committed terminal checkpoint does not become automatic work on reload");
    }
    var prior = journal.Last;
    using(var denyReplace = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        Reject(() => journal.Save(checkpoint), "journal atomic rename failure propagates to coordinator");
    Check(journal.Last == prior && JsonSerializer.Serialize(new WorkshopRunJournal(journalPath).Last) == JsonSerializer.Serialize(prior),
        "failed save changes neither published nor durable checkpoint");
    foreach(var invalid in new[] { "", "{}", "{bad", "null" })
    {
        File.WriteAllText(journalPath, invalid);
        Check(new WorkshopRunJournal(journalPath).NeedsAttention, "invalid run history cannot authorize start");
    }
    foreach(var invalid in new[] {
        checkpoint with { RunId = Guid.Empty }, checkpoint with { CompletedRounds = -1 },
        checkpoint with { Stage = "999" }, checkpoint with { Stage = "not-a-stage" },
        checkpoint with { PendingSaleTasks = null! }, checkpoint with { PendingSaleTasks = [""] },
        checkpoint with { UpdatedUtc = default }, checkpoint with { Detail = null! }, checkpoint with { SaleTaskId = " " } })
    {
        Reject(() => journal.Save(invalid), "invalid checkpoint rejected before durable publication");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(invalid));
        Check(new WorkshopRunJournal(journalPath).NeedsAttention, "syntactically valid but incomplete checkpoint cannot clear recovery gate");
    }
    Console.WriteLine($"RetainerWorkshop: {checks} checks passed.");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    var allowedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if(fullRoot.StartsWith(allowedTemp, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fullRoot).StartsWith("RetainerWorkshopTests-", StringComparison.Ordinal)
        && Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
}

sealed class Fixture : IDisposable
{
    public readonly FakeRuntime R = new();
    public readonly ScheduleLedger L;
    public readonly WorkshopCoordinator C;
    public readonly List<WorkshopCheckpoint> Saves = [];
    public DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    public bool FailSave;
    public Fixture(string path)
    {
        L = new(path);
        C = new(R, L, value => { if(FailSave) throw new IOException("checkpoint write failed"); Saves.Add(value); }, () => Now);
    }
    public void CollectionDone() { R.CollectionState = SessionStatus.Completed; R.Blocking = false; C.Tick(); }
    public void SaleDone() { R.SaleState = SaleBatchState.Completed; R.Blocking = false; C.Tick(); }
    public void Dispose() => L.Dispose();
}
sealed class FakeRuntime : IWorkshopRuntime
{
    public bool Blocking { get; set; }
    public bool SafeIdle => !Blocking;
    private bool firstReady;
    public Func<bool>? FirstReadyProbe { get; set; }
    public bool FirstReady { get => FirstReadyProbe?.Invoke() ?? firstReady; set => firstReady = value; }
    public SessionStatus CollectionState { get; set; }
    public SessionStatus CraftState { get; set; }
    public SaleBatchState SaleState { get; set; }
    public string Detail { get; set; } = "fake runtime";
    public int BeginCollections, BeginCrafts, CraftStops, Resumes, StopCalls;
    public bool ThrowAfterBegin;
    public readonly List<string> BeginSales = [];
    public readonly List<bool> PauseRequests = [];
    public void BeginCollection()
    {
        BeginCollections++; CollectionState = SessionStatus.Running; Blocking = true;
        if(ThrowAfterBegin) throw new InvalidOperationException("accepted before exception");
    }
    public void TickCollection() { }
    public void BeginCraft() { BeginCrafts++; CraftState = SessionStatus.Running; Blocking = true; }
    public void TickCraft() { }
    public void StopCraft() { CraftStops++; CraftState = SessionStatus.Stopped; Blocking = false; }
    public void BeginSale(string taskId)
    {
        BeginSales.Add(taskId); SaleState = SaleBatchState.Running; Blocking = true;
        if(ThrowAfterBegin) throw new InvalidOperationException("accepted before exception");
    }
    public void ResumeSale() { Resumes++; SaleState = SaleBatchState.Running; Blocking = true; }
    public void TickSale(bool pauseAfterCharacter) => PauseRequests.Add(pauseAfterCharacter);
    public void StopAll() => StopCalls++;
    public void Drain() => Blocking = false;
}
sealed class FakeHost : ISweepHost
{
    public ulong CurrentCid { get; set; }
    public bool LoggedIn => true;
    public bool IsSwitching => Pending != null;
    public bool SafeForSwitch => Pending == null;
    public VendorCharacter? Pending;
    public int PrepareCalls;
    public List<VendorCharacter> Changes = [];
    public string? Conflict() => null;
    public void PrepareCharacter() => PrepareCalls++;
    public void ChangeCharacter(VendorCharacter character) { Changes.Add(character); Pending = character; }
    public void Arrive(ulong cid) { CurrentCid = cid; Pending = null; }
}
sealed class FakeSession : IVendorSession
{
    public VendorSessionState State { get; private set; }
    public bool Blocking { get; private set; }
    public string Detail { get; private set; } = "fake";
    public long SoldItems { get; set; }
    public long Gil { get; set; }
    public bool AutoComplete = true, RejectBeforeReset, ThrowBeforeReset;
    public int StopCalls;
    public List<ulong> Begun = [];
    public List<SaleRules> CapturedRules = [];
    public void Begin(ulong cid, SaleRules rules, VendorDelays delays)
    {
        Begun.Add(cid); CapturedRules.Add(rules);
        if(RejectBeforeReset) throw new VendorBeginRejectedException("B rejected before send");
        if(ThrowBeforeReset) throw new InvalidOperationException("unknown B exception");
        SoldItems = Gil = 0; State = VendorSessionState.Running; Blocking = true;
    }
    public void Tick()
    {
        if(AutoComplete && State == VendorSessionState.Running)
        { SoldItems = 5; Gil = 35; Finish(VendorSessionState.Completed); }
    }
    public void Stop() { StopCalls++; State = VendorSessionState.Stopping; }
    public void Finish(VendorSessionState state) { State = state; Blocking = false; Detail = state.ToString(); }
}
