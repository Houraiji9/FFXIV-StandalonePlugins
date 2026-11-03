using RetainerCraftBridge;

var assertions = 0;
void Check(bool value, string message)
{
    if(!value) throw new Exception("FAIL " + message);
    assertions++;
    Console.WriteLine("PASS " + message);
}
(BridgeMachine M, FakeHost H) Begin(ulong first = 1, ulong crafter = 9)
{
    var host = new FakeHost { CurrentCid = first };
    host.Add(first); host.Add(crafter);
    var machine = new BridgeMachine(host);
    machine.Start(new(new(first, "First", "World"), new(crafter, "Crafter", "World")), 123, 7);
    machine.Tick();
    return (machine, host);
}
void Arrive(BridgeMachine m, FakeHost h, ulong cid = 9)
{
    h.CurrentCid = cid; h.LoggedIn = true; h.UiReleased = true; h.NavigationBusy = false;
    m.Tick();
}
void Ack(BridgeMachine m, FakeHost h)
{
    m.OnRetainerAdditional("R");
    m.OnRetainerReady(m.Owner, "R");
    h.Characters[h.CurrentCid].RetainerData[0].VentureEndsAt += 3600;
    m.Tick();
}
void Boundary(BridgeMachine m, FakeHost h, bool eventBoundary = false)
{
    Ack(m, h);
    if(eventBoundary)
    {
        m.OnCharacterAdditional();
        h.QueuedSwitch = true;
        m.OnCharacterReady(m.Owner);
    }
    else m.Tick();
}
void Submit(BridgeMachine m, FakeHost h)
{
    m.Tick(); h.Now += 1100; m.Tick();
}
void StartCraft(BridgeMachine m, FakeHost h)
{
    h.PendingCraftQueued = false; h.ArtisanEndurance = true;
    h.ActualCraftActive = true; h.SafeCraftBoundary = false; h.UiReleased = false;
    m.Tick();
}
void EndCraft(BridgeMachine m, FakeHost h)
{
    h.ActualCraftActive = false; h.SafeCraftBoundary = true; h.UiReleased = false;
    m.Tick(); h.Now += 2100; m.Tick();
}

var enabled = new ReadinessCharacter(true, false, [30, 400]);
Check(ReadinessRules.Ready(enabled, 30, false), "Head Any uses inclusive AdvanceTimer window");
Check(!ReadinessRules.Ready(enabled, 30, true), "Head All does not wake when another selected venture is later");
Check(ReadinessRules.Ready(new(true, false, [0, -10]), 0, true), "Head All works once all selected ventures are due");
Check(!ReadinessRules.Ready(new(true, false, []), 300, false), "No selected ventures is not a new round");
Check(!ReadinessRules.Ready(new(false, false, [-20]), 300, false), "Disabled head is not ready");
Check(!ReadinessRules.Ready(new(true, true, [-20]), 300, false), "Excluded head is not ready");
Check(!ReadinessRules.Ready(new(true, false, [31]), 30, false), "Outside advance window stays waiting");
Check(ReadinessRules.Ready(new(true, false, [0]), -5, true), "AR zero pass remains valid even if advance is negative");
Check(ReadinessRules.Ready(new(true, false, [5, 6]), 10, true), "WaitForAll applies only to the configured head's selected ventures");

