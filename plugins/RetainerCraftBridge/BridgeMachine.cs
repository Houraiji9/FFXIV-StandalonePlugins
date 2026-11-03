namespace RetainerCraftBridge;

/// <summary>AR owns native rotation. Intercept only the configured character's collection boundary.</summary>
internal sealed class BridgeMachine(IBridgeHost host, IMaterialPreparation? materials = null)
{
    private CyclePlan plan = new(new(0, "", ""), new(0, "", ""));
    private IReadOnlyList<TargetRetainer> targets = [];
    private readonly HashSet<string> collected = [];
    private ushort recipe;
    private int count;
    private string? requestedRetainer, heldRetainer;
    private bool characterRequested, characterHeld, visitArmed, ownsNavigation;
    private bool ownsCraft, craftObserved, craftAccepted, stopIssued, stopBoundarySeen, closeIssued, returnWanted, cleanup;
    private bool prepareMaterials, ownsMaterials;
    private long phaseAt, stableAt, heldAt, craftAt;

    public BridgeState State { get; private set; } = BridgeState.Idle;
    public string Detail { get; private set; } = "尚未开始。";
    public string Owner { get; private set; } = "";
    public int Cycle { get; private set; }
    public bool Running => State is not (BridgeState.Idle or BridgeState.Failed or BridgeState.Cancelled);
    public bool Blocking => Running || cleanup;
    public int Successful => collected.Count;
    public int Total => targets.Count;
    private bool TargetSucceeded => targets.Count > 0 && targets.All(x => collected.Contains(x.Name));

    public void Start(CyclePlan settings, ushort recipeId, int craftCount, bool restock = false)
    {
        if(Blocking) throw new InvalidOperationException("上一次循环仍在运行或清理。");
        if(settings.First.Cid == 0 || settings.Crafter.Cid == 0 || recipeId == 0 || craftCount is < 1 or > 9999)
            throw new ArgumentException("请选择开头角色、制作角色、配方及有效次数。");
        if(restock && materials == null) throw new InvalidOperationException("自动取料服务不可用。");
        plan = settings; recipe = recipeId; count = craftCount; prepareMaterials = restock; ownsMaterials = false;
        Owner = "RetainerCraftBridge_" + Guid.NewGuid().ToString("N");
        requestedRetainer = heldRetainer = null;
        characterRequested = characterHeld = ownsNavigation = ownsCraft = cleanup = false;
        Cycle = 0;
        StartCycle();
    }

    private void StartCycle()
    {
        targets = []; collected.Clear(); visitArmed = false;
        craftObserved = craftAccepted = stopIssued = stopBoundarySeen = closeIssued = returnWanted = false;
        State = BridgeState.StartingCycle; phaseAt = host.Now; stableAt = 0;
        Detail = "准备从设置的开头角色启用 AutoRetainer；其余角色顺序由 AutoRetainer 决定。";
    }

