using RetainerCraftBridge;
using RetainerVendorSweep;
using RetainerWorkshop;

var checks = 0;
void Check(bool value, string name) { checks++; if(!value) throw new Exception("FAIL: " + name); }
bool Refuses(Action action) { try { action(); return false; } catch(InvalidOperationException) { return true; } }
// These are the production helpers called by WorkshopIpcHost, not fake-runtime booleans.
var anyTiming = new RetainerTiming { AdvanceTimer = 300, MultiWaitForAll = false };
var allTiming = new RetainerTiming { AdvanceTimer = 300, MultiWaitForAll = true };
var noVentures = new ReadinessCharacter(true, false, []);
Check(!WorkshopReadiness.IsFirstReady(noVentures, anyTiming), "running empty Any is not ready and does not fail");
Check(!WorkshopReadiness.IsFirstReady(noVentures, allTiming), "running empty All is not vacuously ready and does not fail");
Check(Refuses(() => WorkshopReadiness.RequireCollectionEndpoint(noVentures)), "startup empty endpoint still rejected");
foreach(var remaining in new long[][] { [], [0] })
{
    var disabled = new ReadinessCharacter(false, false, remaining);
    var excluded = new ReadinessCharacter(true, true, remaining);
    Check(Refuses(() => WorkshopReadiness.IsFirstReady(disabled, anyTiming)), "disabled runtime endpoint fails even when empty");
    Check(Refuses(() => WorkshopReadiness.IsFirstReady(excluded, allTiming)), "excluded runtime endpoint fails even when empty");
    Check(Refuses(() => WorkshopReadiness.RequireCollectionEndpoint(disabled)), "disabled startup endpoint rejected");
    Check(Refuses(() => WorkshopReadiness.RequireCollectionEndpoint(excluded)), "excluded startup endpoint rejected");
}
var futureVentures = new ReadinessCharacter(true, false, [301, 500]);
WorkshopReadiness.RequireCollectionEndpoint(futureVentures);
Check(!WorkshopReadiness.IsFirstReady(futureVentures, anyTiming), "valid future endpoint can start but next cycle not ready");
Check(WorkshopReadiness.IsFirstReady(new(true, false, [300, 500]), anyTiming), "new Any task at inclusive advance boundary is ready");
Check(!WorkshopReadiness.IsFirstReady(new(true, false, [300, 500]), allTiming), "new All task waits for remaining selected venture");
Check(WorkshopReadiness.IsFirstReady(new(true, false, [0, 300]), allTiming), "new All tasks at advance boundary are ready");
Check(!WorkshopReadiness.IsFirstReady(noVentures, anyTiming) &&
    WorkshopReadiness.IsFirstReady(new(true, false, [-1]), anyTiming), "empty running snapshot can later become ready with new due task");
VendorDelays Delays() => new() { StartMs = 100, RelogMs = 100, LoginStableMs = 100, NextCharacterMs = 100, CloseMs = 100 };
CyclePlan Plan() => new(new(1, "First", "World"), new(2, "End", "World"));
FakeHost Host() { var h = new FakeHost(); h.Add(1); h.Add(2); return h; }
void Advance(CollectionSession s, FakeHost h, int ms = 100) { h.Now += ms; s.Tick(); }
void Enable(CollectionSession s, FakeHost h) { Advance(s, h); Advance(s, h); }
void Proof(CollectionSession s, FakeHost h, string name = "R")
{
    h.CurrentCid = 2; s.Tick(); s.OnRetainerAdditional(name); s.OnRetainerReady(s.Owner, name);
    h.Characters[2].RetainerData.Single(x => x.Name == name).VentureEndsAt += 3600; s.Tick();
}
void EndBoundary(CollectionSession s, FakeHost h)
{
    s.OnCharacterAdditional(); h.QueuedSwitch = true; s.OnCharacterReady(s.Owner);
    s.Tick(); Advance(s, h, 1100);
}
void DrainCraft(CraftPeriod p, FakeHost h)
{
    for(var i = 0; i < 8; i++) { p.Tick(); h.Now += 2100; }
}
(CraftPeriod Period, FakeHost Host, FakeMaterials Materials) Craft(bool local = true, bool restock = false)
{
    var h = Host(); h.CurrentCid = 3; h.UsesLocalCraftQueue = local;
    var m = new FakeMaterials(); var p = new CraftPeriod(h, m); p.ConfigureDelays(Delays());
    p.Begin(new(3, "Craft", "World"), 10, 5, restock); return (p,h,m);
}
void Submit(CraftPeriod p, FakeHost h) { h.Now += 100; p.Tick(); }
void Accepted(FakeHost h) { h.PendingCraftQueued = false; h.ArtisanEndurance = true; }
void Started(FakeHost h) { Accepted(h); h.LocalHasStarted = true; h.ActualCraftActive = true; h.SafeCraftBoundary = false; }
void Finished(FakeHost h) { h.PendingCraftQueued = false; h.ActualCraftActive = false; h.SafeCraftBoundary = true; h.ArtisanEndurance = false; }