var (m, h) = Begin();
Check(m.State == BridgeState.Retainers && h.Enables == 1, "Starts native AR at configured first character");
h.CurrentCid = 4; h.LoggedIn = false; m.Tick();
Check(m.State == BridgeState.Retainers, "Native AR may log out between unrelated characters");
h.Add(5); Arrive(m, h, 5); m.OnRetainerAdditional("R"); m.OnCharacterAdditional();
Check(h.RequestRetainers == 0 && h.RequestCharacters == 0, "Unrelated characters keep native postprocessing and rotation");
Arrive(m, h);
h.Characters[9].RetainerData[0].VentureEndsAt += 3600; m.Tick();
Check(m.Successful == 0, "Offline data change without own Ready cannot count as target collection");
Boundary(m, h, true);
var boundaryCalls = h.Calls.TakeLast(3).ToArray();
Check(boundaryCalls.SequenceEqual(new[] { "disable", "abort", "finish-character" }), "Owned CharacterReady disables then aborts then finishes synchronously");
Check(h.EscapedSwitches == 0, "Prequeued native relog cannot escape the target boundary");
m.OnCharacterReady(m.Owner);
Check(h.FinishCharacters == 1, "Duplicate Ready never releases another consumer's lock");
Submit(m, h);
Check(m.State == BridgeState.CraftPending && h.Crafts == 1 && h.CraftedCid == 9, "Crafts once on configured character after released UI");
m.Tick(); m.Tick(); Check(h.Crafts == 1, "A pending CraftItem is not submitted repeatedly");
StartCraft(m, h);
Check(m.State == BridgeState.Crafting && m.Running, "Observed craft start keeps the bridge running");
h.HeadReady = true; m.Tick(); m.Tick();
Check(m.State == BridgeState.StoppingCraft && h.EnduranceStops == 0, "Head due during a craft waits for the current craft boundary");
EndCraft(m, h);
Check(h.EnduranceStops == 1 && h.CloseCalls == 1 && m.State == BridgeState.StartingCycle, "Safe boundary stops owned Endurance and closes only its recipe once");
m.Tick(); Check(m.State == BridgeState.TravellingToFirst && h.Relogs.SequenceEqual(new[] { "First@World" }), "Returns to the configured head before enabling AR again");
Arrive(m, h, 7); Check(h.Enables == 1, "Wrong travel destination cannot enable the next AR cycle");
h.HeadReady = false; Arrive(m, h, 1);
Check(h.Enables == 2 && m.Cycle == 2, "Second cycle starts only at the configured head");
Arrive(m, h); Boundary(m, h); Submit(m, h);
Check(h.Crafts == 2, "A new cycle permits exactly one new craft request");
StartCraft(m, h); h.ArtisanEndurance = false; m.Tick(); EndCraft(m, h);
Check(m.State == BridgeState.WaitingForFirst && m.Running && h.Enables == 2, "Natural count completion remains monitoring instead of ending the loop");
m.Tick(); Check(h.Crafts == 2, "Waiting after natural completion does not refill the craft count");
h.HeadReady = true; m.Tick(); Check(m.State == BridgeState.StartingCycle, "Head due after natural completion starts the next cycle");

var (same, sh) = Begin(1, 1); Boundary(same, sh); Submit(same, sh);
Check(sh.Crafts == 1 && sh.Relogs.Count == 0 && sh.CraftedCid == 1, "First and crafting character may be the same");

var (early, eh) = Begin(); Arrive(early, eh); Boundary(early, eh); eh.HeadReady = true; Submit(early, eh);
Check(eh.Crafts == 0 && early.State == BridgeState.StartingCycle, "Head already due at boundary skips craft and starts the next rotation");

var (pending, ph) = Begin(); Arrive(pending, ph); Boundary(pending, ph); Submit(pending, ph);
ph.HeadReady = true; pending.Tick(); pending.Tick();
Check(ph.EnduranceStops == 0 && pending.State == BridgeState.CraftPending, "Head due after enqueue cannot prematurely disable not-yet-started CraftItem");
StartCraft(pending, ph); pending.Tick(); EndCraft(pending, ph);
Check(ph.EnduranceStops == 1 && pending.State == BridgeState.StartingCycle, "Pending request is drained only after actual start and a safe craft boundary");

var (cancelPending, cph) = Begin(); Arrive(cancelPending, cph); Boundary(cancelPending, cph); Submit(cancelPending, cph);
cancelPending.Cancel(); cancelPending.Tick();
Check(cph.EnduranceStops == 0 && cancelPending.Blocking, "Cancel pending crafting waits rather than allowing a late restart");
StartCraft(cancelPending, cph); EndCraft(cancelPending, cph); cph.Now += 2100; cancelPending.Tick();
Check(cph.Enables == 1 && cancelPending.State == BridgeState.Cancelled && !cancelPending.Blocking, "Cancelled pending craft drains without restarting AR");

var (locks, lh) = Begin(); Arrive(locks, lh); locks.OnRetainerAdditional("R"); locks.Cancel();
Check(lh.FinishRetainers == 0, "Cancel does not release a requested but unowned global lock");
locks.OnRetainerReady("foreign", "R");
Check(lh.FinishRetainers == 0, "Foreign Ready is untouched during cancellation");
locks.OnRetainerReady(locks.Owner, "R"); locks.OnRetainerReady(locks.Owner, "R");
Check(lh.FinishRetainers == 1 && lh.Calls.IndexOf("abort") < lh.Calls.IndexOf("finish-retainer"), "Late owned Ready is released once after aborting queued work");

