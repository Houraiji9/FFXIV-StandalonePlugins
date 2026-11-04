using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetainerWorkshop;

public enum PeriodMode { Daily, Weekly }
public enum SaleTaskStatus { Pending, InProgress, Completed, Failed, NeedsAttention, Acknowledged }
public enum LedgerState { Ready, NeedsAttention, Unavailable }

[Serializable]
public sealed class ScheduleSettings
{
    [JsonRequired] public bool Enabled { get; set; }
    [JsonRequired] public PeriodMode Mode { get; set; }
    [JsonRequired] public int RoundsBeforeSale { get; set; } = 5;
    [JsonRequired] public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    [JsonRequired] public int ResetMinuteOfDay { get; set; }
    [JsonRequired] public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Monday;

    public string? Validate()
    {
        if(!Enum.IsDefined(Mode) || !Enum.IsDefined(WeekStartsOn)) return "周期或每周起始日无效。";
        if(RoundsBeforeSale < 1) return "出售阈值必须是正整数轮次。";
        if(ResetMinuteOfDay is < 0 or >= 1440) return "周期重置分钟必须在 0–1439 之间。";
        if(string.IsNullOrWhiteSpace(TimeZoneId)) return "请选择有效时区。";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch(Exception ex) when(ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return "无法读取配置的时区；不会回退到本机时区。"; }
        return null;
    }
    public ScheduleSettings Freeze()
    {
        if(Validate() is { } issue) throw new ArgumentException(issue);
        return new() { Enabled = Enabled, Mode = Mode, RoundsBeforeSale = RoundsBeforeSale,
            TimeZoneId = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId).Id,
            ResetMinuteOfDay = ResetMinuteOfDay, WeekStartsOn = WeekStartsOn };
    }
}

public sealed record LedgerDecision(string PeriodKey, int CompletedRounds, string? NewSaleTaskId);
public sealed record PeriodSnapshot(string PeriodKey, int CompletedRounds, string? SaleTaskId,
    SaleTaskStatus? SaleStatus, string PolicyFingerprint, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    bool PendingPolicy, string? PolicyIssue);
public sealed record SaleTaskSnapshot(string TaskId, string PeriodKey, SaleTaskStatus Status,
    string Detail, Guid TriggerRunId, int TriggerCycle, DateTimeOffset CreatedAt);