var h = Host(); var s = new CollectionSession(h); var delay = Delays(); delay.StartMs = 500;
s.Begin(Plan(), delay); delay.StartMs = 1;
Advance(s,h,100); Check(h.Enables == 0, "frozen start delay");
Advance(s,h,400); Check(h.Enables == 0, "separate login stability delay");
Advance(s,h); Check(h.Enables == 1 && s.State == SessionStatus.Running, "enable once on first CID");
s.OnRetainerAdditional("R"); s.OnCharacterAdditional(); Check(h.RequestRetainers == 0 && h.RequestCharacters == 0, "non-end notifications ignored");
h.CurrentCid = 9; h.LoggedIn = false; s.Tick(); Check(s.State == SessionStatus.Running, "native AR logout allowed before end visit");
h.LoggedIn = true; h.CurrentCid = 2; s.Tick(); Check(s.Total == 1 && s.Successful == 0, "freeze end targets");
s.OnRetainerAdditional("unknown"); Check(h.RequestRetainers == 0, "unselected retainer ignored");
s.OnRetainerAdditional("R"); s.OnRetainerAdditional("R"); Check(h.RequestRetainers == 1, "duplicate additional dedup");
s.OnRetainerReady("foreign", "R"); Check(h.FinishRetainers == 0, "foreign owner not released");
s.OnRetainerReady(s.Owner, "R"); s.Tick(); Check(s.Successful == 0 && h.FinishRetainers == 0, "postprocess alone insufficient");
h.Characters[2].RetainerData[0].VentureEndsAt += 3600; s.Tick(); Check(s.Successful == 1 && h.FinishRetainers == 1, "new venture proves collection once");
s.OnRetainerReady(s.Owner, "R"); s.OnRetainerAdditional("R"); Check(h.FinishRetainers == 1 && h.RequestRetainers == 1, "duplicate completed notifications ignored");
s.OnCharacterAdditional(); h.Calls.Clear(); h.QueuedSwitch = true; s.OnCharacterReady(s.Owner);
Check(string.Join(",",h.Calls.Take(3)) == "disable,abort,finish-character", "same-frame stop abort release ordering");
Check(h.EscapedSwitches == 0 && s.State == SessionStatus.Running, "no queued relog escape and no premature completion");
h.UiReleased = false; Advance(s,h,2000); Check(s.State != SessionStatus.Completed, "open UI never completes");
h.UiReleased = true; s.Tick(); Advance(s,h,999); Check(s.State != SessionStatus.Completed, "stable idle duration required");
Advance(s,h,1); Check(s.State == SessionStatus.Completed && !s.Blocking, "completed only after proof and idle");
Check(h.Crafts == 0 && h.Enables == 1, "collection never crafts or restarts AR");
var oldOwner = s.Owner; h.CurrentCid = 1; s.Begin(Plan(),Delays()); Enable(s,h);
Check(s.Owner != oldOwner && s.Successful == 0, "new session resets proof and owner");
s.OnCharacterReady(oldOwner); Check(h.FinishCharacters == 1, "old session ready cannot release new lock");
Proof(s,h); EndBoundary(s,h); Check(s.State == SessionStatus.Completed && h.Enables == 2, "two explicit rounds independently complete");