var (noEvidence, nh) = Begin(); Arrive(noEvidence, nh); noEvidence.OnRetainerAdditional("R"); noEvidence.OnRetainerReady(noEvidence.Owner, "R");
nh.Now += 16000; noEvidence.Tick();
Check(noEvidence.State == BridgeState.Failed && nh.Crafts == 0, "Postprocess without fresh venture state fails closed");
Check(nh.FinishRetainers == 1, "Failure releases only its held retainer lock");

var (lastPermit, lph) = Begin(); Arrive(lastPermit, lph); Ack(lastPermit, lph);
lph.Characters[9].Ventures = 0; lph.Characters[9].InventorySpace = 0; lastPermit.Tick(); Submit(lastPermit, lph);
Check(lph.Crafts == 1, "Last successful collection can consume the final permits or inventory slots");
var (noPermit, nph) = Begin(); Arrive(noPermit, nph); nph.Characters[9].Ventures = 0; noPermit.Tick();
Check(noPermit.State == BridgeState.Failed && nph.Crafts == 0, "Resource failure before collection is not success");

var (manualAr, mah) = Begin(); Arrive(manualAr, mah); Boundary(manualAr, mah); mah.ArMultiEnabled = true; manualAr.Tick();
Check(manualAr.State == BridgeState.Failed && mah.Crafts == 0 && !mah.ArMultiEnabled, "Manual AR restart during handoff stops the bridge");
var (wrongCid, wh) = Begin(); Arrive(wrongCid, wh); Boundary(wrongCid, wh); wh.CurrentCid = 2; wrongCid.Tick();
Check(wrongCid.State == BridgeState.Failed && wh.Crafts == 0, "Wrong crafting CID never becomes a fallback");

var (timeout, th) = Begin(); Arrive(timeout, th); Boundary(timeout, th); Submit(timeout, th); th.Now += 91000; timeout.Tick(); timeout.Tick();
Check(timeout.State == BridgeState.Failed && timeout.Blocking && th.Crafts == 1 && th.Enables == 1, "Unobserved async start times out without retry or AR resume");
var (late, lateh) = Begin(); Arrive(late, lateh); Boundary(late, lateh); Submit(late, lateh); StartCraft(late, lateh);
lateh.HeadReady = true; late.Tick(); lateh.ActualCraftActive = false; lateh.SafeCraftBoundary = true; late.Tick();
lateh.ActualCraftActive = true; lateh.SafeCraftBoundary = false; late.Tick();
Check(late.State == BridgeState.Failed && lateh.Enables == 1, "Late Artisan queue after stop is a failure, never a parallel AR start");

var (foreign, fh) = Begin(); Arrive(foreign, fh); Boundary(foreign, fh); Submit(foreign, fh); StartCraft(foreign, fh);
fh.ArtisanForeignControl = true; foreign.Cancel(); foreign.Tick();
Check(fh.EnduranceStops == 0 && foreign.Blocking, "Cancellation does not release another workflow's Artisan pause/list ownership");
var (recipeChange, rh) = Begin(); Arrive(recipeChange, rh); Boundary(recipeChange, rh); Submit(recipeChange, rh); StartCraft(recipeChange, rh);
rh.RecipeMatchesResult = false; rh.ActualCraftActive = false; rh.SafeCraftBoundary = true; rh.HeadReady = true; recipeChange.Tick(); recipeChange.Tick();
Check(recipeChange.State == BridgeState.Failed && rh.CloseCalls == 0 && rh.EnduranceStops == 0, "Changed recipe menu is not stopped or closed as owned");

var (travel, trh) = Begin(); travel.Cancel(); trh.Now += 2100; travel.Tick(); trh.Now += 2100; travel.Tick();
Check(trh.NavigationAborts == 1 && !travel.Blocking, "Cancel native rotation aborts its owned navigation and waits for drain");
var (failedTravel, fth) = Begin(); Arrive(failedTravel, fth); Boundary(failedTravel, fth); fth.HeadReady = true; Submit(failedTravel, fth);
fth.ThrowRelog = true; failedTravel.Tick();
Check(failedTravel.State == BridgeState.Failed && fth.NavigationAborts == 1 && fth.Crafts == 0, "Rejected return-to-head is not retried and does not craft");

