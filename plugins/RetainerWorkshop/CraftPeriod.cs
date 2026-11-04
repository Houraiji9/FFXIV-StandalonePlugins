using RetainerCraftBridge;
using RetainerVendorSweep;

namespace RetainerWorkshop;

// A single cancellable craft period. Navigation and next-cycle decisions belong to the coordinator.
internal sealed class CraftPeriod(IBridgeHost host, IMaterialPreparation materials)
{
    private enum Phase { Idle, Start, Materials, Pending, Crafting, Stopping }
    private Phase phase;
    private CharacterIdentity crafter = new(0, "", "");
    private VendorDelays configuredDelays = new(), delays = new();
    private ushort recipe;
    private int count;
    private bool restock, ownsMaterials, ownsCraft, accepted, observed, stopIssued, boundarySeen, closeIssued;
    private long due, phaseAt, craftAt, closeDue;
    private long? stableAt;
    private SessionStatus ending;
    public SessionStatus State { get; private set; }
    public bool Blocking => phase != Phase.Idle;
    public string Detail { get; private set; } = "尚未开始制作时段。";
    public void ConfigureDelays(VendorDelays settings) => configuredDelays = settings.Freeze();
    private bool AtCrafter => host.LoggedIn && host.CurrentCid == crafter.Cid;

    public void Begin(CharacterIdentity character, ushort recipeId, int craftCount, bool prepareMaterials)
    {
        if(Blocking) throw new InvalidOperationException("上一制作时段尚未释放。");
        State = SessionStatus.Idle; Detail = "检查本次制作时段。";
        if(character.Cid == 0 || recipeId == 0 || craftCount is < 1 or > 9999) throw new ArgumentException("制作角色、配方或次数无效。");
        if(!host.LoggedIn || host.CurrentCid != character.Cid || !host.UiReleased || host.ArMultiEnabled || host.ArBusy || host.NavigationBusy || !host.ArtisanIdle)
            throw new InvalidOperationException("请先到达制作角色并释放自动流程及交互。");
        crafter = character; recipe = recipeId; count = craftCount; restock = prepareMaterials; delays = configuredDelays.Freeze();
        ownsMaterials = ownsCraft = accepted = observed = stopIssued = boundarySeen = closeIssued = false;
        stableAt = null; ending = SessionStatus.Completed; State = SessionStatus.Running; phase = Phase.Start;
        due = host.Now + delays.StartMs; phaseAt = due; closeDue = 0;
        Detail = "等待制作启动延迟；本时段只提交一次制作请求。";
    }

    public void Tick()
    {
        if(!Blocking) return;
        try
        {
            if(phase == Phase.Stopping) { Drain(); return; }
            if(!AtCrafter || host.ArMultiEnabled || host.ArBusy || host.NavigationBusy || host.ArtisanForeignControl)
            { Fail("制作角色或独占状态发生变化，已停止本时段。"); return; }
            if(host.CraftError is { } error && ownsCraft) { Fail("制作任务失败：" + error); return; }
            switch(phase)
            {
                case Phase.Start:
                    if(host.Now - phaseAt > delays.TimeoutSeconds * 1000L) { Fail("制作启动条件等待超时。"); break; }
                    if(host.Now < due || !host.UiReleased || !host.ArtisanIdle) break;
                    if(restock)
                    {
                        ownsMaterials = true; phase = Phase.Materials; phaseAt = host.Now;
                        materials.Begin(recipe, count, crafter.Cid); Detail = "准备本制作时段材料。";
                    }
                    else Submit();
                    break;
                case Phase.Materials:
                    if(!host.ArtisanIdle) { Fail("取料期间 Artisan 已被其他流程启动。"); break; }
                    if(host.Now - phaseAt > 600000) { Fail("材料准备超过十分钟，未开始制作。"); break; }
                    materials.Tick(); Detail = materials.Detail;
                    if(materials.State == MaterialPreparationState.Failed) { Fail("材料准备失败：" + materials.Detail); break; }
                    if(materials.State == MaterialPreparationState.Cancelled) { Fail("材料准备被取消，未开始制作。"); break; }
                    if(materials.Blocking || materials.State != MaterialPreparationState.Completed || !StableIdle(1000)) break;
                    ownsMaterials = false; Submit();
                    break;
                case Phase.Pending:
                    accepted |= host.CraftRequestAccepted;
                    if(host.CraftHasStarted)
                    {
                        if(!host.RecipeMatches(recipe)) { Fail("实际配方不属于本时段，未接管其他制作。"); break; }
                        observed = true; phase = Phase.Crafting; Detail = "制作中，等待完成或总调度请求停止。";
                    }
                    else if(host.Now - craftAt > 90000) Fail("九十秒内未观察到制作开始；不重发请求。");
                    break;
                case Phase.Crafting:
                    if(!host.RecipeMatches(recipe)) { Fail("制作配方发生变化，不接管其他制作。"); break; }
                    if(!host.ArtisanEndurance) StartStop(SessionStatus.Completed, "本时段制作结束，等待安全收尾；不代表所有成品均成功。");
                    break;
            }
        }
        catch(Exception ex)
        {
            if(phase == Phase.Stopping) { stableAt = null; Detail = "制作收尾仍受阻：" + ex.Message; }
            else Fail("制作时段操作失败：" + ex.Message);
        }
    }