h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); s.OnCharacterAdditional(); s.OnCharacterReady(s.Owner);
Check(s.State == SessionStatus.Failed && s.Blocking && h.Crafts == 0, "character postprocess missing collection fails");
s.Tick(); Advance(s,h,2100); Check(!s.Blocking && s.State == SessionStatus.Failed, "failed session releases without completed count");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); s.OnRetainerAdditional("R"); s.OnRetainerReady(s.Owner,"R");
Advance(s,h,15001); Check(s.State == SessionStatus.Failed && h.FinishRetainers == 1, "stale cache timeout releases own lock");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); Proof(s,h); h.Characters[2].Ventures = 0; h.Characters[2].InventorySpace = 0;
s.Tick(); s.Tick(); Advance(s,h,1100); Check(s.State == SessionStatus.Completed, "last successful collection can exhaust resources; idle fallback");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); h.Characters[2].Ventures = 0; s.Tick();
Check(s.State == SessionStatus.Failed, "resources insufficient before proof cannot count");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); s.OnCharacterAdditional(); s.Stop(); var aborts = h.NavigationAborts;
s.Stop(); s.OnCharacterReady(s.Owner); Check(h.FinishCharacters == 1 && h.NavigationAborts == aborts && s.State == SessionStatus.Stopped, "cancel idempotent and late own ready released");
s.Tick(); Advance(s,h,2100); Check(!s.Blocking && h.Enables == 1, "cancel never resumes AR");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); s.Stop(); s.Tick(); Advance(s,h,2100);
Check(h.Enables == 0 && h.Calls.Count == 0 && !s.Blocking, "stop before start sends no controls");
h = Host(); h.CurrentCid = 5; s = new(h); s.Begin(Plan(),Delays()); Advance(s,h); Advance(s,h);
Check(h.Relogs.Count == 1, "one delayed relog"); s.Stop(); s.Tick(); Advance(s,h,5000);
Check(s.Blocking && h.NavigationAborts == 1, "cancel pending relog cannot treat old CID idle as cancellation proof");
h.CurrentCid = 1; s.Tick(); Advance(s,h,110); Advance(s,h,2100); Advance(s,h,2100);
Check(!s.Blocking && h.Enables == 0, "late requested login cleaned without enabling AR");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); h.CurrentCid = 3; s.Tick();
Check(s.State == SessionStatus.Failed, "leaving armed end prevents wrong character completion");

