namespace RetainerVendorSweep;

public enum SweepState { Idle, Running, Stopping, Completed, Stopped, Failed }
public enum VendorCharacterStatus { Pending, Switching, Selling, Completed, Stopped, Failed }
public sealed record SweepResult(VendorCharacter Character, VendorCharacterStatus Status, long Count, long Gil, string Detail);

// One explicitly selected, frozen list per manual Start. This never discovers or claims all account characters.
public sealed class SweepMachine(ISweepHost host, IVendorSession session)
{
    private enum Phase { Idle, StartDelay, Select, RelogDelay, AwaitCharacter, Stable, Session, NextDelay, Drain }
    private Phase phase;
    private VendorCharacter[] characters = [];
    private readonly List<SweepResult> results = [];
    private SaleRules rules = new();
    private VendorDelays delays = new();
    private int index;
    private long due, deadline, lastCount, lastGil;
    private long? stableSince;
    private bool stopping, failed, switchRequested, sessionBegun, sessionAccepted, sessionActivityObserved, stopSent, stopTimedOut;
    public SweepState State { get; private set; }
    public bool Running => phase != Phase.Idle && !stopping && !failed;
    public bool Blocking => phase != Phase.Idle || session.Blocking;
    public string Detail { get; private set; } = "选择已知角色和出售规则后手动开始；不会自动恢复旧轮次。";
    public IReadOnlyList<SweepResult> Results => results.AsReadOnly();
    private VendorCharacter Current => characters[index];

    public void Start(IReadOnlyList<VendorCharacter> selected, SaleRules selectedRules, VendorDelays selectedDelays, long now)
    {
        if(Blocking) throw new InvalidOperationException("上一轮或其已发请求尚未清理。");
        if(selected == null || selected.Count == 0) throw new InvalidOperationException("请选择至少一个已知角色；不会自动枚举全账号。");
        if(selectedRules == null || selectedDelays == null) throw new ArgumentNullException(nameof(selectedRules));
        if(selectedRules.Validate() is { } invalid) throw new InvalidOperationException(invalid);
        var copy = selected.Select(x => x == null ? null : new VendorCharacter(x.Cid, x.Name?.Trim() ?? "", x.World?.Trim() ?? "")).ToArray();
        if(copy.Any(x => x == null || x.Cid == 0 || x.Name.Length == 0 || x.World.Length == 0))
            throw new InvalidOperationException("角色必须有已知 CID、姓名及世界。");
        if(copy.Select(x => x!.Cid).Distinct().Count() != copy.Length ||
           copy.Select(x => (x!.Name.ToUpperInvariant(), x.World.ToUpperInvariant())).Distinct().Count() != copy.Length)
            throw new InvalidOperationException("角色列表有重复 CID 或重复姓名／世界，请先核对。");
        if(!host.LoggedIn || host.CurrentCid == 0 || host.IsSwitching || !host.SafeForSwitch)
            throw new InvalidOperationException("请先登录并结束当前交互及换角。");
        if(host.Conflict() is { } conflict) throw new InvalidOperationException(conflict);
        characters = copy.Cast<VendorCharacter>().ToArray(); rules = selectedRules.Freeze(); delays = selectedDelays.Freeze();
        results.Clear(); results.AddRange(characters.Select(x => new SweepResult(x, VendorCharacterStatus.Pending, 0, 0, "本轮选定，尚未处理。")));
        index = 0; lastCount = lastGil = 0; stableSince = null;
        stopping = failed = switchRequested = sessionBegun = sessionAccepted = sessionActivityObserved = stopSent = stopTimedOut = false;
        State = SweepState.Running; phase = Phase.StartDelay;
        due = Add(now, delays.StartMs); deadline = Add(due, delays.TimeoutSeconds * 1000L);
        Detail = $"已冻结本轮选定的 {characters.Length} 个已知角色；等待启动延迟。";
    }