    public void Tick()
    {
        try
        {
            if(cleanup) { CleanupTick(); return; }
            if(!Running) return;
            if(State != BridgeState.Retainers && host.ArMultiEnabled)
            { Fail("制作交接期间 AutoRetainer 被重新开启；已停止，避免同时操作。"); return; }
            switch(State)
            {
                case BridgeState.StartingCycle:
                    if(host.Now - phaseAt > 180000) { Fail("开头角色准备超时。"); break; }
                    if(!host.UiReleased || host.ArBusy || host.NavigationBusy || !host.ArtisanIdle) break;
                    if(host.CurrentCid != plan.First.Cid)
                    {
                        State = BridgeState.TravellingToFirst; phaseAt = host.Now; ownsNavigation = true;
                        host.Relog(plan.First.Name, plan.First.World);
                        Detail = "正在切换到开头角色。";
                    }
                    else EnableNativeRotation();
                    break;
                case BridgeState.TravellingToFirst:
                    if(host.Now - phaseAt > 180000) { Fail("3 分钟内没有到达开头角色，不会重试换角。"); break; }
                    if(host.LoggedIn && host.CurrentCid == plan.First.Cid && host.UiReleased && !host.ArBusy && !host.NavigationBusy)
                    { ownsNavigation = false; EnableNativeRotation(); }
                    break;
                case BridgeState.Retainers: TickRetainers(); break;
                case BridgeState.DrainingRetainers:
                    if(!AtCrafter()) { Fail("收尾期间离开指定制作角色，不会在其他角色制作。"); break; }
                    if(host.Now - phaseAt > 120000) { Fail("雇员交互未在 2 分钟内退出，不会提交制作。"); break; }
                    if(!StableIdle(1000)) break;
                    if(host.FirstReady(plan)) { StartCycle(); break; }
                    if(!host.ArtisanIdle) { Fail("Artisan 已被其他流程占用或暂停，没有提交制作。"); break; }
                    if(prepareMaterials)
                    {
                        ownsMaterials = true; State = BridgeState.PreparingMaterials; phaseAt = host.Now; stableAt = 0;
                        materials!.Begin(recipe, count, plan.Crafter.Cid);
                        Detail = "已暂停 AR，正在准备本次制作需要的材料。";
                    }
                    else SubmitCraft();
                    break;
                case BridgeState.PreparingMaterials:
                case BridgeState.StoppingMaterials:
                    TickMaterials();
                    break;
                case BridgeState.CraftPending:
                    CheckCraftIdentity();
                    if(!Running) break;
                    returnWanted |= host.FirstReady(plan);
                    craftAccepted |= host.CraftRequestAccepted;
                    if(host.CraftHasStarted)
                    {
                        if(!host.RecipeMatches(recipe)) { Fail("实际制作配方不符合本次设置，未接管其他制作。"); break; }
                        craftObserved = true; State = BridgeState.Crafting;
                        Detail = "制作中；持续监视开头角色的雇员到期条件。";
                    }
                    else if(returnWanted && craftAccepted)
                    { State = BridgeState.StoppingCraft; phaseAt = host.Now; stableAt = 0; }
                    else if(host.Now - craftAt > 90000)
                        Fail("90 秒内未观察到制作开始；请检查职业、材料及 Artisan。不重试，不恢复 AR。待执行制作可能仍需手动停止。");
                    break;
                case BridgeState.Crafting:
                    CheckCraftIdentity();
                    if(!Running) break;
                    returnWanted |= host.FirstReady(plan);
                    if(returnWanted || !host.ArtisanEndurance)
                    { State = BridgeState.StoppingCraft; phaseAt = host.Now; stableAt = 0; }
                    break;
                case BridgeState.StoppingCraft:
                    CheckCraftIdentity();
                    if(!Running) break;
                    returnWanted |= host.FirstReady(plan);
                    if(StopCraftTick())
                    {
                        ownsCraft = false;
                        if(returnWanted) StartCycle();
                        else { State = BridgeState.WaitingForFirst; Detail = "本次制作已停止或自然结束，继续等待开头角色到期；不代表指定产量全部成功。"; }
                    }
                    break;
                case BridgeState.WaitingForFirst:
                    if(!AtCrafter()) { Fail("等待期间离开指定制作角色，循环已停止。"); break; }
                    if(!host.ArtisanIdle) { Fail("等待期间 Artisan 被其他流程启动，循环已停止。"); break; }
                    if(host.FirstReady(plan)) StartCycle();
                    break;
            }
        }
        catch(Exception ex)
        {
            if(cleanup) { stableAt = 0; Detail = "清理尚未完成，禁止重新启动：" + ex.Message; }
            else Fail("接口或状态检查失败：" + ex.Message);
        }
    }

    private void SubmitCraft()
    {
        ownsCraft = true; craftAt = host.Now; State = BridgeState.CraftPending;
        Detail = "已提交本制作时段的一次请求，等待实际开始；开头到期后撤销后续制作并等待当前操作收尾。";
        host.Craft(recipe, count);
    }