    private void Submit()
    {
        ownsCraft = true; phase = Phase.Pending; craftAt = phaseAt = host.Now;
        host.Craft(recipe, count); Detail = "制作请求已提交一次，等待接收及实际开始。";
    }
    public void Stop()
    {
        if(!Blocking || phase == Phase.Stopping && ending != SessionStatus.Completed) return;
        StartStop(SessionStatus.Stopped, "已撤销后续制作，等待自有材料/制作请求安全结束。");
    }
    public void Fail(string reason)
    {
        if(!Blocking) return;
        if(phase == Phase.Stopping) { ending = State = SessionStatus.Failed; Detail = reason; return; }
        StartStop(SessionStatus.Failed, reason);
    }
    private void StartStop(SessionStatus result, string detail)
    {
        ending = result; State = result == SessionStatus.Completed ? SessionStatus.Running : result;
        phase = Phase.Stopping; phaseAt = host.Now; stableAt = null; Detail = detail;
        if(ownsCraft) Try(host.CancelPendingCraft);
        if(ownsMaterials) Try(materials.Cancel);
    }
    private void Drain()
    {
        if(host.Now - phaseAt > 600000 + delays.CloseMs && ending != SessionStatus.Failed)
        {
            ending = State = SessionStatus.Failed;
            Detail = "制作收尾超过十分钟，保留在途请求阻塞，不会恢复 AR。";
        }
        // AR stays stopped, including when another UI attempts to re-enable it during cleanup.
        if(host.ArMultiEnabled) { host.DisableArFunctions(); host.AbortArTasks(); ending = State = SessionStatus.Failed; }
        if(ownsMaterials)
        {
            if(materials.State == MaterialPreparationState.Running)
            {
                materials.Cancel();
                if(materials.State == MaterialPreparationState.Running) return; // Never Tick a preparer that refused cancellation.
            }
            materials.Tick(); Detail = "等待材料收尾：" + materials.Detail;
            if(materials.Blocking) { stableAt = null; return; }
            if(materials.State == MaterialPreparationState.Failed) ending = State = SessionStatus.Failed;
            ownsMaterials = false; stableAt = null;
        }
        if(ownsCraft)
        {
            if(!AtCrafter || host.ArtisanForeignControl) { stableAt = null; Detail = "无法确认本时段制作所有权，请手动处理；不会恢复 AR。"; return; }
            if(host.CraftError != null) ending = State = SessionStatus.Failed;
            host.CancelPendingCraft();
            accepted |= host.CraftRequestAccepted || observed || host.UsesLocalCraftQueue;
            if(!accepted)
            {
                // Legacy IsBusy omits PreCrafting.Tasks. Silence is not proof of cancellation.
                stableAt = null; Detail = "兼容 CraftItem 尚未确认接收，私有预处理可能迟到。请手动停止 Artisan 后处理；本时段保持阻塞。"; return;
            }
            if(stopIssued && boundarySeen && host.ActualCraftActive)
                throw new InvalidOperationException("已退出后出现迟到制作，请手动检查；不会交还 AR。");
            if(!stopIssued)
            {
                if(!host.CanStopCraftNow) { stableAt = null; Detail = "已停止后续调度，等待当前普通工艺完成。"; return; }
                if(!host.UsesLocalCraftQueue && !host.RecipeMatches(recipe)
                    && (!host.UiReleased || host.ArtisanEndurance || host.ActualCraftActive))
                    throw new InvalidOperationException("菜单不属于本时段配方，未操作其他制作。");
                host.StopEndurance(); stopIssued = true; closeDue = host.Now + delays.CloseMs;
                return; // Ordinary actions stay on separate ticks; no global StopRequest toggle.
            }
            if(host.ArtisanEndurance) throw new InvalidOperationException("停止后再次启用制作，保持阻塞。");
            if(!host.SafeCraftBoundary) { stableAt = null; return; }
            boundarySeen = true;
            if(!closeIssued)
            {
                if(host.Now < closeDue) return;
                closeIssued = host.CloseOwnRecipe(recipe);
                if(!closeIssued && !host.UiReleased) return;
                if(closeIssued) return;
            }
            if(!host.ArtisanIdle || !host.UiReleased || host.ArBusy || host.NavigationBusy) { stableAt = null; return; }
            if(!StableIdle(2000)) return;
            ownsCraft = false; stableAt = null;
        }
        if(!StableIdle(2000)) return;
        phase = Phase.Idle; State = ending;
        Detail = ending == SessionStatus.Completed ? "本制作时段及自有界面已结束；次数是尝试上限，不承诺成品总量。" : "本制作时段已停止并释放自有请求，未恢复 AR。";
    }
    public string? DisposeStop()
    {
        if(!Blocking) return null;
        Stop();
        if(ownsMaterials) Try(materials.Cancel);
        if(ownsCraft)
        {
            Try(host.CancelPendingCraft);
            Try(() => { if(AtCrafter && !host.ArtisanForeignControl && host.RecipeMatches(recipe)) host.StopEndurance(); });
        }
        return "制作时段卸载：不再提交动作或恢复 AR；已发制作/取料请求不能保证撤回，请手动核对。";
    }
    private bool StableIdle(int ms)
    {
        if(!AtCrafter || !host.UiReleased || host.ArBusy || host.NavigationBusy || !host.ArtisanIdle)
        { stableAt = null; return false; }
        stableAt ??= host.Now; return host.Now - stableAt.Value >= ms;
    }
    private void Try(Action action) { try { action(); } catch(Exception ex) { Detail += " 清理异常：" + ex.Message; } }
}