    public void Tick(long now)
    {
        if(phase == Phase.Idle) return;
        if(phase == Phase.Drain) { Drain(now); return; }
        try
        {
            if(host.Conflict() is { } conflict) { Fail(conflict, now); return; }
            switch(phase)
            {
                case Phase.StartDelay:
                case Phase.NextDelay:
                    if(now >= due) phase = Phase.Select;
                    break;
                case Phase.Select:
                    if(AtTarget()) EnterStable(now, false);
                    else if(host.SafeForSwitch && !host.IsSwitching && host.LoggedIn)
                    {
                        phase = Phase.RelogDelay; due = Add(now, delays.RelogMs);
                        deadline = Add(due, delays.TimeoutSeconds * 1000L);
                        Detail = $"等待换角延迟，本轮第 {index + 1}/{characters.Length} 个已知角色。";
                    }
                    else if(now > deadline) Fail("等待可安全换角状态超时，未发送换角请求。", now);
                    break;
                case Phase.RelogDelay:
                    if(AtTarget()) { EnterStable(now, false); break; }
                    if(now > deadline) { Fail("换角前等待界面释放超时，未重试。", now); break; }
                    if(now < due || host.IsSwitching || !host.SafeForSwitch || !host.LoggedIn) break;
                    // Mark ownership before calling: a throwing call may have already submitted an asynchronous request.
                    switchRequested = true; stableSince = null; phase = Phase.AwaitCharacter;
                    deadline = Add(now, delays.RelogTimeoutSeconds * 1000L + delays.LoginStableMs);
                    SetResult(VendorCharacterStatus.Switching, "已提交一次换角，等待实际目标 CID。");
                    Detail = $"正在等待本轮第 {index + 1}/{characters.Length} 个角色登录；不会重复提交换角。";
                    try { host.ChangeCharacter(Current); }
                    catch(CharacterSwitchRejectedException) { switchRequested = false; throw; }
                    break;
                case Phase.AwaitCharacter:
                    if(AtTarget()) EnterStable(now, true);
                    else if(now > deadline) Fail("换角回应超时，保留已发请求的收尾状态，不会重发或继续下一角色。", now);
                    break;
                case Phase.Stable:
                    if(!AtTarget()) stableSince = null;
                    else stableSince ??= now;
                    if(stableSince.HasValue && now - stableSince.Value >= delays.LoginStableMs)
                    {
                        switchRequested = false; stableSince = null;
                        // This public host handoff is done once per observed, stable character.
                        // If it fails, no native session has been submitted and there is nothing to drain there.
                        host.PrepareCharacter();
                        // Parameter preparation is not a submitted Begin and must not acquire the previous session.
                        var sessionRules = rules.Freeze(); var sessionDelays = delays.Freeze();
                        sessionBegun = true; sessionAccepted = sessionActivityObserved = false;
                        stopSent = false; lastCount = lastGil = 0;
                        phase = Phase.Session; deadline = Add(now, delays.TimeoutSeconds * 1000L);
                        SetResult(VendorCharacterStatus.Selling, "已提交一次本角色直售会话。");
                        Detail = $"正在处理本轮第 {index + 1}/{characters.Length} 个角色。";
                        try { session.Begin(Current.Cid, sessionRules, sessionDelays); }
                        catch(VendorBeginRejectedException) { sessionBegun = false; throw; }
                        // A normal return publishes this character's session, including a valid zero-item Completed.
                        sessionAccepted = true;
                    }
                    else if(now > deadline) Fail("目标角色登录或界面稳定等待超时。", now);
                    break;
                case Phase.Session:
                    if(!host.LoggedIn || host.CurrentCid != Current.Cid)
                    { Fail("出售期间角色身份改变，已停止后续角色。", now); break; }
                    session.Tick();
                    if(!CaptureCounters()) { Fail("出售会话的数量或金额出现无效、回退或溢出，已停止。", now); break; }
                    SetResult(VendorCharacterStatus.Selling, session.Detail);
                    Detail = $"本轮第 {index + 1}/{characters.Length} 个角色：{session.Detail}";
                    if(session.State == VendorSessionState.Failed) { Fail(session.Detail, now); break; }
                    if(session.State == VendorSessionState.Stopped) { Fail("出售会话提前停止：" + session.Detail, now); break; }
                    if(session.State == VendorSessionState.Completed && !session.Blocking)
                    {
                        SetResult(VendorCharacterStatus.Completed, session.Detail); sessionBegun = sessionAccepted = false;
                        index++; lastCount = lastGil = 0;
                        if(index == characters.Length)
                        { phase = Phase.Idle; State = SweepState.Completed; Detail = $"本轮选定的 {characters.Length} 个已知角色已处理完毕。"; break; }
                        phase = Phase.NextDelay; due = Add(now, delays.NextCharacterMs);
                        deadline = Add(due, delays.TimeoutSeconds * 1000L);
                    }
                    else if(session.State == VendorSessionState.Idle && now > deadline)
                        Fail("会话提交后没有取得开始回应，停止且不会重发。", now);
                    break;
            }
        }
        catch(VendorBeginRejectedException ex) { Fail(ex.Message, now); }
        catch(Exception ex) { Fail($"轮次操作异常（{ex.GetType().Name}），已停止后续动作。", now); }
    }

    public void Stop(long now)
    {
        if(phase == Phase.Idle || stopping) return;
        BeginStop(now);
    }

    private void Fail(string reason, long now)
    {
        if(!failed)
        {
            failed = true; Detail = reason;
            if(index < results.Count) SetResult(VendorCharacterStatus.Failed, reason);
        }
        BeginStop(now); State = SweepState.Failed;
    }

