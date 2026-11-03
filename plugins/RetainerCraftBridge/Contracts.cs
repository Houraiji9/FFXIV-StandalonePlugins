namespace RetainerCraftBridge;

// Minimal public IPC DTOs. Dalamud converts foreign return objects through JSON.
// No AutoRetainer binary dependency or private reflection is used.
public sealed class CharacterSnapshot
{
    public ulong CID { get; set; }
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public bool Enabled { get; set; }
    public bool ExcludeRetainer { get; set; }
    public uint Ventures { get; set; }
    public uint InventorySpace { get; set; }
    public List<RetainerSnapshot> RetainerData { get; set; } = [];
}
public sealed class RetainerSnapshot
{
    public string Name { get; set; } = "";
    public long VentureEndsAt { get; set; }
    public bool HasVenture { get; set; }
    public uint VentureID { get; set; }
}
public sealed class RetainerTiming
{
    public int AdvanceTimer { get; set; }
    public bool MultiWaitForAll { get; set; }
}
internal sealed record TargetRetainer(string Name, uint VentureId, long VentureEndsAt);
internal sealed record CharacterIdentity(ulong Cid, string Name, string World);
internal sealed record CyclePlan(CharacterIdentity First, CharacterIdentity Crafter);
internal sealed record ReadinessCharacter(bool Enabled, bool Excluded, IReadOnlyList<long> Remaining);

internal static class ReadinessRules
{
    // AR selects candidates in <=0 and <=AdvanceTimer passes. For one sentinel
    // these combine into the advance condition; the zero pass only affects ordering.
    public static bool Ready(ReadinessCharacter first, int advanceSeconds, bool waitForAll)
    {
        if(!first.Enabled || first.Excluded || first.Remaining.Count == 0) return false;
        var threshold = Math.Max(0, advanceSeconds);
        var ready = waitForAll ? first.Remaining.All(x => x <= threshold) : first.Remaining.Any(x => x <= threshold);
        return ready;
    }
}
internal enum BridgeState
{
    Idle, StartingCycle, TravellingToFirst, Retainers, DrainingRetainers,
    PreparingMaterials, StoppingMaterials, CraftPending, Crafting, StoppingCraft, WaitingForFirst, Failed, Cancelled,
}
internal enum MaterialPreparationState { Idle, Running, Completed, Cancelling, Cancelled, Failed }
internal interface IMaterialPreparation : IDisposable
{
    MaterialPreparationState State { get; }
    bool Blocking { get; }
    string Detail { get; }
    void Begin(ushort recipe, int count, ulong cid);
    void Tick();
    void Cancel();
}
internal interface IBridgeHost
{
    long Now { get; }
    ulong CurrentCid { get; }
    bool LoggedIn { get; }
    bool UiReleased { get; }
    bool ArBusy { get; }
    bool NavigationBusy { get; }
    bool ArMultiEnabled { get; }
    bool ActualCraftActive { get; }
    bool SafeCraftBoundary { get; }
    bool ArtisanIdle { get; }
    bool ArtisanEndurance { get; }
    bool ArtisanForeignControl { get; }
    bool CraftRequestAccepted { get; }
    bool CanStopCraftNow { get; }
    bool CraftHasStarted { get; }
    string? CraftError { get; }
    bool UsesLocalCraftQueue { get; }
    bool FirstReady(CyclePlan plan);
    IReadOnlyList<TargetRetainer> ReadVisitTargets(ulong cid);
    CharacterSnapshot? ReadCharacter(ulong cid);
    bool RecipeMatches(ushort recipe);
    void EnableMultiMode();
    void DisableArFunctions();
    void AbortArTasks();
    void Relog(string name, string world);
    void AbortNavigation();
    void RequestRetainer(string owner);
    void FinishRetainer();
    void RequestCharacter(string owner);
    void FinishCharacter();
    void Craft(ushort recipe, int count);
    void CancelPendingCraft();
    void StopEndurance();
    bool CloseOwnRecipe(ushort recipe);
}
