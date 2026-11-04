using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

// One business boundary, not a claim that AR visited every registered character.
internal sealed class CollectionSession(IBridgeHost host)
{
    private enum Phase { Idle, Start, RelogDelay, Travelling, EnableDelay, Retainers, Drain, Cleanup }
    private Phase phase;
    private CyclePlan plan = new(new(0, "", ""), new(0, "", ""));
    private VendorDelays delays = new();
    private IReadOnlyList<TargetRetainer> targets = [];
    private readonly HashSet<string> collected = [];
    private string? requestedRetainer, heldRetainer;
    private bool characterRequested, characterHeld, armed, ownsAr, ownsNavigation, firstSwitchPending;
    private long due, deadline, collectionAt, heldAt, controlDue;
    private long? stableAt;
    public SessionStatus State { get; private set; }
    public bool Blocking => phase != Phase.Idle;
    public string Detail { get; private set; } = "尚未开始收取。";
    public string Owner { get; private set; } = "";
    public int Successful => collected.Count;
    public int Total => targets.Count;
    private bool AtEnd => host.LoggedIn && host.CurrentCid == plan.Crafter.Cid;
    private bool Succeeded => targets.Count > 0 && targets.All(x => collected.Contains(x.Name));

    public void Begin(CyclePlan firstAndEnd, VendorDelays settings)
    {
        if(Blocking) throw new InvalidOperationException("上一收取会话尚未收尾。");
        State = SessionStatus.Idle; Detail = "正在检查本次收取。"; targets = []; collected.Clear();
        if(firstAndEnd.First.Cid == 0 || firstAndEnd.Crafter.Cid == 0) throw new ArgumentException("请选择开头及收尾角色。");
        if(!host.LoggedIn || !host.UiReleased || host.ArBusy || host.NavigationBusy || host.ArMultiEnabled || !host.ArtisanIdle)
            throw new InvalidOperationException("当前自动流程或交互尚未释放，未启动收取。");
        plan = firstAndEnd; delays = settings.Freeze();
        Owner = "RetainerWorkshop_" + Guid.NewGuid().ToString("N");
        requestedRetainer = heldRetainer = null;
        characterRequested = characterHeld = armed = ownsAr = ownsNavigation = firstSwitchPending = false;
        State = SessionStatus.Running; Wait(Phase.Start, delays.StartMs);
        Detail = "等待启动延迟；从开头角色启用 AR，顺序仍由 AR 决定。";
    }

    public void Tick()
    {
        if(!Blocking) return;
        try
        {
            if(phase == Phase.Cleanup) { Cleanup(); return; }
            if(phase != Phase.Retainers && host.ArMultiEnabled) { Fail("交接期间 AR 被外部重新开启。"); return; }
            switch(phase)
            {
                case Phase.Start:
                    if(TimedOut("等待启动条件超时。") || host.Now < due || !Idle()) break;
                    if(host.CurrentCid == plan.First.Cid) Wait(Phase.EnableDelay, delays.LoginStableMs);
                    else Wait(Phase.RelogDelay, delays.RelogMs);
                    break;
                case Phase.RelogDelay:
                    if(TimedOut("换角前等待空闲超时。") || host.Now < due || !Idle()) break;
                    if(host.CurrentCid == plan.First.Cid) { Wait(Phase.EnableDelay, delays.LoginStableMs); break; }
                    ownsNavigation = firstSwitchPending = true; phase = Phase.Travelling;
                    deadline = host.Now + delays.RelogTimeoutSeconds * 1000L + delays.LoginStableMs;
                    try { host.Relog(plan.First.Name, plan.First.World); }
                    catch(CharacterSwitchRejectedException) { firstSwitchPending = ownsNavigation = false; throw; }
                    Detail = "已提交一次开头角色换角，等待目标 CID；不会重试。";
                    break;
                case Phase.Travelling:
                    if(TimedOut("未在时限内确认开头角色；已停止换角队列，保留在途请求收尾。")) break;
                    if(!host.LoggedIn || host.CurrentCid != plan.First.Cid || !Idle()) { stableAt = null; break; }
                    stableAt ??= host.Now;
                    if(host.Now - stableAt.Value < delays.LoginStableMs) break;
                    firstSwitchPending = ownsNavigation = false;
                    Wait(Phase.EnableDelay, delays.NextCharacterMs);
                    break;
                case Phase.EnableDelay:
                    if(TimedOut("开头角色准备超时。")) break;
                    if(!host.LoggedIn || host.CurrentCid != plan.First.Cid) { Fail("启用前离开了开头角色。"); break; }
                    if(!Idle()) { due = host.Now + delays.LoginStableMs; break; }
                    if(host.Now < due) break;
                    ownsAr = ownsNavigation = true; phase = Phase.Retainers; collectionAt = host.Now;
                    ArmVisit(); host.EnableMultiMode();
                    Detail = "AR 原生轮换中；仅在收尾角色收取证据齐全后完成本次业务轮次。";
                    break;
                case Phase.Retainers: Collect(); break;
                case Phase.Drain:
                    if(!AtEnd) { Fail("收尾确认期间离开指定收尾角色，未记完成。"); break; }
                    if(TimedOut("收尾交互未退出，未记完成。")) break;
                    if(!StableIdle(Math.Max(1000, delays.CloseMs))) break;
                    ownsAr = ownsNavigation = false; phase = Phase.Idle; State = SessionStatus.Completed;
                    Detail = "收尾角色的目标雇员已确认收取，AR 与界面已释放；本次业务轮次完成。";
                    break;
            }
        }
        catch(Exception ex)
        {
            if(phase == Phase.Cleanup) { stableAt = null; Detail = "收取收尾仍受阻：" + ex.Message; }
            else Fail("收取接口或状态检查失败：" + ex.Message);
        }
    }

