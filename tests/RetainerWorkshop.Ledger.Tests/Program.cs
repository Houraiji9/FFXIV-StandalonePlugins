using System.Text.Json.Nodes;
using RetainerWorkshop;

var checks = 0;
void Check(bool condition, string description) { checks++; if(!condition) throw new Exception(description); }
void Reject(Action action, string description)
{
    var rejected = false;
    try { action(); } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException) { rejected = true; }
    Check(rejected, description);
}
var root = Path.Combine(Path.GetTempPath(), "RetainerWorkshopLedgerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var nextFile = 0;
string NewPath() => Path.Combine(root, (++nextFile).ToString(), "schedule.json");
ScheduleSettings Daily(int count = 2) => new() { Enabled = true, Mode = PeriodMode.Daily, TimeZoneId = "UTC", RoundsBeforeSale = count };
DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
var morning = At("2026-11-03T08:00:00Z");
var nextDay = At("2026-11-04T08:00:00Z");
try
{
    var settings = Daily();
    Check(settings.Validate() == null, "valid explicit timezone and threshold");
    var frozen = settings.Freeze(); settings.RoundsBeforeSale = 9;
    Check(frozen.RoundsBeforeSale == 2 && !ReferenceEquals(settings, frozen), "freeze isolates mutable configuration");
    foreach(var invalid in new[] {
        new ScheduleSettings { RoundsBeforeSale = 0 }, new ScheduleSettings { RoundsBeforeSale = -1 },
        new ScheduleSettings { ResetMinuteOfDay = -1 }, new ScheduleSettings { ResetMinuteOfDay = 1440 },
        new ScheduleSettings { Mode = (PeriodMode)5 }, new ScheduleSettings { WeekStartsOn = (DayOfWeek)7 },
        new ScheduleSettings { TimeZoneId = "Not/ARealZone" }, new ScheduleSettings { TimeZoneId = "" } })
    {
        Check(invalid.Validate() != null, "invalid schedule fails validation");
        Reject(() => invalid.Freeze(), "invalid schedule is not silently clamped");
    }

    var path = NewPath();
    using(var ledger = new ScheduleLedger(path))
    {
        Check(ledger.State == LedgerState.Ready && ledger.Issue == null && ledger.Tasks.Count == 0, "new ledger is idle and ready");
        var initialBytes = File.ReadAllText(path);
        var before = ledger.GetPeriod(Daily(), morning);
        Check(before.CompletedRounds == 0 && before.SaleTaskId == null && File.ReadAllText(path) == initialBytes, "display does not persist or allocate anything");
        Check(before.StartsAt == At("2026-11-03T00:00:00Z") && before.EndsAt == At("2026-11-04T00:00:00Z"), "daily UTC boundaries exact");
        var run = Guid.NewGuid();
        Reject(() => ledger.RecordCompleted(Guid.Empty, 1, Daily(), morning), "empty run identity rejected");
        Reject(() => ledger.RecordCompleted(run, 0, Daily(), morning), "zero cycle rejected");
        var disabled = Daily(); disabled.Enabled = false;
        var ignored = ledger.RecordCompleted(run, 1, disabled, morning);
        Check(ignored.CompletedRounds == 0 && File.ReadAllText(path) == initialBytes, "disabled schedule does not count or allocate");
        var first = ledger.RecordCompleted(run, 1, Daily(), morning);
        Check(first.CompletedRounds == 1 && first.NewSaleTaskId == null && File.Exists(path), "first completed round saved without task");
        var duplicate = ledger.RecordCompleted(run, 1, Daily(), nextDay);
        Check(duplicate.PeriodKey == first.PeriodKey && duplicate.CompletedRounds == 1 && duplicate.NewSaleTaskId == null,
            "repeated completion across midnight remains the original event");
        var threshold = ledger.RecordCompleted(run, 2, Daily(), morning.AddMinutes(1));
        Check(threshold.CompletedRounds == 2 && threshold.NewSaleTaskId != null, "threshold creates one durable sale intent");
        Check(File.ReadAllText(path).Contains(threshold.NewSaleTaskId!, StringComparison.Ordinal), "intent exists on disk before caller can dispatch it");
        Check(ledger.State == LedgerState.Ready && ledger.HasUnresolvedTasks && ledger.GetRunnableTasks().Count == 1,
            "normal pending work is runnable, not a manual-recovery fault");
        var task = threshold.NewSaleTaskId!;
        ledger.UpdateSale(task, SaleTaskStatus.InProgress, "owned execution");
        var more = ledger.RecordCompleted(run, 3, Daily(), morning.AddMinutes(2));
        Check(more.CompletedRounds == 3 && more.NewSaleTaskId == null, "active sale does not block collecting another round");
        ledger.UpdateSale(task, SaleTaskStatus.Completed, "confirmed");
        var bytes = File.ReadAllText(path); ledger.UpdateSale(task, SaleTaskStatus.Completed, "duplicate callback");
        Check(bytes == File.ReadAllText(path), "duplicate terminal update has no second durable transition");
        Reject(() => ledger.UpdateSale(task, SaleTaskStatus.InProgress, "retry"), "completed sale cannot become runnable again");
        Reject(() => ledger.UpdateSale(task, SaleTaskStatus.Failed, "late old error"), "completed sale cannot regress");
        Reject(() => ledger.UpdateSale("missing", SaleTaskStatus.Completed, "unknown"), "unknown result cannot create a replacement task");
        Reject(() => ledger.UpdateSale(task, SaleTaskStatus.Pending, "retry"), "generic update cannot requeue");
        Reject(() => ledger.UpdateSale(task, SaleTaskStatus.Acknowledged, "bypass"), "generic update cannot acknowledge");
        Reject(() => ledger.AcknowledgeAfterManualCheck(task, "checked"), "completed task does not need manual acknowledgement");
        Check(ledger.RecordCompleted(run, 4, Daily(), morning.AddMinutes(3)).NewSaleTaskId == null, "more rounds do not get another same-period allowance");
        Check(ledger.GetRunnableTasks().Count == 0 && ledger.Tasks.Count == 1, "completed history retained outside runnable queue");
    }
    using(var ledger = new ScheduleLedger(path))
    {
        Check(ledger.State == LedgerState.Ready && ledger.Tasks.Single().Status == SaleTaskStatus.Completed, "completed state survives reload without attention");
        Check(ledger.GetPeriod(Daily(), morning).CompletedRounds == 4, "all completed events survive reload");
        Check(ledger.ValidatePolicy(Daily(), morning.AddDays(-1)) != null, "manual start preflight detects a clock earlier than durable progress");
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(), morning.AddDays(-1)), "clock rollback cannot create an earlier allowance");
        Check(ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(), nextDay).CompletedRounds == 1, "new day resets count while preserving history");
        var second = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(), nextDay.AddMinutes(1));
        Check(second.NewSaleTaskId != null && ledger.Tasks.Count == 2, "new period has its own single allowance");
    }
    using(var ledger = new ScheduleLedger(path))
    {
        Check(ledger.State == LedgerState.NeedsAttention && ledger.Tasks.Last().Status == SaleTaskStatus.NeedsAttention,
            "pending task on reload becomes attention instead of replay");
        Check(File.ReadAllText(path).Contains("NeedsAttention", StringComparison.Ordinal), "recovery classification itself is durable");
        Check(ledger.GetRunnableTasks().Count == 0, "recovery blocks all new automatic dispatch");
        var attention = ledger.Tasks.Last().TaskId;
        Reject(() => ledger.UpdateSale(attention, SaleTaskStatus.InProgress, "resume"), "reloaded request cannot resume through generic state update");
        Reject(() => ledger.AcknowledgeAfterManualCheck(attention, " "), "manual check requires an explicit explanation");
        ledger.AcknowledgeAfterManualCheck(attention, "inventory and gil checked manually");
        Check(ledger.State == LedgerState.Ready && ledger.Tasks.Last().Status == SaleTaskStatus.Acknowledged, "manual check only clears attention");
        var afterAck = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(), nextDay.AddMinutes(2));
        Check(afterAck.NewSaleTaskId == null && ledger.Tasks.Count == 2, "acknowledged period cannot receive a replacement sale");
    }

    path = NewPath();
    using(var ledger = new ScheduleLedger(path))
    {
        var taskA = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning).NewSaleTaskId!;
        ledger.UpdateSale(taskA, SaleTaskStatus.InProgress, "paused at character boundary");
        var taskB = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), nextDay).NewSaleTaskId!;
        Check(taskB != null && taskA != taskB && ledger.GetRunnableTasks().Select(x => x.TaskId).SequenceEqual(new[] { taskA, taskB }),
            "cross-day tasks queue serially behind the old in-progress task");
        ledger.UpdateSale(taskA, SaleTaskStatus.Failed, "B role failed after A sold five");
        Check(ledger.State == LedgerState.NeedsAttention && ledger.GetRunnableTasks().Count == 0, "partial failure blocks subsequent queued task");
        var blocked = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), nextDay.AddDays(1));
        Check(blocked.CompletedRounds == 1 && blocked.NewSaleTaskId == null && ledger.Tasks.Count == 2,
            "manual attention still records completed rounds without producing a new automatic task");
        ledger.AcknowledgeAfterManualCheck(taskA, "partial sale checked; do not retry A");
        Check(ledger.GetRunnableTasks().Single().TaskId == taskB, "acknowledgement does not replay the partially failed task");
    }

    path = NewPath();
    using(var ledger = new ScheduleLedger(path))
    {
        ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(5), morning);
        var changed = Daily(1);
        Check(ledger.ValidatePolicy(changed, morning) != null, "threshold edit cannot reset current policy");
        var display = ledger.GetPeriod(changed, morning);
        Check(display.PendingPolicy && display.CompletedRounds == 1 && display.PolicyIssue != null, "UI displays old active period and pending settings");
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, changed, morning.AddHours(1)), "new threshold rejected before old period ends");
        var weekly = Daily(5); weekly.Mode = PeriodMode.Weekly;
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, weekly, morning), "daily to weekly switch cannot gift a second in-period allowance");
        var changedZone = Daily(5); changedZone.TimeZoneId = "Pacific Standard Time";
        Check(ledger.ValidatePolicy(changedZone, morning) != null, "timezone change is a policy change");
        Check(ledger.ValidatePolicy(changed, nextDay) == null && !ledger.GetPeriod(changed, nextDay).PendingPolicy,
            "settings can take effect after the prior persisted period ends");
        Check(ledger.RecordCompleted(Guid.NewGuid(), 1, changed, nextDay).NewSaleTaskId != null, "new policy counts only newly completed rounds");
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(5), morning), "clock rollback cannot reopen a prior policy");
    }

    using(var ledger = new ScheduleLedger(NewPath()))
    {
        var weekly = Daily(); weekly.Mode = PeriodMode.Weekly;
        var sunday = ledger.GetPeriod(weekly, At("2026-11-01T23:59:59Z"));
        var monday = ledger.GetPeriod(weekly, At("2026-11-02T00:00:00Z"));
        Check(sunday.PeriodKey != monday.PeriodKey && sunday.EndsAt == monday.StartsAt, "weekly reset occurs exactly at configured Monday boundary");
        weekly.WeekStartsOn = DayOfWeek.Sunday; weekly.ResetMinuteOfDay = 4 * 60;
        var earlySunday = ledger.GetPeriod(weekly, At("2026-11-01T03:59:59Z"));
        var lateSunday = ledger.GetPeriod(weekly, At("2026-11-01T04:00:00Z"));
        Check(earlySunday.EndsAt == lateSunday.StartsAt && earlySunday.PeriodKey != lateSunday.PeriodKey, "custom weekly day and reset minute both apply");
        Check(ledger.GetPeriod(Daily(), At("2027-01-01T00:00:00Z")).StartsAt == At("2027-01-01T00:00:00Z"), "year boundary has no week-number collision");
        var pacific = Daily(); pacific.TimeZoneId = "Pacific Standard Time"; pacific.ResetMinuteOfDay = 90;
        var firstReset = ledger.GetPeriod(pacific, At("2026-11-01T08:45:00Z"));
        var repeatedHour = ledger.GetPeriod(pacific, At("2026-11-01T09:15:00Z"));
        Check(firstReset.PeriodKey == repeatedHour.PeriodKey && firstReset.StartsAt == At("2026-11-01T08:30:00Z"),
            "fall-back repeated hour does not reset a second time or roll into the previous period");
        var beforeReset = ledger.GetPeriod(pacific, At("2026-11-01T08:29:59Z"));
        Check(beforeReset.EndsAt == firstReset.StartsAt, "fall-back boundary is its first occurrence");
        pacific.ResetMinuteOfDay = 150;
        var spring = ledger.GetPeriod(pacific, At("2026-03-08T10:00:00Z"));
        Check(spring.StartsAt == At("2026-03-08T10:00:00Z"), "spring skipped reset moves to the first valid minute");
    }

    path = NewPath();
    var weeklyRun = Guid.NewGuid();
    var weeklySettings = Daily(2); weeklySettings.Mode = PeriodMode.Weekly;
    string inProgressId;
    using(var ledger = new ScheduleLedger(path))
    {
        ledger.RecordCompleted(weeklyRun, 1, weeklySettings, At("2026-11-01T20:00:00Z"));
        var oldWeek = ledger.RecordCompleted(weeklyRun, 2, weeklySettings, At("2026-11-01T23:59:59Z"));
        inProgressId = oldWeek.NewSaleTaskId!;
        ledger.UpdateSale(inProgressId, SaleTaskStatus.InProgress, "old week sale still active");
        var newWeek = ledger.RecordCompleted(weeklyRun, 3, weeklySettings, At("2026-11-02T00:00:00Z"));
        Check(newWeek.CompletedRounds == 1 && newWeek.NewSaleTaskId == null && newWeek.PeriodKey != oldWeek.PeriodKey,
            "week boundary resets new count while old-week in-progress intent retains its original period");
        Check(ledger.RecordCompleted(weeklyRun, 4, weeklySettings, At("2026-11-02T00:01:00Z")).NewSaleTaskId != null,
            "new week can queue exactly one task behind previous week's execution");
    }
    using(var ledger = new ScheduleLedger(path))
    {
        Check(ledger.Tasks.Count == 2 && ledger.Tasks.All(x => x.Status == SaleTaskStatus.NeedsAttention),
            "reload quarantines both in-progress and queued tasks rather than resuming either");
        ledger.AcknowledgeAfterManualCheck(inProgressId, "old week confirmed manually");
        Check(ledger.State == LedgerState.NeedsAttention && ledger.GetRunnableTasks().Count == 0,
            "acknowledging one task cannot clear another unresolved task");
    }

    path = NewPath();
    using(var first = new ScheduleLedger(path))
    {
        using var second = new ScheduleLedger(path);
        Check(second.State == LedgerState.Unavailable, "another ledger instance cannot use a stale independent writer");
        Reject(() => second.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning), "writer-lock failure cannot dispatch");
        Check(first.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning).NewSaleTaskId != null, "original writer remains operational");
    }
    using(var denyRecoveryWrite = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    using(var ledger = new ScheduleLedger(path))
    {
        Check(ledger.State == LedgerState.Unavailable, "recovery cannot proceed if quarantining the old intent could not be saved");
        Reject(() => ledger.GetRunnableTasks(), "failed durable recovery exposes no runnable task");
    }
    foreach(var invalid in new[] { "", "{}", "null", "{broken json" })
    {
        path = NewPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, invalid);
        using var ledger = new ScheduleLedger(path);
        Check(ledger.State == LedgerState.Unavailable && ledger.Issue != null, "corrupt or incomplete ledger is not a fresh allowance");
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning), "invalid history prohibits recording and dispatch");
        Check(ledger.Tasks.Count == 0, "invalid document does not expose partly loaded mutable task data");
    }
    path = NewPath();
    using(var ledger = new ScheduleLedger(path))
    {
        ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(), morning);
        ledger.RecordCompleted(Guid.NewGuid(), 2, Daily(), morning.AddMinutes(1));
    }
    File.Delete(path);
    using(var ledger = new ScheduleLedger(path))
        Check(ledger.State == LedgerState.Unavailable, "missing primary with backup cannot restore a previously available quota");

    path = NewPath();
    using(var ledger = new ScheduleLedger(path))
    {
        var original = ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(2), morning);
        using(var denyReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(2), morning.AddMinutes(1)), "failed atomic replacement never returns a trigger");
            Check(ledger.State == LedgerState.Unavailable && ledger.Tasks.Count == 0, "failed write disables ledger without publishing speculative task");
        }
        Check(JsonNode.Parse(File.ReadAllText(path))!["Periods"]![0]!["CompletedRounds"]!.GetValue<int>() == original.CompletedRounds,
            "failed replacement leaves the preceding durable count intact");
        Reject(() => ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(2), morning.AddMinutes(2)), "I/O failure is fail-closed for the remainder of this instance");
    }

    path = NewPath();
    using(var ledger = new ScheduleLedger(path)) ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning);
    var tampered = JsonNode.Parse(File.ReadAllText(path))!; tampered["Periods"]![0]!["CompletedRounds"] = 99;
    File.WriteAllText(path, tampered.ToJsonString());
    using(var ledger = new ScheduleLedger(path)) Check(ledger.State == LedgerState.Unavailable, "counter without matching event evidence cannot be loaded");
    foreach(var mutation in new Action<JsonNode>[] {
        node => node["Events"]!.AsArray().Add(node["Events"]![0]!.DeepClone()),
        node => node["Periods"]![0]!["Policy"]!.AsObject().Remove("RoundsBeforeSale"),
        node => node["Periods"]![0]!["EndsAt"] = "2026-12-31T00:00:00Z",
        node => node["Tasks"]![0]!["Status"] = "UnknownStatus",
        node => { node["ActivePolicy"] = null; node["PolicyUntilUtc"] = null; },
        node => node["UnexpectedField"] = true })
    {
        path = NewPath();
        using(var ledger = new ScheduleLedger(path)) ledger.RecordCompleted(Guid.NewGuid(), 1, Daily(1), morning);
        var node = JsonNode.Parse(File.ReadAllText(path))!; mutation(node); File.WriteAllText(path, node.ToJsonString());
        using var broken = new ScheduleLedger(path);
        Check(broken.State == LedgerState.Unavailable, "duplicate events, partial policies, changed boundaries and unknown schema fail closed");
    }
    path = NewPath();
    using(var ledger = new ScheduleLedger(path)) { }
    File.Delete(path);
    using(var ledger = new ScheduleLedger(path)) Check(ledger.State == LedgerState.Unavailable,
        "even an initially opened ledger is not silently recreated after its primary disappears");
    Console.WriteLine($"RetainerWorkshop.Ledger: {checks} checks passed.");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    var allowedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if(fullRoot.StartsWith(allowedTemp, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fullRoot).StartsWith("RetainerWorkshopLedgerTests-", StringComparison.Ordinal)
        && Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
}
