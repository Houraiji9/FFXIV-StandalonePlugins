namespace RetainerCraftBridge;

public enum CraftMode { Inherit, Standard, Quick }
internal enum CraftSessionState { Idle, Preparing, WaitingStart, Crafting, Settling, Stopping, Completed, Stopped, Failed }

[Serializable]
public sealed class CraftDelays
{
    public int OpenRecipeMs { get; set; } = 500;
    public int FillMaterialsMs { get; set; } = 500;
    public int StartCraftMs { get; set; } = 800;
    public int SubmitQuickMs { get; set; } = 800;
    public int CancelQuickMs { get; set; } = 300;
    public int CloseRecipeMs { get; set; } = 500;
    public int BetweenCraftsMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 90;

    public CraftDelays Freeze() => new()
    {
        OpenRecipeMs = Step(OpenRecipeMs), FillMaterialsMs = Step(FillMaterialsMs), StartCraftMs = Step(StartCraftMs),
        SubmitQuickMs = Step(SubmitQuickMs), CancelQuickMs = Step(CancelQuickMs), CloseRecipeMs = Step(CloseRecipeMs),
        BetweenCraftsMs = Step(BetweenCraftsMs), TimeoutSeconds = Math.Clamp(TimeoutSeconds, 15, 600)
    };
    private static int Step(int value) => Math.Clamp(value, 100, 30_000);
}

internal interface ICraftSession : IDisposable
{
    CraftSessionState State { get; }
    bool Active { get; }
    bool RepeatEnabled { get; }
    bool Accepted { get; }
    bool Blocking { get; }
    bool Failed { get; }
    bool HasStarted { get; }
    bool UiReleased { get; }
    int Attempted { get; }
    int Completed { get; }
    int Requested { get; }
    string Detail { get; }
    void Begin(ushort recipe, int count, CraftMode mode);
    void Tick();
    void Stop();
}