    private void TickMaterials()
    {
        if(materials == null || !ownsMaterials) { Fail("材料准备状态丢失，AR 保持关闭。"); return; }
        if(!AtCrafter() || !host.ArtisanIdle || host.ArBusy || host.NavigationBusy)
        { Fail("材料准备期间角色或自动流程状态改变，停止交接。"); return; }
        if(host.Now - phaseAt > 600000) { Fail("10 分钟内未完成材料准备，AR 保持关闭。"); return; }
        returnWanted |= host.FirstReady(plan);
        if(returnWanted && State == BridgeState.PreparingMaterials)
        {
            State = BridgeState.StoppingMaterials;
            materials.Cancel(); stableAt = 0;
        }
        materials.Tick();
        Detail = (returnWanted ? "开头角色已到期，等待取料收尾：" : "准备制作材料：") + materials.Detail;
        if(materials.State == MaterialPreparationState.Failed)
        { Fail("材料准备失败：" + materials.Detail); return; }
        if(materials.Blocking) { stableAt = 0; return; }
        if(returnWanted)
        {
            if(materials.State is not (MaterialPreparationState.Cancelled or MaterialPreparationState.Completed)) return;
            if(!StableIdle(1000)) return;
            ownsMaterials = false; StartCycle();
            return;
        }
        if(materials.State == MaterialPreparationState.Cancelled)
        { Fail("材料准备已取消，没有提交制作。"); return; }
        if(materials.State != MaterialPreparationState.Completed || !StableIdle(1000)) return;
        ownsMaterials = false; SubmitCraft();
    }