var (p,ch,m) = Craft(); p.Stop(); DrainCraft(p,ch); Check(!p.Blocking && p.State == SessionStatus.Stopped && ch.Crafts == 0, "craft stop before send");
(p,ch,m) = Craft(); Submit(p,ch); Check(ch.Crafts == 1 && p.State == SessionStatus.Running, "one craft submission");
Started(ch); p.Tick(); Finished(ch); p.Tick(); DrainCraft(p,ch);
Check(!p.Blocking && p.State == SessionStatus.Completed && ch.Enables == 0, "natural crafting completes period only, no AR restart");
Check(ch.Crafts == 1 && ch.EnduranceStops == 1, "natural end never repeats request");
p.Begin(new(3,"Craft","World"),10,1,false); Submit(p,ch); Check(ch.Crafts == 2, "next explicit period can craft again"); p.Stop(); DrainCraft(p,ch);
(p,ch,m) = Craft(false); Submit(p,ch); p.Stop(); DrainCraft(p,ch);
Check(p.Blocking && ch.EnduranceStops == 0 && ch.Crafts == 1, "legacy pending cancellation preserves unknown private queue");
Accepted(ch); DrainCraft(p,ch); Check(!p.Blocking && p.State == SessionStatus.Stopped && ch.EnduranceStops == 1, "legacy accepted later can stop safely");
(p,ch,m) = Craft(false); Submit(p,ch); Started(ch); p.Tick(); p.Stop(); p.Tick();
Check(ch.EnduranceStops == 0 && p.Blocking, "ordinary active craft waits safe boundary");
Finished(ch); DrainCraft(p,ch); Check(!p.Blocking && ch.EnduranceStops == 1, "ordinary completion permits stop");
(p,ch,m) = Craft(false); Submit(p,ch); Started(ch); ch.QuickActive = true; p.Tick(); p.Stop(); p.Tick();
Check(ch.EnduranceStops == 1 && p.Blocking, "QS stops continuation while active");
ch.QuickActive = false; Finished(ch); DrainCraft(p,ch); Check(!p.Blocking, "QS waits actual boundary before release");
(p,ch,m) = Craft(); Submit(p,ch); p.Stop(); DrainCraft(p,ch); Check(!p.Blocking && ch.LocalCancelCalls > 0, "local queue pending cancellation is owned and cancellable");
(p,ch,m) = Craft(); ch.ThrowCraftBeforeAccept = true; Submit(p,ch); DrainCraft(p,ch);
Check(p.State == SessionStatus.Failed && !p.Blocking && ch.Crafts == 1, "local rejected Begin is failed but releasable");
(p,ch,m) = Craft(); Submit(p,ch); Started(ch); ch.RecipeMatchesResult = false; p.Tick();
Check(p.State == SessionStatus.Failed && ch.Enables == 0, "wrong actual recipe never accepted");
(p,ch,m) = Craft(false); Submit(p,ch); Started(ch); p.Tick(); p.Stop(); Finished(ch); p.Tick(); ch.Now += 110; p.Tick();
ch.ActualCraftActive = true; ch.SafeCraftBoundary = false; p.Tick(); Check(p.Blocking && ch.Enables == 0, "late craft after safe boundary blocks handoff");
(p,ch,m) = Craft(false); Submit(p,ch); Accepted(ch); p.Stop(); p.Tick(); ch.ArtisanEndurance = true; p.Tick();
Check(p.Blocking && ch.Enables == 0, "late endurance activation blocks handoff");
(p,ch,m) = Craft(); Submit(p,ch); ch.CurrentCid = 4; p.Tick(); DrainCraft(p,ch);
Check(p.State == SessionStatus.Failed && p.Blocking && ch.CloseCalls == 0, "wrong CID never closes foreign craft");
(p,ch,m) = Craft(); ch.ArMultiEnabled = true; p.Tick(); p.Tick(); Check(!ch.ArMultiEnabled && p.State == SessionStatus.Failed, "external MM enable causes fail and disable during craft handoff");
(p,ch,m) = Craft(restock:true); Submit(p,ch); Check(m.Begins == 1 && ch.Crafts == 0, "materials before crafting");
p.Stop(); p.Tick(); Check(m.Cancels > 0 && p.Blocking && ch.Crafts == 0, "material cancellation blocks until release");
m.Blocking = false; m.State = MaterialPreparationState.Cancelled; DrainCraft(p,ch); Check(!p.Blocking && ch.Crafts == 0, "cancelled materials never craft");
(p,ch,m) = Craft(restock:true); Submit(p,ch); m.ThrowCancel = true; p.Stop(); var ticks = m.Ticks; p.Tick();
Check(m.Ticks == ticks && p.Blocking, "failed material cancel must not tick still-running withdrawals");
m.ThrowCancel = false; m.Blocking = false; m.State = MaterialPreparationState.Cancelled; DrainCraft(p,ch);
Check(!p.Blocking, "material cleanup can recover without restarting");
(p,ch,m) = Craft(restock:true); Submit(p,ch); m.State = MaterialPreparationState.Completed; m.Blocking = false; p.Tick(); ch.Now += 1000; p.Tick();
Check(ch.Crafts == 1, "completed materials after stable idle submit once");
(p,ch,m) = Craft(); Submit(p,ch); var warning = p.DisposeStop();
Check(warning != null && ch.LocalCancelCalls > 0 && ch.Enables == 0, "unload synchronous cancellation and explicit caveat");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); warning = s.DisposeStop();
Check(warning != null && !h.ArMultiEnabled && h.NavigationAborts > 0, "collection unload disables owned work");