/// <summary>
/// Durable at-most-once sale intent, not a resumable game queue. A new intent is returned only after
/// atomic persistence. Every writer holds an exclusive sidecar lock; corrupt/missing-after-backup
/// or failed writes require investigation and never fall back to an older, reusable allowance.
/// </summary>
public sealed class ScheduleLedger : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly object gate = new();
    private readonly string path;
    private FileStream? writerLock;
    private LedgerDocument document = LedgerDocument.Empty();
    private string? fault;
    private bool disposed;

    public ScheduleLedger(string path)
    {
        this.path = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            var previouslyOpened = File.Exists(this.path + ".lock");
            writerLock = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if(File.Exists(this.path))
            {
                var loaded = JsonSerializer.Deserialize<LedgerDocument>(File.ReadAllText(this.path), Json)
                    ?? throw new InvalidDataException("周期账本为空。");
                ValidateDocument(loaded); document = loaded;
            }
            else if(File.Exists(this.path + ".bak") || previouslyOpened)
                throw new InvalidDataException("主账本缺失但存在历史备份或先前打开记录；不能将旧资格当作全新周期。");
            else Commit(LedgerDocument.Empty()); // Even the first event subsequently has a recoverable prior file.

            if(document.Tasks.Any(x => x.Status is SaleTaskStatus.Pending or SaleTaskStatus.InProgress))
            {
                var recovered = Clone();
                foreach(var task in recovered.Tasks.Where(x => x.Status is SaleTaskStatus.Pending or SaleTaskStatus.InProgress))
                {
                    task.Status = SaleTaskStatus.NeedsAttention;
                    task.Detail = "插件重新加载；旧出售请求的结果尚未核实。不会自动重发，请人工检查后确认。";
                }
                Commit(recovered);
            }
        }
        catch(Exception ex) { fault = "周期账本不可用：" + ex.Message; }
    }

    public LedgerState State { get { lock(gate) return fault != null || disposed ? LedgerState.Unavailable
        : AttentionRequired(document) ? LedgerState.NeedsAttention : LedgerState.Ready; } }
    public string? Issue { get { lock(gate) return fault ?? (disposed ? "周期账本已关闭。" :
        AttentionRequired(document) ? "仍有未解决的出售任务；禁止生成新的自动出售任务。" : null); } }
    public bool HasUnresolvedTasks { get { lock(gate) return Unresolved(document); } }
    public IReadOnlyList<SaleTaskSnapshot> Tasks
    {
        get { lock(gate) return document.Tasks.Select(x => new SaleTaskSnapshot(x.Id, x.PeriodKey, x.Status,
            x.Detail, x.RunId, x.Cycle, x.CreatedAt)).ToArray(); }
    }
    public IReadOnlyList<SaleTaskSnapshot> GetRunnableTasks()
    {
        lock(gate)
        {
            EnsureAvailable();
            if(AttentionRequired(document)) return [];
            return Tasks.Where(x => x.Status is SaleTaskStatus.Pending or SaleTaskStatus.InProgress)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.TaskId, StringComparer.Ordinal).ToArray();
        }
    }

    public string? ValidatePolicy(ScheduleSettings settings, DateTimeOffset now)
    {
        lock(gate)
        {
            if(fault != null || disposed) return Issue;
            var requested = settings.Freeze();
            if(document.LastObservedUtc.HasValue && now < document.LastObservedUtc.Value)
                return "系统时间早于最近已持久化的完成事件；请核对系统时间后再开始。";
            return PolicyIssue(requested, now);
        }
    }

    // Read-only: display cannot advance time, reset a period, acknowledge or allocate an allowance.
    public PeriodSnapshot GetPeriod(ScheduleSettings settings, DateTimeOffset now)
    {
        lock(gate)
        {
            EnsureAvailable();
            var requested = settings.Freeze();
            var issue = PolicyIssue(requested, now);
            var effective = issue == null ? requested : document.ActivePolicy!;
            var period = Describe(effective, now);
            var row = document.Periods.FirstOrDefault(x => x.Key == period.Key);
            var task = row?.SaleTaskId is { } id ? document.Tasks.Single(x => x.Id == id) : null;
            return new(period.Key, row?.CompletedRounds ?? 0, task?.Id, task?.Status, period.Fingerprint,
                period.Start, period.End, issue != null, issue);
        }
    }

    public LedgerDecision RecordCompleted(Guid runId, int cycle, ScheduleSettings settings, DateTimeOffset now)
    {
        lock(gate)
        {
            EnsureAvailable();
            if(runId == Guid.Empty || cycle < 1) throw new ArgumentException("收取完成事件必须有有效运行编号及正整数轮次。");
            var requested = settings.Freeze();
            var previous = document.Events.FirstOrDefault(x => x.RunId == runId && x.Cycle == cycle);
            if(previous != null)
            {
                var oldPeriod = document.Periods.Single(x => x.Key == previous.PeriodKey);
                return new(oldPeriod.Key, oldPeriod.CompletedRounds, null);
            }
            if(PolicyIssue(requested, now) is { } policyIssue) throw new InvalidOperationException(policyIssue);
            var period = Describe(requested, now);
            if(!requested.Enabled)
                return new(period.Key, document.Periods.FirstOrDefault(x => x.Key == period.Key)?.CompletedRounds ?? 0, null);
            if(document.LastObservedUtc.HasValue && now < document.LastObservedUtc.Value)
                throw new InvalidOperationException("系统时间早于最近已持久化的完成事件；不会向回退周期记账或重发出售。");

            var next = Clone();
            var row = next.Periods.FirstOrDefault(x => x.Key == period.Key);
            if(row == null)
            {
                row = new() { Key = period.Key, Fingerprint = period.Fingerprint, Policy = requested.Freeze(),
                    StartsAt = period.Start, EndsAt = period.End, CompletedRounds = 0, SaleTaskId = null };
                next.Periods.Add(row);
            }
            row.CompletedRounds = checked(row.CompletedRounds + 1);
            next.Events.Add(new() { RunId = runId, Cycle = cycle, PeriodKey = row.Key });
            next.ActivePolicy = requested.Freeze();
            next.PolicyUntilUtc = period.End; next.LastObservedUtc = now.ToUniversalTime();
            string? created = null;
            if(row.SaleTaskId == null && row.CompletedRounds >= requested.RoundsBeforeSale && !AttentionRequired(next))
            {
                created = Guid.NewGuid().ToString("N"); row.SaleTaskId = created;
                next.Tasks.Add(new() { Id = created, PeriodKey = row.Key, Status = SaleTaskStatus.Pending,
                    Detail = "本周期出售资格已持久化占用，等待唯一调度器接管。", RunId = runId, Cycle = cycle, CreatedAt = now.ToUniversalTime() });
            }
            Commit(next);
            return new(row.Key, row.CompletedRounds, created);
        }
    }

    public void UpdateSale(string taskId, SaleTaskStatus state, string detail)
    {
        lock(gate)
        {
            EnsureAvailable();
            if(!Enum.IsDefined(state) || state is SaleTaskStatus.Pending or SaleTaskStatus.Acknowledged)
                throw new ArgumentException("不能重新排队或通过通用更新解除人工核对。");
            var next = Clone(); var task = FindTask(next, taskId);
            if(task.Status == state) return;
            if(task.Status is not (SaleTaskStatus.Pending or SaleTaskStatus.InProgress)
                || (task.Status == SaleTaskStatus.InProgress && state == SaleTaskStatus.InProgress))
                throw new InvalidOperationException("已结束或需要人工核对的出售任务不能重新执行或回退状态。");
            task.Status = state; task.Detail = detail ?? ""; Commit(next);
        }
    }

    public void AcknowledgeAfterManualCheck(string taskId, string detail)
    {
        lock(gate)
        {
            EnsureAvailable();
            if(string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("请填写人工核对说明。");
            var next = Clone(); var task = FindTask(next, taskId);
            if(task.Status == SaleTaskStatus.Acknowledged) return;
            if(task.Status is not (SaleTaskStatus.NeedsAttention or SaleTaskStatus.Failed))
                throw new InvalidOperationException("只能确认失败或重载后待核实的任务，不能绕过正在进行的出售。");
            task.Status = SaleTaskStatus.Acknowledged; task.Detail = detail;
            Commit(next); // Keep SaleTaskId, original event and the occupied period forever.
        }
    }

    private string? PolicyIssue(ScheduleSettings settings, DateTimeOffset now) =>
        document.ActivePolicy != null && Fingerprint(settings) != Fingerprint(document.ActivePolicy)
        && document.PolicyUntilUtc.HasValue && now < document.PolicyUntilUtc.Value
        ? $"当前周期仍沿用原策略；请在 {document.PolicyUntilUtc.Value:O} 之后开始使用新周期设置。不会因修改阈值或周期重新获得出售资格。" : null;
    private static bool Unresolved(LedgerDocument value) => value.Tasks.Any(x =>
        x.Status is SaleTaskStatus.Pending or SaleTaskStatus.InProgress or SaleTaskStatus.Failed or SaleTaskStatus.NeedsAttention);
    private static bool AttentionRequired(LedgerDocument value) => value.Tasks.Any(x =>
        x.Status is SaleTaskStatus.Failed or SaleTaskStatus.NeedsAttention);
    private static TaskRow FindTask(LedgerDocument value, string id) => value.Tasks.SingleOrDefault(x => x.Id == id)
        ?? throw new ArgumentException("找不到出售任务；不会创建替代任务。");
    private void EnsureAvailable()
    {
        if(disposed || fault != null || writerLock == null) throw new InvalidOperationException(Issue ?? "账本没有独占写入权限。");
    }
    private LedgerDocument Clone() => JsonSerializer.Deserialize<LedgerDocument>(JsonSerializer.Serialize(document, Json), Json)!;
    private void Commit(LedgerDocument next)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            next.Revision = checked(document.Revision + 1); ValidateDocument(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            using(var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if(File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path); // Never overwrite a concurrently introduced first ledger.
            document = next;
        }
        catch(Exception ex)
        {
            fault = "周期账本保存失败，禁止继续自动出售：" + ex.Message;
            throw new IOException(fault, ex);
        }
        finally
        {
            try { if(File.Exists(temporary)) File.Delete(temporary); } catch { /* Preserve failed-write evidence if locked. */ }
        }
    }

    private static string Fingerprint(ScheduleSettings settings)
    {
        var canonical = string.Join("|", "v1", (int)settings.Mode, settings.TimeZoneId,
            settings.ResetMinuteOfDay, (int)settings.WeekStartsOn, settings.RoundsBeforeSale);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    private sealed record PeriodDescription(string Key, string Fingerprint, DateTimeOffset Start, DateTimeOffset End);
    private static PeriodDescription Describe(ScheduleSettings settings, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        var start = Boundary(date, settings.ResetMinuteOfDay, zone);
        if(now < start) date = date.AddDays(-1);
        if(settings.Mode == PeriodMode.Weekly)
            date = date.AddDays(-(((int)date.DayOfWeek - (int)settings.WeekStartsOn + 7) % 7));
        start = Boundary(date, settings.ResetMinuteOfDay, zone);
        var end = Boundary(date.AddDays(settings.Mode == PeriodMode.Daily ? 1 : 7), settings.ResetMinuteOfDay, zone);
        var fingerprint = Fingerprint(settings);
        return new(fingerprint + ":" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), fingerprint, start, end);
    }
    private static DateTimeOffset Boundary(DateTime date, int minute, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.AddMinutes(minute), DateTimeKind.Unspecified);
        // A skipped wall-clock reset occurs at the first valid minute after the gap. A repeated
        // reset occurs at its first occurrence; comparing UTC boundaries avoids resetting twice.
        var attempts = 0;
        while(zone.IsInvalidTime(local) && attempts++ < 1440) local = local.AddMinutes(1);
        if(zone.IsInvalidTime(local)) throw new InvalidOperationException("无法确定该时区的周期边界。");
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void ValidateDocument(LedgerDocument value)
    {
        if(value.Version != 1 || value.Revision < 0 || value.Periods == null || value.Events == null || value.Tasks == null)
            throw new InvalidDataException("周期账本版本或结构无效。");
        if((value.ActivePolicy == null) != !value.PolicyUntilUtc.HasValue
            || (value.Events.Count > 0 && (!value.LastObservedUtc.HasValue || value.ActivePolicy == null)))
            throw new InvalidDataException("周期策略与时间记录不完整。");
        if(value.ActivePolicy?.Validate() is { } activeIssue) throw new InvalidDataException(activeIssue);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in value.Periods)
        {
            if(row == null || row.Policy == null || row.Policy.Validate() != null || row.CompletedRounds < 1
                || row.StartsAt >= row.EndsAt || !keys.Add(row.Key) || row.Fingerprint != Fingerprint(row.Policy)
                || !row.Key.StartsWith(row.Fingerprint + ":", StringComparison.Ordinal))
                throw new InvalidDataException("周期历史无效，不能重建出售资格。");
            var expected = Describe(row.Policy, row.StartsAt);
            if(row.Key != expected.Key || row.StartsAt != expected.Start || row.EndsAt != expected.End)
                throw new InvalidDataException("周期键或起止时间与冻结策略不符。");
        }
        var events = new HashSet<(Guid, int)>();
        foreach(var row in value.Events)
            if(row == null || row.RunId == Guid.Empty || row.Cycle < 1 || !keys.Contains(row.PeriodKey) || !events.Add((row.RunId, row.Cycle)))
                throw new InvalidDataException("完成事件缺失、重复或指向未知周期。");
        foreach(var period in value.Periods)
            if(value.Events.Count(x => x.PeriodKey == period.Key) != period.CompletedRounds)
                throw new InvalidDataException("完成次数与事件历史不一致。");
        if(value.ActivePolicy != null && (!value.LastObservedUtc.HasValue || !value.Periods.Any(x =>
            x.Fingerprint == Fingerprint(value.ActivePolicy) && x.EndsAt == value.PolicyUntilUtc
            && value.LastObservedUtc >= x.StartsAt && value.LastObservedUtc < x.EndsAt)))
            throw new InvalidDataException("最近策略、周期边界或完成时间不一致。");
        var ids = new HashSet<string>(StringComparer.Ordinal); var taskPeriods = new HashSet<string>(StringComparer.Ordinal);
        foreach(var task in value.Tasks)
        {
            if(task == null || !Guid.TryParseExact(task.Id, "N", out _) || !ids.Add(task.Id) || !taskPeriods.Add(task.PeriodKey)
                || !keys.Contains(task.PeriodKey) || !Enum.IsDefined(task.Status) || task.Detail == null
                || !value.Events.Any(x => x.RunId == task.RunId && x.Cycle == task.Cycle && x.PeriodKey == task.PeriodKey)
                || value.Periods.Single(x => x.Key == task.PeriodKey).SaleTaskId != task.Id
                || value.Periods.Single(x => x.Key == task.PeriodKey) is var period
                    && (period.CompletedRounds < period.Policy.RoundsBeforeSale
                        || task.CreatedAt < period.StartsAt || task.CreatedAt >= period.EndsAt))
                throw new InvalidDataException("出售任务与周期资格不一致。");
        }
        foreach(var period in value.Periods)
            if(period.SaleTaskId != null && !ids.Contains(period.SaleTaskId)) throw new InvalidDataException("已占用的出售资格缺少任务历史。");
    }

    public void Dispose()
    {
        lock(gate) { if(disposed) return; disposed = true; writerLock?.Dispose(); writerLock = null; }
    }

    // Explicit required fields prevent '{}' or partially truncated state from becoming a fresh allowance.
    private sealed class LedgerDocument
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public long Revision { get; set; }
        [JsonRequired] public ScheduleSettings? ActivePolicy { get; set; }
        [JsonRequired] public DateTimeOffset? PolicyUntilUtc { get; set; }
        [JsonRequired] public DateTimeOffset? LastObservedUtc { get; set; }
        [JsonRequired] public List<PeriodRow> Periods { get; set; } = [];
        [JsonRequired] public List<CompletionRow> Events { get; set; } = [];
        [JsonRequired] public List<TaskRow> Tasks { get; set; } = [];
        public static LedgerDocument Empty() => new() { Version = 1 };
    }
    private sealed class PeriodRow
    {
        [JsonRequired] public string Key { get; set; } = "";
        [JsonRequired] public string Fingerprint { get; set; } = "";
        [JsonRequired] public ScheduleSettings Policy { get; set; } = new();
        [JsonRequired] public DateTimeOffset StartsAt { get; set; }
        [JsonRequired] public DateTimeOffset EndsAt { get; set; }
        [JsonRequired] public int CompletedRounds { get; set; }
        [JsonRequired] public string? SaleTaskId { get; set; }
    }
    private sealed class CompletionRow
    {
        [JsonRequired] public Guid RunId { get; set; }
        [JsonRequired] public int Cycle { get; set; }
        [JsonRequired] public string PeriodKey { get; set; } = "";
    }
    private sealed class TaskRow
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string PeriodKey { get; set; } = "";
        [JsonRequired] public SaleTaskStatus Status { get; set; }
        [JsonRequired] public string Detail { get; set; } = "";
        [JsonRequired] public Guid RunId { get; set; }
        [JsonRequired] public int Cycle { get; set; }
        [JsonRequired] public DateTimeOffset CreatedAt { get; set; }
    }
}