var (unload, uh) = Begin(); Arrive(unload, uh); Boundary(unload, uh); Submit(unload, uh);
var unloadWarning = unload.DisposeStop();
Check(unloadWarning?.Contains("pending") == true && uh.EnduranceStops == 1 && uh.Enables == 1, "Unload synchronously stops known recipe mode and warns about uncancellable pending queue");
var (unloadOther, uoh) = Begin(); Arrive(unloadOther, uoh); Boundary(unloadOther, uoh); Submit(unloadOther, uoh); StartCraft(unloadOther, uoh);
uoh.RecipeMatchesResult = false; unloadOther.DisposeStop();
Check(uoh.EnduranceStops == 0, "Unload does not stop Endurance after recipe ownership changes");

var (missing, mh) = Begin(); Arrive(missing, mh); missing.OnCharacterAdditional(); mh.QueuedSwitch = true; missing.OnCharacterReady(missing.Owner);
Check(missing.State == BridgeState.Failed && mh.Crafts == 0 && mh.EscapedSwitches == 0, "Character boundary with missing collection proof aborts but never crafts");
var (lateMode, lmh) = Begin(); Arrive(lateMode, lmh); Boundary(lateMode, lmh); Submit(lateMode, lmh); StartCraft(lateMode, lmh);
lmh.HeadReady = true; lateMode.Tick(); lmh.ActualCraftActive = false; lmh.SafeCraftBoundary = true; lateMode.Tick();
lmh.ArtisanEndurance = true; lateMode.Tick();
Check(lateMode.State == BridgeState.Failed && lmh.Enables == 1 && lmh.EnduranceStops == 1, "Endurance restarting after stop fails without another stop or AR resume");

(BridgeMachine M, FakeHost H, FakeMaterials Materials) Preparing()
{
    var host = new FakeHost(); host.Add(1); host.Add(9);
    var materials = new FakeMaterials();
    var machine = new BridgeMachine(host, materials);
    machine.Start(new(new(1, "First", "World"), new(9, "Crafter", "World")), 123, 7, true);
    machine.Tick(); Arrive(machine, host); Boundary(machine, host); Submit(machine, host);
    return (machine, host, materials);
}
var (prep, preph, prepService) = Preparing();
Check(prep.State == BridgeState.PreparingMaterials && prepService.Begins == 1 && preph.Crafts == 0
    && prepService.Request == (123, 7, 9UL), "Materials are prepared once for the frozen recipe/count/crafter before CraftItem");
prep.Tick(); prep.Tick();
Check(preph.Crafts == 0 && prepService.Begins == 1, "Pending material withdrawal cannot enqueue crafting or duplicate preparation");
prepService.State = MaterialPreparationState.Completed; prepService.Blocking = true; prep.Tick();
Check(preph.Crafts == 0, "Material quantities alone cannot bypass an outstanding owned UI/transfer");
prepService.Blocking = false; preph.UiReleased = false; prep.Tick(); preph.Now += 1500; prep.Tick();
Check(preph.Crafts == 0, "Completed material job still requires released retainer UI");
preph.UiReleased = true; prep.Tick(); preph.Now += 1100; prep.Tick();
Check(preph.Crafts == 1 && prep.State == BridgeState.CraftPending, "Verified material preparation and stable idle permit one craft submission");

var (prepDue, pdh, pds) = Preparing();
pdh.HeadReady = true; prepDue.Tick(); prepDue.Tick();
Check(prepDue.State == BridgeState.StoppingMaterials && pds.Cancels == 1 && pdh.Crafts == 0 && pdh.Enables == 1,
    "Head due during materials cancels once and waits without crafting or resuming AR");
pds.State = MaterialPreparationState.Cancelled; pds.Blocking = false; pdh.UiReleased = false; prepDue.Tick();
Check(prepDue.State == BridgeState.StoppingMaterials, "Cancelled material queue with open UI cannot hand control to AR");
pdh.UiReleased = true; prepDue.Tick(); pdh.Now += 1100; prepDue.Tick();
Check(prepDue.State == BridgeState.StartingCycle && pdh.Crafts == 0, "Cancelled material preparation returns to head only after stable cleanup");