    private void BeginStop(long now)
    {
        if(stopping) return;
        stopping = true; phase = Phase.Drain; stableSince = null;
        deadline = Add(now, delays.RelogTimeoutSeconds * 1000L + delays.LoginStableMs);
        if(!failed) { State = SweepState.Stopping; Detail = "已撤销后续出售和换角，等待已发请求收尾。"; }
        if(sessionBegun) sessionActivityObserved |= session.Blocking || session.State == VendorSessionState.Running;
        if(sessionAccepted && sessionBegun && !session.Blocking && session.State is VendorSessionState.Completed or VendorSessionState.Stopped or VendorSessionState.Failed)
        {
            // A terminal, released session already finished cleanup. Do not turn it back into Stopping.
            if(!CaptureCounters()) { failed = true; State = SweepState.Failed; Detail = "已结束会话的数量或金额无法核实。"; }
            if(session.State == VendorSessionState.Failed) { failed = true; State = SweepState.Failed; Detail = session.Detail; }
            SetResult(failed ? VendorCharacterStatus.Failed : VendorCharacterStatus.Stopped, session.Detail);
            sessionBegun = sessionAccepted = false;
        }
        if(sessionBegun && !stopSent)
        {
            stopSent = true;
            try { session.Stop(); }
            catch(Exception ex) { failed = true; State = SweepState.Failed; Detail = $"停止会话异常（{ex.GetType().Name}），保留收尾阻塞。"; }
        }
        if(!sessionBegun && !switchRequested) FinishStop();
    }

    private void Drain(long now)
    {
        try
        {
            if(sessionBegun)
            {
                // A terminal value left over after a throwing Begin is not evidence that this attempt ended.
                // Observe activity before Stop/Tick can merely rewrite an old terminal state.
                sessionActivityObserved |= session.Blocking || session.State == VendorSessionState.Running;
                session.Tick();
                if(sessionAccepted && !CaptureCounters())
                { failed = true; State = SweepState.Failed; Detail = "收尾数量或金额无法核实，未将异常数值计入结果。"; }
                if(session.State == VendorSessionState.Failed)
                { failed = true; State = SweepState.Failed; if(sessionAccepted) Detail = session.Detail; }
                if((sessionAccepted || sessionActivityObserved) && !session.Blocking && session.State is VendorSessionState.Completed or VendorSessionState.Stopped or VendorSessionState.Failed)
                {
                    SetResult(failed ? VendorCharacterStatus.Failed : VendorCharacterStatus.Stopped, sessionAccepted ? session.Detail : Detail);
                    sessionBegun = sessionAccepted = false;
                }
            }
            if(switchRequested)
            {
                if(AtTarget()) stableSince ??= now;
                else stableSince = null;
                if(stableSince.HasValue && now - stableSince.Value >= delays.LoginStableMs)
                    switchRequested = false;
            }
            if(!sessionBegun && !switchRequested) { FinishStop(); return; }
            if(now > deadline && !stopTimedOut)
            {
                stopTimedOut = failed = true; State = SweepState.Failed;
                Detail = "停止收尾超时；已发请求尚未核实结束，继续保留阻塞且不会自动重启。";
                if(index < results.Count) SetResult(VendorCharacterStatus.Failed, Detail);
            }
        }
        catch(Exception ex)
        { failed = true; State = SweepState.Failed; Detail = $"收尾观察异常（{ex.GetType().Name}），保留阻塞。"; }
    }

    private void FinishStop()
    {
        for(var i = index; i < results.Count; i++)
        {
            if(results[i].Status is VendorCharacterStatus.Completed or VendorCharacterStatus.Failed or VendorCharacterStatus.Stopped) continue;
            results[i] = results[i] with { Status = VendorCharacterStatus.Stopped, Detail = "本轮已停止；没有继续该角色的动作。" };
        }
        phase = Phase.Idle;
        State = failed ? SweepState.Failed : SweepState.Stopped;
        if(!failed) Detail = "本轮已停止，已发请求完成收尾；未处理角色不会继续。";
    }

    private void EnterStable(long now, bool preserveRelogDeadline)
    {
        phase = Phase.Stable; stableSince = now;
        if(!preserveRelogDeadline) deadline = Add(now, delays.TimeoutSeconds * 1000L + delays.LoginStableMs);
        Detail = $"核对本轮第 {index + 1}/{characters.Length} 个角色的 CID，并等待登录稳定。";
    }
    private bool AtTarget() => host.LoggedIn && host.CurrentCid == Current.Cid && !host.IsSwitching && host.SafeForSwitch;
    private bool CaptureCounters()
    {
        if(!sessionAccepted) return false;
        var count = session.SoldItems; var gil = session.Gil;
        if(count < lastCount || gil < lastGil || count < 0 || gil < 0) return false;
        try
        {
            long totalCount = count, totalGil = gil;
            for(var i = 0; i < index; i++)
            { totalCount = checked(totalCount + results[i].Count); totalGil = checked(totalGil + results[i].Gil); }
        }
        catch(OverflowException) { return false; }
        lastCount = count; lastGil = gil; return true;
    }
    private void SetResult(VendorCharacterStatus status, string detail) =>
        results[index] = new(Current, status, lastCount, lastGil, detail);
    private static long Add(long now, long milliseconds) => now > long.MaxValue - milliseconds ? long.MaxValue : now + milliseconds;
}
