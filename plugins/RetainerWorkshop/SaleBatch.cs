using RetainerVendorSweep;

namespace RetainerWorkshop;

// The verified single-role SweepMachine owns each dispatch. Resume retains the next role cursor.
internal sealed class SaleBatch(ISweepHost host, IVendorSession session, Action<string> persistProgress)
{
    private readonly List<SweepResult> results = [];
    private VendorCharacter[] characters = [];
    private SaleRules rules = new();
    private VendorDelays delays = new();
    private SweepMachine? current;
    private int index, sliceCompleted;
    private long nextAt;
    private string taskId = "";
    public SaleBatchState State { get; private set; }
    public bool Blocking => State == SaleBatchState.Running || current?.Blocking == true || session.Blocking;
    public string Detail { get; private set; } = "未开始出售。";
    public IReadOnlyList<SweepResult> Results => results.Concat(current?.Results ?? []).ToArray();

    public void Begin(string id, IReadOnlyList<VendorCharacter> selected, SaleRules selectedRules, VendorDelays selectedDelays, long now)
    {
        if(Blocking || State == SaleBatchState.Paused) throw new InvalidOperationException("上一出售任务尚未结束。");
        if(selected.Count == 0 || selected.Any(x => x.Cid == 0) || selected.Select(x => x.Cid).Distinct().Count() != selected.Count)
            throw new InvalidOperationException("请选择不重复的有效出售角色。");
        if(selected.Select(x => (x.Name.ToUpperInvariant(), x.World.ToUpperInvariant())).Distinct().Count() != selected.Count)
            throw new InvalidOperationException("出售角色存在同名同世界的重复身份。");
        if(selectedRules.Validate() is { } issue) throw new InvalidOperationException(issue);
        taskId = id; characters = selected.ToArray(); rules = selectedRules.Freeze(); delays = selectedDelays.Freeze();
        index = sliceCompleted = 0; results.Clear(); current = null; nextAt = now + delays.StartMs;
        Detail = "已冻结本次出售角色与规则。"; State = SaleBatchState.Running;
        persistProgress(Progress());
    }
    public void Resume(long now)
    {
        if(State != SaleBatchState.Paused || current?.Blocking == true || session.Blocking)
            throw new InvalidOperationException("出售任务尚未安全暂停。");
        State = SaleBatchState.Running; sliceCompleted = 0; nextAt = now + delays.NextCharacterMs;
        Detail = "从尚未处理的角色继续同一出售任务。"; persistProgress(Progress());
    }
    public void Tick(long now, bool pauseAfterCharacter)
    {
        if(State != SaleBatchState.Running)
        {
            if(current?.Blocking == true) { current.Tick(now); Detail = current.Detail; }
            return;
        }
        try
        {
            if(current != null)
            {
                current.Tick(now); Detail = current.Detail;
                if(current.State is SweepState.Failed or SweepState.Stopped)
                {
                    State = current.State == SweepState.Failed ? SaleBatchState.Failed : SaleBatchState.Stopped;
                    Detail = current.Detail; persistProgress(Progress()); return;
                }
                if(current.State != SweepState.Completed || current.Blocking) return;
                results.AddRange(current.Results); index++; sliceCompleted++; current = null;
                nextAt = now + delays.NextCharacterMs;
                persistProgress(Progress()); // Save completed identity before any next-role dispatch.
            }
            if(index == characters.Length)
            { State = SaleBatchState.Completed; Detail = $"本次 {characters.Length} 个选定角色出售已完成。"; persistProgress(Progress()); return; }
            if(pauseAfterCharacter && sliceCompleted > 0)
            { State = SaleBatchState.Paused; Detail = "当前角色出售已收尾，保留下一角色进度并返回收取。"; persistProgress(Progress()); return; }
            if(now < nextAt) return;
            // Intent is durably marked before handing an identity to the game-facing queue.
            persistProgress(Progress() + " 将处理下一角色；若此后中断，须人工核对。");
            current = new(host, session);
            current.Start([characters[index]], rules, delays, now);
        }
        catch(Exception ex)
        {
            State = SaleBatchState.Failed; Detail = "出售任务停止：" + ex.Message;
            current?.Stop(now); // Do not let a progress-write failure leave a live queue dispatching.
        }
    }
    public void Stop(long now)
    {
        if(State is SaleBatchState.Idle or SaleBatchState.Completed && !Blocking) return;
        if(State != SaleBatchState.Failed) State = SaleBatchState.Stopped;
        current?.Stop(now);
        Detail = "已停止出售任务，等待已发请求；不继续下一角色。";
    }
    private string Progress() => $"出售任务 {taskId}；已处理 {index}/{characters.Length}；状态 {State}；" +
        string.Join("；", Results.Select(x => $"{x.Character.Cid}:{x.Status}:{x.Count}:{x.Gil}"));
}