h = Host(); h.CurrentCid = 5; h.RejectRelog = true; s = new(h); s.Begin(Plan(),Delays()); Advance(s,h); Advance(s,h);
Check(s.State == SessionStatus.Failed && h.Relogs.Count == 1, "explicit rejected relog fails once");
s.Tick(); Advance(s,h,2100); Check(!s.Blocking && h.NavigationAborts == 0, "known rejected relog needs no phantom arrival");
h = Host(); h.CurrentCid = 5; h.ThrowRelog = true; s = new(h); s.Begin(Plan(),Delays()); Advance(s,h); Advance(s,h); s.Tick(); Advance(s,h,5000);
Check(s.Blocking && s.State == SessionStatus.Failed && h.Relogs.Count == 1, "unknown relog throw retains pending arrival");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); s.OnRetainerAdditional("R"); s.OnRetainerReady(s.Owner,"R");
h.Characters[2].RetainerData[0].HasVenture = false; s.Tick(); Check(s.Successful == 1, "received retainer clearing old venture proves collection");
h = Host(); h.Characters[2].RetainerData.Add(new() { Name="S", HasVenture=true, VentureID=2, VentureEndsAt=100 });
s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); Proof(s,h); s.OnCharacterAdditional(); s.OnCharacterReady(s.Owner);
Check(s.State == SessionStatus.Failed && s.Successful == 1 && s.Total == 2, "missing one frozen retainer blocks boundary");
h = Host(); h.Characters[2].RetainerData.Clear(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); h.CurrentCid = 2; s.Tick(); s.OnCharacterAdditional();
Check(s.Total == 0 && h.RequestCharacters == 0 && s.State != SessionStatus.Completed, "empty target cannot complete business cycle");
h = Host(); s = new(h); var longDelay = Delays(); longDelay.StartMs = 60000; longDelay.TimeoutSeconds = 15; s.Begin(Plan(),longDelay);
Advance(s,h,59000); Check(s.State == SessionStatus.Running && h.Enables == 0, "human start delay excluded from timeout");
Advance(s,h,1000); Advance(s,h,100); Check(h.Enables == 1, "long human delay dispatch remains reachable");
h = Host(); s = new(h); s.Begin(Plan(),Delays()); Enable(s,h); Proof(s,h); s.OnCharacterAdditional(); s.OnCharacterReady(s.Owner); h.ArMultiEnabled = true; s.Tick();
Check(s.State == SessionStatus.Failed && !h.ArMultiEnabled, "external MM restart during final drain invalidates completion");
(p,ch,m) = Craft(false); Submit(p,ch); p.Stop(); ch.Now += 601000; p.Tick();
Check(p.Blocking && p.State == SessionStatus.Failed && ch.EnduranceStops == 0, "legacy unaccepted timeout remains blocked and reports failure");
(p,ch,m) = Craft(false); Submit(p,ch); Accepted(ch); p.Stop(); ch.RecipeMatchesResult = false; p.Tick();
Check(p.Blocking && ch.EnduranceStops == 0, "accepted foreign recipe cannot be stopped even if UI reports released");
(p,ch,m) = Craft(restock:true); Submit(p,ch); m.State = MaterialPreparationState.Failed; m.Blocking = false; p.Tick(); DrainCraft(p,ch);
Check(p.State == SessionStatus.Failed && !p.Blocking && ch.Crafts == 0, "material failure never submits craft and cleans released request");
(p,ch,m) = Craft(); var freezeDelay = Delays(); freezeDelay.StartMs=60000; freezeDelay.TimeoutSeconds=15; p.ConfigureDelays(freezeDelay);
p.Stop(); DrainCraft(p,ch); p.Begin(new(3,"Craft","World"),10,1,false); freezeDelay.StartMs=100; ch.Now += 59000; p.Tick();
Check(p.State == SessionStatus.Running && ch.Crafts == 0, "craft delay frozen and excluded from response timeout");
ch.Now += 1000; p.Tick(); Check(ch.Crafts == 1, "long craft delay eventually sends once");