    private void ArmVisit()
    {
        if(armed || !AtEnd) return;
        var next = host.ReadVisitTargets(plan.Crafter.Cid).ToArray();
        if(next.Length == 0) return;
        if(next.Any(x => string.IsNullOrWhiteSpace(x.Name)) || next.Select(x => x.Name).Distinct().Count() != next.Length)
            throw new InvalidOperationException("收尾雇员名称无法唯一匹配。");
        targets = next; collected.Clear(); armed = true;
    }
    private void Collect()
    {
        if(armed && !AtEnd) { Fail("收尾角色在成功确认前离开，未记完成。"); return; }
        ArmVisit();
        if(host.Now - collectionAt > 7_200_000) { Fail("两小时内未到达收尾收取边界。"); return; }
        if(heldRetainer != null)
        {
            var old = targets.Single(x => x.Name == heldRetainer);
            var fresh = host.ReadCharacter(plan.Crafter.Cid)?.RetainerData.SingleOrDefault(x => x.Name == heldRetainer);
            if(fresh != null && (!fresh.HasVenture || fresh.VentureID == 0 || fresh.VentureEndsAt > old.VentureEndsAt))
            { collected.Add(heldRetainer); ReleaseRetainer(); }
            else if(host.Now - heldAt > 15000) Fail("雇员后处理缺少对应探险状态更新，未记成功。");
            return;
        }
        if(armed)
        {
            var data = host.ReadCharacter(plan.Crafter.Cid);
            if(data is not { Enabled: true, ExcludeRetainer: false } || (!Succeeded && (data.Ventures < 2 || data.InventorySpace < 2)))
            { Fail("收尾角色的启用、探险许可或空间条件不满足，未记完成。"); return; }
            if(Succeeded && host.UiReleased && !host.ArBusy && !host.NavigationBusy) { StopAtBoundary(); return; }
        }
        if(!host.ArMultiEnabled) Fail("AR 多角色模式被停止，收取会话停止。");
    }
    public void OnRetainerAdditional(string name)
    {
        if(phase != Phase.Retainers || !AtEnd) return;
        try
        {
            ArmVisit();
            if(!armed || requestedRetainer != null || heldRetainer != null || collected.Contains(name) || !targets.Any(x => x.Name == name)) return;
            requestedRetainer = name; host.RequestRetainer(Owner);
        }
        catch(Exception ex) { Fail("请求雇员后处理失败：" + ex.Message); }
    }
    public void OnRetainerReady(string owner, string name)
    {
        if(owner != Owner || requestedRetainer != name) return;
        requestedRetainer = null; heldRetainer = name;
        if(phase == Phase.Cleanup || !Blocking) { Try(ReleaseRetainer); return; }
        if(phase != Phase.Retainers || !AtEnd) { Fail("雇员后处理发生在非收尾角色。"); return; }
        heldAt = host.Now;
    }
    public void OnCharacterAdditional()
    {
        if(phase != Phase.Retainers || !AtEnd || !armed || characterRequested || characterHeld) return;
        characterRequested = true;
        try { host.RequestCharacter(Owner); } catch(Exception ex) { Fail("请求角色后处理失败：" + ex.Message); }
    }
    public void OnCharacterReady(string owner)
    {
        if(owner != Owner || !characterRequested) return;
        characterRequested = false; characterHeld = true;
        if(phase == Phase.Cleanup || !Blocking) { Try(ReleaseCharacter); return; }
        try
        {
            // Same-frame handshake: AR has already queued the next relog. Do not delay this sequence.
            host.DisableArFunctions(); host.AbortArTasks(); ReleaseCharacter();
            if(phase != Phase.Retainers || !AtEnd || !Succeeded) { Fail("收尾角色仍缺少成功收取证据，未记完成。"); return; }
            EnterDrain();
        }
        catch(Exception ex) { Fail("无法截停下一次换角：" + ex.Message); }
    }
    private void StopAtBoundary() { host.DisableArFunctions(); host.AbortArTasks(); EnterDrain(); }
    private void EnterDrain()
    {
        ownsNavigation = false; phase = Phase.Drain; stableAt = null;
        deadline = host.Now + delays.TimeoutSeconds * 1000L + Math.Max(1000, delays.CloseMs);
        Detail = "收尾雇员已核实，等待 AR 队列及界面释放后记完成。";
    }
    public void Stop() { if(Blocking && phase != Phase.Cleanup) End(SessionStatus.Stopped, "已停止后续收取，等待自有请求收尾。"); }
    public void Fail(string reason) { if(Blocking && phase != Phase.Cleanup) End(SessionStatus.Failed, reason); }
    private void End(SessionStatus result, string detail)
    {
        State = result; Detail = detail; phase = Phase.Cleanup; stableAt = null;
        if(ownsAr) { Try(host.DisableArFunctions); Try(host.AbortArTasks); }
        controlDue = host.Now + delays.CloseMs;
        Try(ReleaseRetainer); Try(ReleaseCharacter);
        if(ownsNavigation) Try(host.AbortNavigation);
    }
    private void Cleanup()
    {
        if(ownsAr && (host.ArMultiEnabled || host.ArBusy))
        {
            stableAt = null;
            if(host.Now < controlDue) return;
            host.DisableArFunctions(); host.AbortArTasks(); controlDue = host.Now + delays.CloseMs;
        }
        ReleaseRetainer(); ReleaseCharacter();
        // Abort cannot undo a logout/login already submitted to the game.
        if(firstSwitchPending)
        {
            if(!host.LoggedIn || host.CurrentCid != plan.First.Cid) { stableAt = null; return; }
            if(!StableIdle(delays.LoginStableMs)) return;
            firstSwitchPending = false; stableAt = null;
        }
        if(!StableIdle(2000)) return;
        phase = Phase.Idle; ownsAr = ownsNavigation = false;
        Detail += " 自有队列与交互已释放，未自动重新开启 AR。";
    }
    public string? DisposeStop()
    {
        if(!Blocking) return null;
        Stop();
        if(ownsAr) { Try(host.DisableArFunctions); Try(host.AbortArTasks); }
        Try(ReleaseRetainer); Try(ReleaseCharacter);
        if(ownsNavigation) Try(host.AbortNavigation);
        return "收取会话卸载：已撤销后续调度；已提交的登录、传送或界面请求可能仍需手动处理。";
    }
    private void ReleaseRetainer() { if(heldRetainer == null) return; host.FinishRetainer(); heldRetainer = null; }
    private void ReleaseCharacter() { if(!characterHeld) return; host.FinishCharacter(); characterHeld = false; }
    private bool Idle() => host.LoggedIn && host.UiReleased && !host.ArBusy && !host.NavigationBusy && host.ArtisanIdle;
    private bool StableIdle(int ms)
    {
        if(!Idle()) { stableAt = null; return false; }
        stableAt ??= host.Now; return host.Now - stableAt.Value >= ms;
    }
    private void Wait(Phase next, int ms) { phase = next; stableAt = null; due = host.Now + ms; deadline = due + delays.TimeoutSeconds * 1000L; }
    private bool TimedOut(string text) { if(host.Now <= deadline) return false; Fail(text); return true; }
    private void Try(Action action) { try { action(); } catch(Exception ex) { Detail += " 清理异常：" + ex.Message; } }
}