var (prepCancel, pch, pcs) = Preparing(); prepCancel.Cancel(); prepCancel.Tick();
Check(prepCancel.Blocking && pcs.Cancels == 1 && pch.Crafts == 0 && pch.Enables == 1,
    "User stop during withdrawal retains cleanup ownership without restarting AR");
pcs.State = MaterialPreparationState.Cancelled; pcs.Blocking = false; prepCancel.Tick(); pch.Now += 2100; prepCancel.Tick();
Check(!prepCancel.Blocking && prepCancel.State == BridgeState.Cancelled && pch.Enables == 1,
    "User-cancelled material cleanup ends without restarting the loop");

var (prepFail, pfh, pfs) = Preparing(); pfs.State = MaterialPreparationState.Failed; prepFail.Tick(); prepFail.Tick();
Check(prepFail.State == BridgeState.Failed && prepFail.Blocking && pfh.Crafts == 0 && pfh.Enables == 1,
    "Failed withdrawal never crafts or releases unresolved material work");
var (prepWrong, pwh, pws) = Preparing(); pwh.CurrentCid = 2; prepWrong.Tick();
Check(prepWrong.State == BridgeState.Failed && pws.Cancels == 1 && pwh.Crafts == 0,
    "Character change during material preparation cancels without touching the next craft");
var (prepTimeout, pth, pts) = Preparing(); pth.Now += 600001; prepTimeout.Tick();
Check(prepTimeout.State == BridgeState.Failed && pts.Cancels == 1 && pth.Crafts == 0,
    "Material preparation timeout begins cleanup without queueing Artisan");
var (prepUnload, puh, pus) = Preparing(); var prepUnloadWarning = prepUnload.DisposeStop();
Check(prepUnloadWarning?.Contains("material preparation") == true && pus.Disposed && puh.Crafts == 0 && puh.Enables == 1,
    "Unload with outstanding materials warns and keeps AR disabled");
var (prepCancelThrow, pcth, pcts) = Preparing(); pcts.ThrowCancel = true;
var ticksBeforeCancel = pcts.Ticks;
prepCancelThrow.Cancel(); prepCancelThrow.Tick(); prepCancelThrow.Tick();
Check(pcts.Ticks == ticksBeforeCancel && prepCancelThrow.Blocking && pcth.Crafts == 0,
    "A failing material cancellation cannot tick a still-running withdrawal service");
pcts.ThrowCancel = false; prepCancelThrow.Tick();
Check(pcts.State == MaterialPreparationState.Cancelling && pcts.Ticks == ticksBeforeCancel + 1,
    "Cleanup resumes material ticking only after cancellation has actually taken effect");

var (acceptedPending, aph) = Begin(); Arrive(acceptedPending, aph); Boundary(acceptedPending, aph); Submit(acceptedPending, aph);
aph.PendingCraftQueued = false; aph.ArtisanEndurance = true; aph.UiReleased = false;
acceptedPending.Cancel(); acceptedPending.Tick();
Check(aph.EnduranceStops == 1 && !aph.ArtisanEndurance && aph.Crafts == 1,
    "Cancel stops accepted Endurance before the first actual craft instead of waiting forever");
aph.Now += 2100; acceptedPending.Tick(); aph.Now += 2100; acceptedPending.Tick();
Check(!acceptedPending.Blocking && aph.Enables == 1,
    "Accepted but never-started crafting drains to a stopped usable plugin without resuming AR");

var (clearedPending, clph) = Begin(); Arrive(clearedPending, clph); Boundary(clearedPending, clph); Submit(clearedPending, clph);
clearedPending.Cancel(); clearedPending.Tick();
Check(clearedPending.Blocking && clph.EnduranceStops == 0, "Busy private activation still prevents premature cancellation success");
clph.PendingCraftQueued = false; clearedPending.Tick(); clph.Now += 2100; clearedPending.Tick(); clph.Now += 2100; clearedPending.Tick();
Check(clearedPending.Blocking && !clph.ActualCraftActive && clph.Enables == 1 && clph.EnduranceStops == 0,
    "Legacy IsBusy=false cannot prove its unobserved PreCrafting tasks have been cleared");