Console.WriteLine($"{checks} session checks passed.");

sealed class FakeMaterials : IMaterialPreparation
{
    public MaterialPreparationState State { get; set; }
    public bool Blocking { get; set; }
    public string Detail => "fake materials";
    public int Begins, Cancels, Ticks;
    public bool ThrowCancel;
    public void Begin(ushort recipe, int count, ulong cid) { Begins++; State = MaterialPreparationState.Running; Blocking = true; }
    public void Tick() { Ticks++; }
    public void Cancel() { Cancels++; if(ThrowCancel) throw new Exception("cancel refused"); if(State != MaterialPreparationState.Failed) State = MaterialPreparationState.Cancelling; }
    public void Dispose() { }
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
    public bool PendingCraftQueued, QuickActive, QueuedSwitch, RejectRelog, ThrowRelog;
    public bool RecipeMatchesResult = true;
    public int Enables, Crafts, EnduranceStops, CloseCalls, RequestRetainers, RequestCharacters, FinishRetainers, FinishCharacters, EscapedSwitches, NavigationAborts;
    public List<string> Calls { get; } = [];
    public List<string> Relogs { get; } = [];
    public Dictionary<ulong, CharacterSnapshot> Characters { get; } = [];
    public void Add(ulong cid) => Characters.TryAdd(cid, new() { CID = cid, Name = "Character", World = "World", Enabled = true, Ventures = 100, InventorySpace = 100,
        RetainerData = [new() { Name = "R", HasVenture = true, VentureID = 1, VentureEndsAt = 100 }] });
    public bool FirstReady(CyclePlan plan) => false;
    public IReadOnlyList<TargetRetainer> ReadVisitTargets(ulong cid) => Characters[cid].RetainerData.Where(x => x.HasVenture)
        .Select(x => new TargetRetainer(x.Name, x.VentureID, x.VentureEndsAt)).ToArray();
    public CharacterSnapshot? ReadCharacter(ulong cid) => Characters.GetValueOrDefault(cid);
    public bool RecipeMatches(ushort recipe) => RecipeMatchesResult;
    public void EnableMultiMode() { Enables++; ArMultiEnabled = true; Calls.Add("enable"); }
    public void DisableArFunctions() { ArMultiEnabled = false; Calls.Add("disable"); }
    public void AbortArTasks() { QueuedSwitch = false; ArBusy = false; Calls.Add("abort"); }
    public void Relog(string name, string world) { Relogs.Add(name + "@" + world); if(RejectRelog) throw new CharacterSwitchRejectedException("known rejection"); if(ThrowRelog) throw new Exception("unknown submission"); }
    public void AbortNavigation() { NavigationAborts++; NavigationBusy = false; Calls.Add("abort-navigation"); }
    public void RequestRetainer(string owner) { RequestRetainers++; Calls.Add("request-retainer"); }
    public void FinishRetainer() { FinishRetainers++; Calls.Add("finish-retainer"); }
    public void RequestCharacter(string owner) { RequestCharacters++; Calls.Add("request-character"); }
    public void FinishCharacter() { FinishCharacters++; if(QueuedSwitch) EscapedSwitches++; Calls.Add("finish-character"); }
    public void Craft(ushort recipe, int count) { Crafts++; LocalHasStarted = false; LocalAccepted = false; if(ThrowCraftBeforeAccept) throw new InvalidOperationException("rejected"); PendingCraftQueued = true; LocalAccepted = UsesLocalCraftQueue; Calls.Add("craft"); }
    public void CancelPendingCraft() { if(UsesLocalCraftQueue) { LocalCancelCalls++; PendingCraftQueued = false; ArtisanEndurance = false; } }
    public void StopEndurance() { EnduranceStops++; ArtisanEndurance = false; Calls.Add("stop-endurance"); }
    public bool CloseOwnRecipe(ushort recipe) { CloseCalls++; UiReleased = true; Calls.Add("close-recipe"); return true; }
}