    private void EnableNativeRotation()
    {
        if(!host.LoggedIn || host.CurrentCid != plan.First.Cid) throw new InvalidOperationException("没有确认开头角色。");
        Cycle++; State = BridgeState.Retainers; phaseAt = host.Now;
        ownsNavigation = true; // Native AR can delegate navigation and relog to Lifestream in this exclusive run.
        ArmVisit();
        host.EnableMultiMode();
        Detail = "AutoRetainer 原生多角色轮换中；仅等待指定制作角色的收取结束，不计算全部角色轮次。";
    }
    private bool AtCrafter() => host.LoggedIn && host.CurrentCid == plan.Crafter.Cid;
    private void ArmVisit()
    {
        if(visitArmed || !AtCrafter()) return;
        targets = host.ReadVisitTargets(plan.Crafter.Cid).ToArray();
        if(targets.Count == 0) return;
        collected.Clear(); visitArmed = true;
    }
    private void TickRetainers()
    {
        // Native AR may log out and visit ANY other character. It owns ordering.
        if(visitArmed && !AtCrafter()) { Fail("指定制作角色在收尾确认前离开，已停止；不会改用其他角色制作。"); return; }
        ArmVisit();
        if(host.Now - phaseAt > 7200000) { Fail("2 小时内未完成指定制作角色的收取边界，请检查 AR 轮换条件。"); return; }
        if(heldRetainer != null)
        {
            var old = targets.Single(x => x.Name == heldRetainer);
            var fresh = host.ReadCharacter(plan.Crafter.Cid)?.RetainerData.FirstOrDefault(x => x.Name == heldRetainer);
            if(fresh != null && (!fresh.HasVenture || fresh.VentureID == 0 || fresh.VentureEndsAt > old.VentureEndsAt))
            { collected.Add(heldRetainer); ReleaseRetainer(); }
            else if(host.Now - heldAt > 15000) Fail("雇员后处理没有对应的任务状态更新；不会当作收取成功。");
            return;
        }
        if(visitArmed)
        {
            var data = host.ReadCharacter(plan.Crafter.Cid);
            if(data is not { Enabled: true, ExcludeRetainer: false } || (!TargetSucceeded && (data.Ventures < 2 || data.InventorySpace < 2)))
            { Fail("指定制作角色的启用状态、探险许可或空间不满足收取条件。"); return; }
            // AR's no-next branch may omit CharacterAdditional; require all collection evidence and idle.
            if(TargetSucceeded && host.UiReleased && !host.ArBusy && !host.NavigationBusy)
            { StopAtBoundary(); return; }
        }
        if(!host.ArMultiEnabled) Fail("原 AutoRetainer 多角色模式被停止，桥接循环随之停止。");
    }
    public void OnRetainerAdditional(string name)
    {
        if(State != BridgeState.Retainers || !AtCrafter()) return;
        try
        {
            ArmVisit();
            if(!visitArmed || requestedRetainer != null || heldRetainer != null || collected.Contains(name) || !targets.Any(x => x.Name == name)) return;
            requestedRetainer = name;
            host.RequestRetainer(Owner);
        }
        catch(Exception ex) { Fail("请求雇员后处理失败：" + ex.Message); }
    }
    public void OnRetainerReady(string owner, string name)
    {
        if(owner != Owner || requestedRetainer != name) return;
        requestedRetainer = null; heldRetainer = name;
        if(!Running) { Try(ReleaseRetainer); return; }
        if(State != BridgeState.Retainers || !AtCrafter()) { Fail("雇员后处理不在指定角色上。"); return; }
        heldAt = host.Now;
    }
    public void OnCharacterAdditional()
    {
        if(State != BridgeState.Retainers || !AtCrafter() || !visitArmed || characterRequested || characterHeld) return;
        characterRequested = true;
        try { host.RequestCharacter(Owner); } catch(Exception ex) { Fail("请求角色后处理失败：" + ex.Message); }
    }
    public void OnCharacterReady(string owner)
    {
        if(owner != Owner || !characterRequested) return;
        characterRequested = false; characterHeld = true;
        if(!Running) { Try(ReleaseCharacter); return; }
        try
        {
            // The next relog is already queued. Remain synchronous and preserve this order.
            host.DisableArFunctions(); host.AbortArTasks(); ReleaseCharacter();
            if(State != BridgeState.Retainers || !AtCrafter() || !TargetSucceeded)
            { Fail("指定角色后处理结束时仍缺少成功收取证据；没有启动制作。"); return; }
            EnterDrain();
        }
        catch(Exception ex) { Fail("无法截停已排队的后续换角：" + ex.Message); }
    }
    private void StopAtBoundary() { host.DisableArFunctions(); host.AbortArTasks(); EnterDrain(); }
    private void EnterDrain()
    {
        ownsNavigation = false; State = BridgeState.DrainingRetainers; phaseAt = host.Now; stableAt = 0;
        Detail = "指定制作角色已确认收取，已关闭 AR 并撤销后续换角，等待雇员界面退出。";
    }
    private void CheckCraftIdentity()
    {
        if(!AtCrafter()) Fail("制作期间登录身份改变，不恢复 AutoRetainer。请手动检查 Artisan。");
        else if(host.ArtisanForeignControl) Fail("检测到 Artisan 列表或外部暂停，停止交接；不解除其他流程的控制。");
        else if(host.CraftError is { } error) Fail("制作任务停止：" + error);
    }
    private bool StopCraftTick()
    {
        if(host.Now - phaseAt > 600000) throw new InvalidOperationException("10 分钟内未安全退出制作，AR 保持关闭。请手动处理当前工艺或速制窗口。");
        if(!AtCrafter() || host.ArtisanForeignControl) throw new InvalidOperationException("无法确认本插件的制作所有权；请手动处理 Artisan。");
        host.CancelPendingCraft(); // Native sessions revoke unsent actions immediately; legacy IPC has no queue cancellation.
        // A locally owned queue can be cancelled even if Begin was rejected before accepting any action.
        // Its Blocking/UI proof below still prevents releasing an already-issued native request.
        craftAccepted |= host.CraftRequestAccepted || craftObserved || host.UsesLocalCraftQueue;
        if(!craftAccepted)
        {
            // IsBusy covers the legacy task managers, but omits PreCrafting.Tasks, which can still open
            // a recipe after those queues are aborted. Only an accepted request can be stopped/cleared via IPC.
            stableAt = 0;
            Detail = "循环已停止；兼容方式的请求尚未确认接收，Artisan 私有预处理仍可能打开配方。请在 Artisan 停止该任务；必要时关闭并重新加载 Artisan 后重载本桥接器。不会恢复 AR。后续可改用标准/简易模式的可取消队列。";
            return false;
        }
        if(stopIssued && stopBoundarySeen && host.ActualCraftActive)
            throw new InvalidOperationException("退出当前工艺后又出现迟到制作，AR 保持关闭，请检查 Artisan。");
        if(!stopIssued)
        {
            if(!host.CanStopCraftNow) { stableAt = 0; Detail = "循环已停止；等待当前普通工艺完成，再关闭后续制作。"; return false; }
            if(!host.UsesLocalCraftQueue && !host.RecipeMatches(recipe) && !host.UiReleased)
                throw new InvalidOperationException("制作菜单已不属于本次配方，未关闭其他菜单。");
            host.StopEndurance(); // Never SetStopRequest true/false: that pair resumes the old job.
            stopIssued = true;
        }
        if(host.ArtisanEndurance) throw new InvalidOperationException("停止后 Artisan 再次启用，AR 保持关闭。");
        if(!host.SafeCraftBoundary) { stableAt = 0; Detail = "后续制作已关闭，等待当前工艺或简易制作窗口退出。"; return false; }
        stopBoundarySeen = true;
        if(!closeIssued)
        {
            closeIssued = host.CloseOwnRecipe(recipe);
            if(!closeIssued && !host.UiReleased) return false;
        }
        if(!host.ArtisanIdle || !host.UiReleased || host.ArBusy || host.NavigationBusy) { stableAt = 0; return false; }
        if(stableAt == 0) stableAt = host.Now;
        return host.Now - stableAt >= 2000;
    }
    private bool StableIdle(int duration)
    {
        if(!host.UiReleased || host.ArBusy || host.NavigationBusy) { stableAt = 0; return false; }
        if(stableAt == 0) stableAt = host.Now;
        return host.Now - stableAt >= duration;
    }
    private void ReleaseRetainer() { if(heldRetainer == null) return; heldRetainer = null; host.FinishRetainer(); }
    private void ReleaseCharacter() { if(!characterHeld) return; characterHeld = false; host.FinishCharacter(); }
    public void Cancel()
    {
        if(cleanup)
        {
            Try(host.DisableArFunctions); Try(host.AbortArTasks);
            if(ownsMaterials && materials != null) Try(materials.Cancel);
            return;
        }
        Stop(BridgeState.Cancelled, "已请求停止循环。只清理本插件拥有的工作，不自动恢复制作或 AR。");
    }
    public string? DisposeStop()
    {
        if(!Blocking) return null;
        State = BridgeState.Cancelled;
        Try(host.DisableArFunctions); Try(host.AbortArTasks); Try(ReleaseRetainer); Try(ReleaseCharacter);
        if(ownsNavigation) Try(host.AbortNavigation);
        if(ownsCraft) Try(host.CancelPendingCraft);
        if(ownsMaterials && materials != null)
        {
            Try(materials.Cancel);
            Try(materials.Dispose);
            if(materials.Blocking) return "RetainerCraftBridge unloaded during material preparation. Pending inventory operations or retainer UI require manual cleanup. AR will not resume.";
        }
        if(!ownsCraft) return null;
        // Unload cannot wait for another framework tick. Never touch the global StopRequest.
        if(AtCrafter() && !host.ArtisanForeignControl && host.RecipeMatches(recipe)) Try(host.StopEndurance);
        if(craftObserved && host.SafeCraftBoundary) Try(() => host.CloseOwnRecipe(recipe));
        return craftObserved
            ? "RetainerCraftBridge unloaded during owned crafting. Endurance was asked to stop; the current craft may require manual completion. AR will not resume."
            : "RetainerCraftBridge unloaded with an asynchronous CraftItem still pending. Public IPC cannot remove Artisan's private queue; cancel it manually. AR will not resume.";
    }
    public void Fail(string detail) => Stop(BridgeState.Failed, detail);
    private void Stop(BridgeState result, string detail)
    {
        if(!Running) return;
        State = result; Detail = detail; cleanup = true; stableAt = 0; phaseAt = host.Now;
        Try(host.DisableArFunctions); Try(host.AbortArTasks); Try(ReleaseRetainer); Try(ReleaseCharacter);
        if(ownsNavigation) Try(host.AbortNavigation);
        if(ownsCraft) Try(host.CancelPendingCraft);
        if(ownsMaterials && materials != null) Try(materials.Cancel);
        // Keep outstanding requests: an own Ready already being broadcast must be released once.
    }
    private void CleanupTick()
    {
        host.DisableArFunctions();
        if(ownsMaterials && materials != null)
        {
            // A failed cancellation must never let a still-running preparer submit more withdrawals.
            if(materials.State == MaterialPreparationState.Running)
            {
                materials.Cancel();
                if(materials.State == MaterialPreparationState.Running) return;
            }
            materials.Tick();
            if(materials.Blocking) return;
            ownsMaterials = false; stableAt = 0;
        }
        if(ownsCraft)
        {
            if(!craftObserved)
            {
                if(host.ActualCraftActive && AtCrafter() && host.RecipeMatches(recipe)) craftObserved = true;
            }
            if(!StopCraftTick()) return;
            ownsCraft = false; stableAt = 0;
        }
        if(!StableIdle(2000)) return;
        cleanup = false; ownsNavigation = false;
        Detail += " 已空闲；可检查后手动重新开始。";
    }
    private void Try(Action action) { try { action(); } catch(Exception ex) { Detail += " 清理接口错误：" + ex.Message; } }
}