var (quickStop, qsh) = Begin(); Arrive(quickStop, qsh); Boundary(quickStop, qsh); Submit(quickStop, qsh);
qsh.QuickActive = true; StartCraft(quickStop, qsh); quickStop.Cancel(); quickStop.Tick(); quickStop.Tick();
Check(qsh.EnduranceStops == 1 && quickStop.Blocking && quickStop.State == BridgeState.Cancelled,
    "Quick synthesis stops repeat mode immediately while the current batch is still active");
qsh.QuickActive = false; EndCraft(quickStop, qsh); qsh.Now += 2100; quickStop.Tick();
Check(!quickStop.Blocking && qsh.Enables == 1, "Quick batch exit completes user stop without restarting AR");

var (ownPending, oph) = Begin(); Arrive(ownPending, oph); Boundary(ownPending, oph);
oph.UsesLocalCraftQueue = true; Submit(ownPending, oph); oph.RecipeMatchesResult = false;
ownPending.Cancel(); ownPending.Tick(); oph.Now += 2100; ownPending.Tick(); oph.Now += 2100; ownPending.Tick();
Check(oph.LocalCancelCalls > 0 && !ownPending.Blocking && !oph.PendingCraftQueued && oph.Enables == 1,
    "Own native queue can cancel before recipe selection without requiring foreign menu ownership");
var (fastNative, fnh) = Begin(); Arrive(fastNative, fnh); Boundary(fastNative, fnh);
fnh.UsesLocalCraftQueue = true; Submit(fastNative, fnh);
fnh.PendingCraftQueued = false; fnh.LocalHasStarted = true; fnh.ArtisanEndurance = false;
fastNative.Tick();
Check(fastNative.State == BridgeState.Crafting && !fnh.ActualCraftActive,
    "Historical start proof captures a fast native craft that has already ended between bridge polls");
fastNative.Tick(); EndCraft(fastNative, fnh);
Check(fastNative.State == BridgeState.WaitingForFirst && fnh.Crafts == 1,
    "Native completed count enters monitoring without another crafting request");
var (nativeFailure, nfh) = Begin(); Arrive(nativeFailure, nfh); Boundary(nativeFailure, nfh);
nfh.UsesLocalCraftQueue = true; Submit(nativeFailure, nfh); nfh.CraftError = "verified failure";
nativeFailure.Tick();
Check(nativeFailure.State == BridgeState.Failed && nfh.LocalCancelCalls > 0 && nfh.Crafts == 1,
    "Native failure revokes queued starts and is never mistaken for successful count completion");

var (nativeRejected, nrh) = Begin(); Arrive(nativeRejected, nrh); Boundary(nativeRejected, nrh);
nrh.UsesLocalCraftQueue = true; nrh.ThrowCraftBeforeAccept = true; Submit(nativeRejected, nrh);
nativeRejected.Tick(); nrh.Now += 2100; nativeRejected.Tick(); nrh.Now += 2100; nativeRejected.Tick();
Check(nativeRejected.State == BridgeState.Failed && !nativeRejected.Blocking && !nrh.LocalAccepted && !nrh.PendingCraftQueued && nrh.Enables == 1,
    "Rejected local Begin has no private pending work and can clean up without ever being accepted");

Console.WriteLine($"{assertions} assertions passed.");

sealed class FakeMaterials : IMaterialPreparation
{
    public MaterialPreparationState State { get; set; }
    public bool Blocking { get; set; }
    public string Detail => "fake preparation";
    public int Begins, Cancels, Ticks;
    public bool Disposed, ThrowCancel;
    public (ushort Recipe, int Count, ulong Cid) Request;
    public void Begin(ushort recipe, int count, ulong cid)
    { Begins++; Request = (recipe, count, cid); State = MaterialPreparationState.Running; Blocking = true; }
    public void Tick() { Ticks++; }
    public void Cancel() { Cancels++; if(ThrowCancel) throw new InvalidOperationException("cancel failed"); if(State != MaterialPreparationState.Failed) State = MaterialPreparationState.Cancelling; }
    public void Dispose() { Disposed = true; }
}

