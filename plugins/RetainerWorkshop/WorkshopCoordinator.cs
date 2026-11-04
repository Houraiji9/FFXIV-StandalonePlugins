namespace RetainerWorkshop;

public enum WorkshopStage { Idle, Collecting, Selling, Crafting, Waiting, Stopping, Stopped, LimitReached, Failed }
public enum SaleBatchState { Idle, Running, Paused, Completed, Stopped, Failed }

internal interface IWorkshopRuntime
{
    bool Blocking { get; }
    bool SafeIdle { get; }
    bool FirstReady { get; }
    SessionStatus CollectionState { get; }
    SessionStatus CraftState { get; }
    SaleBatchState SaleState { get; }
    string Detail { get; }
    void BeginCollection();
    void TickCollection();
    void BeginCraft();
    void TickCraft();
    void StopCraft();
    void BeginSale(string taskId);
    void ResumeSale();
    void TickSale(bool pauseAfterCharacter);
    void StopAll();
    void Drain();
}

public sealed record WorkshopCheckpoint(Guid RunId, string Stage, int CompletedRounds, bool StopLatched,
    string? SaleTaskId, string[] PendingSaleTasks, string Detail, DateTimeOffset UpdatedUtc);

// Only this coordinator decides which engine may run. Engines never start another stage.
internal sealed class WorkshopCoordinator(IWorkshopRuntime runtime, ScheduleLedger ledger,
    Action<WorkshopCheckpoint> save, Func<DateTimeOffset> utcNow)
{
    private readonly Queue<string> sales = new();
    private ScheduleSettings schedule = new();
    private Guid run;
    private int cycle, limit;
    private bool craftEnabled, stopLatched, stopping, failed, craftStopRequested, manual, currentSaleStarted;
    private string? currentSale;
    public WorkshopStage Stage { get; private set; }
    public int CompletedRounds { get; private set; }
    public bool StopLatched => stopLatched;
    public bool Blocking => Stage is WorkshopStage.Collecting or WorkshopStage.Selling or WorkshopStage.Crafting
        or WorkshopStage.Waiting or WorkshopStage.Stopping || runtime.Blocking;
    public string Detail { get; private set; } = "尚未开始；加载不会自动运行。";
    public Guid RunId => run;

    public void Start(ScheduleSettings settings, int stopAfter, bool craft)
    {
        Initialize(settings, stopAfter, craft, false);
        try { BeginCollection(); }
        catch(Exception ex) { Fail("启动收取失败：" + ex.Message); }
    }
    public void StartManualSale()
    {
        Initialize(new ScheduleSettings { Enabled = false }, 0, false, true);
        try
        {
            currentSale = "manual-" + run.ToString("N");
            Stage = WorkshopStage.Selling; Detail = "手动出售，所选角色只处理一遍。";
            Persist();
            currentSaleStarted = true; runtime.BeginSale(currentSale);
        }
        catch(Exception ex) { Fail("启动出售失败：" + ex.Message); }
    }
    private void Initialize(ScheduleSettings settings, int stopAfter, bool craft, bool isManual)
    {
        if(Blocking) throw new InvalidOperationException("上一流程尚未结束或释放交互。");
        if(ledger.State != LedgerState.Ready) throw new InvalidOperationException(ledger.Issue ?? "周期记录需处理。");
        if(!runtime.SafeIdle) throw new InvalidOperationException("请先结束当前游戏交互与其他自动任务。");
        if(!isManual && ledger.ValidatePolicy(settings, utcNow()) is { } issue) throw new InvalidOperationException(issue);
        if(stopAfter < 0) throw new ArgumentOutOfRangeException(nameof(stopAfter));
        schedule = settings.Freeze(); limit = stopAfter; craftEnabled = craft; manual = isManual;
        run = Guid.NewGuid(); cycle = CompletedRounds = 0; stopLatched = stopping = failed = craftStopRequested = false;
        currentSale = null; currentSaleStarted = false; sales.Clear();
        Stage = WorkshopStage.Idle;
    }
    private void BeginCollection()
    {
        if(stopLatched || stopping) throw new InvalidOperationException("停机决定已生效，禁止开启下一轮。");
        cycle = checked(cycle + 1); Stage = WorkshopStage.Collecting;
        Detail = $"正在收取第 {cycle} 段；已确认完成 {CompletedRounds} 轮。";
        Persist(); runtime.BeginCollection();
    }
    public void Tick()
    {
        if(stopping) { Drain(); return; }
        if(!Blocking) return;
        try
        {
            switch(Stage)
            {
                case WorkshopStage.Collecting:
                    runtime.TickCollection();
                    if(runtime.CollectionState == SessionStatus.Failed) { Fail(runtime.Detail); return; }
                    if(runtime.CollectionState == SessionStatus.Stopped) { Fail("收取被提前停止，未计入完成轮次。"); return; }
                    if(runtime.CollectionState == SessionStatus.Completed && !runtime.Blocking && runtime.SafeIdle)
                    {
                        var decision = ledger.RecordCompleted(run, cycle, schedule, utcNow());
                        CompletedRounds = checked(CompletedRounds + 1);
                        stopLatched |= limit > 0 && CompletedRounds >= limit;
                        if(decision.NewSaleTaskId != null) sales.Enqueue(decision.NewSaleTaskId);
                        Stage = WorkshopStage.Waiting; Persist();
                        ChooseAfterCollection();
                    }
                    break;
                case WorkshopStage.Selling:
                    runtime.TickSale(!manual && !stopLatched && runtime.FirstReady);
                    if(runtime.SaleState == SaleBatchState.Failed) { Fail(runtime.Detail); return; }
                    if(runtime.SaleState == SaleBatchState.Stopped) { Fail("出售被提前停止；不自动重发。"); return; }
                    if(runtime.SaleState == SaleBatchState.Paused && !runtime.Blocking && runtime.SafeIdle)
                    { BeginCollection(); return; }
                    if(runtime.SaleState == SaleBatchState.Completed && !runtime.Blocking && runtime.SafeIdle)
                    {
                        if(!manual) ledger.UpdateSale(currentSale!, SaleTaskStatus.Completed, "所选角色出售已完成并释放交互。");
                        currentSale = null; currentSaleStarted = false;
                        Stage = WorkshopStage.Waiting; Persist();
                        if(manual) { Finish(WorkshopStage.Stopped, "手动出售完成，未开启收取或制作。"); return; }
                        ChooseAfterCollection();
                    }
                    break;
                case WorkshopStage.Crafting:
                    if(!craftStopRequested && runtime.FirstReady)
                    { craftStopRequested = true; runtime.StopCraft(); }
                    runtime.TickCraft();
                    if(runtime.CraftState == SessionStatus.Failed) { Fail(runtime.Detail); return; }
                    if(runtime.CraftState is SessionStatus.Completed or SessionStatus.Stopped && !runtime.Blocking && runtime.SafeIdle)
                    { Stage = WorkshopStage.Waiting; Detail = "制作时段已结束，等待开头角色到期。"; Persist(); }
                    break;
                case WorkshopStage.Waiting:
                    if(stopLatched) { Finish(WorkshopStage.LimitReached, "已达到本次运行轮次上限，轮换和自动制作保持关闭。"); return; }
                    if(runtime.FirstReady && runtime.SafeIdle) BeginCollection();
                    break;
            }
            if(Blocking && !stopping) Detail = $"已完成 {CompletedRounds} 轮 · {runtime.Detail}";
        }
        catch(Exception ex) { Fail("流程停止：" + ex.Message); }
    }
    private void ChooseAfterCollection()
    {
        if(currentSale == null && sales.TryDequeue(out var queued)) currentSale = queued;
        if(currentSale != null)
        {
            Stage = WorkshopStage.Selling; Detail = "执行本轮已触发的出售任务。"; Persist();
            if(currentSaleStarted) runtime.ResumeSale();
            else
            {
                ledger.UpdateSale(currentSale, SaleTaskStatus.InProgress, "开始本轮触发的出售，角色进度由单一会话保存。");
                currentSaleStarted = true; runtime.BeginSale(currentSale);
            }
            return;
        }
        if(stopLatched) { Finish(WorkshopStage.LimitReached, "已达到轮次上限，本轮出售已收尾；整体待机。"); return; }
        if(runtime.FirstReady) { BeginCollection(); return; }
        if(craftEnabled)
        {
            craftStopRequested = false; Stage = WorkshopStage.Crafting; Detail = "返回指定制作角色，开始本次制作时段。";
            Persist(); runtime.BeginCraft(); return;
        }
        Stage = WorkshopStage.Waiting; Detail = "等待开头角色下一次到期。"; Persist();
    }
    public void Stop() => BeginStop(false, "已手动停止；撤销后续动作，等待已发送操作收尾。");
    public void Fail(string detail) => BeginStop(true, detail);
    private void BeginStop(bool failure, string detail)
    {
        if(!Blocking && Stage is not WorkshopStage.Idle) return;
        failed |= failure; stopping = true; stopLatched = true; Detail = detail;
        Stage = WorkshopStage.Stopping;
        // Stop intent precedes storage: a full disk must never prevent cancellation.
        try { runtime.StopAll(); } catch(Exception ex) { failed = true; Detail += " 停止接口异常：" + ex.Message; }
        try
        {
            if(!manual)
            {
                if(currentSale != null) ledger.UpdateSale(currentSale, SaleTaskStatus.NeedsAttention, Detail);
                foreach(var queued in sales) ledger.UpdateSale(queued, SaleTaskStatus.NeedsAttention, "运行已停止，未自动执行。");
            }
            Persist();
        }
        catch(Exception ex) { failed = true; Detail += " 保存停止记录失败：" + ex.Message; }
    }
    private void Drain()
    {
        try
        {
            runtime.Drain();
            if(runtime.Blocking || !runtime.SafeIdle) return;
            Finish(failed ? WorkshopStage.Failed : WorkshopStage.Stopped,
                Detail + " 已释放交互，可检查后手动开始；不会自动恢复。");
        }
        catch(Exception ex) { failed = true; Detail = "收尾尚未核实：" + ex.Message; }
    }
    private void Finish(WorkshopStage state, string detail)
    {
        Stage = state; Detail = detail;
        // Only a committed terminal checkpoint permits the coordinator to leave drain.
        try { Persist(); stopping = false; }
        catch { Stage = WorkshopStage.Stopping; stopping = failed = true; throw; }
    }
    private void Persist() => save(new(run, Stage.ToString(), CompletedRounds, stopLatched, currentSale,
        sales.ToArray(), Detail, utcNow()));
}