sealed class FakeHost : IBridgeHost
{
    public long Now { get; set; } = 10000;
    public ulong CurrentCid { get; set; } = 1;
    public bool LoggedIn { get; set; } = true;
    public bool UiReleased { get; set; } = true;
    public bool ArBusy { get; set; }
    public bool NavigationBusy { get; set; }
    public bool ArMultiEnabled { get; set; }
    public bool ActualCraftActive { get; set; }
    public bool SafeCraftBoundary { get; set; } = true;
    public bool ArtisanEndurance { get; set; }
    public bool ArtisanForeignControl { get; set; }
    public bool ArtisanIdle => !PendingCraftQueued && !ArtisanEndurance && !ActualCraftActive && !ArtisanForeignControl;
    public bool CraftRequestAccepted => UsesLocalCraftQueue ? LocalAccepted : ArtisanEndurance;
    public bool CanStopCraftNow => UsesLocalCraftQueue || SafeCraftBoundary || !ActualCraftActive || QuickActive;
    public bool CraftHasStarted => UsesLocalCraftQueue ? LocalHasStarted : ActualCraftActive;
    public string? CraftError { get; set; }
    public bool UsesLocalCraftQueue { get; set; }
    public bool LocalAccepted, LocalHasStarted, ThrowCraftBeforeAccept;
    public int LocalCancelCalls;
    public bool PendingCraftQueued, QuickActive;
    public bool HeadReady, QueuedSwitch, ThrowRelog;
    public bool RecipeMatchesResult = true;
    public int Enables, Crafts, EnduranceStops, CloseCalls, RequestRetainers, RequestCharacters, FinishRetainers, FinishCharacters, EscapedSwitches, NavigationAborts;
    public ulong CraftedCid;
    public List<string> Calls { get; } = [];
    public List<string> Relogs { get; } = [];
    public Dictionary<ulong, CharacterSnapshot> Characters { get; } = [];
    public void Add(ulong cid) => Characters.TryAdd(cid, new() { CID = cid, Name = "Character", World = "World", Enabled = true, Ventures = 100, InventorySpace = 100,
        RetainerData = [new() { Name = "R", HasVenture = true, VentureID = 1, VentureEndsAt = 100 }] });
    public bool FirstReady(CyclePlan plan) => HeadReady;
    public IReadOnlyList<TargetRetainer> ReadVisitTargets(ulong cid) => Characters[cid].RetainerData.Where(x => x.HasVenture)
        .Select(x => new TargetRetainer(x.Name, x.VentureID, x.VentureEndsAt)).ToArray();
    public CharacterSnapshot? ReadCharacter(ulong cid) => Characters.GetValueOrDefault(cid);
    public bool RecipeMatches(ushort recipe) => RecipeMatchesResult;
    public void EnableMultiMode() { Enables++; ArMultiEnabled = true; Calls.Add("enable"); }
    public void DisableArFunctions() { ArMultiEnabled = false; Calls.Add("disable"); }
    public void AbortArTasks() { QueuedSwitch = false; ArBusy = false; Calls.Add("abort"); }
    public void Relog(string name, string world) { Relogs.Add(name + "@" + world); if(ThrowRelog) throw new Exception("refused"); }
    public void AbortNavigation() { NavigationAborts++; NavigationBusy = false; Calls.Add("abort-navigation"); }
    public void RequestRetainer(string owner) { RequestRetainers++; Calls.Add("request-retainer"); }
    public void FinishRetainer() { FinishRetainers++; Calls.Add("finish-retainer"); }
    public void RequestCharacter(string owner) { RequestCharacters++; Calls.Add("request-character"); }
    public void FinishCharacter() { FinishCharacters++; if(QueuedSwitch) EscapedSwitches++; Calls.Add("finish-character"); }
    public void Craft(ushort recipe, int count) { Crafts++; if(ThrowCraftBeforeAccept) throw new InvalidOperationException("rejected before native action"); CraftedCid = CurrentCid; PendingCraftQueued = true; LocalAccepted = UsesLocalCraftQueue; Calls.Add("craft"); }
    public void CancelPendingCraft() { if(UsesLocalCraftQueue) { LocalCancelCalls++; PendingCraftQueued = false; ArtisanEndurance = false; } }
    public void StopEndurance() { EnduranceStops++; ArtisanEndurance = false; Calls.Add("stop-endurance"); }
    public bool CloseOwnRecipe(ushort recipe) { CloseCalls++; UiReleased = true; Calls.Add("close-recipe"); return true; }
}
