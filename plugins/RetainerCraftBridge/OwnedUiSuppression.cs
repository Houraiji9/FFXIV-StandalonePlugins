using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RetainerCraftBridge;

// YesAlready's documented cooperative pause set. Never clears the shared set, toggles plugin
// configuration, removes another owner, or creates a missing provider on YesAlready's behalf.
internal sealed class OwnedUiSuppression(IDalamudPluginInterface pi, IFramework framework) : IDisposable
{
    private const string Tag = "YesAlready.StopRequests";
    private readonly string owner = "RetainerCraftBridge_UI_" + Guid.NewGuid().ToString("N");
    private HashSet<string>? held;
    private bool disposed, borrowed;

    // Call before starting, and before every owned queue tick while the bridge is Blocking.
    // A reload may replace/clear the provider's set, so acquiring once is insufficient.
    public bool TryAcquire(out string error)
    {
        error = "";
        if(disposed || framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread)
        { error = "自动界面协作暂停必须在有效游戏框架线程执行。"; return false; }
        try
        {
            if(!pi.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == "YesAlready"))
            {
                Release();
                return true; // Optional cooperation: no new plugin dependency is introduced.
            }
            if(!pi.TryGetData<HashSet<string>>(Tag, out var current))
            { error = "YesAlready 已加载，但公开暂停接口尚不可用；不会继续自动操作界面。"; return false; }
            borrowed = true;
            if(!ReferenceEquals(held, current))
            {
                Release();
                held = current;
            }
            current.Add(owner);
            if(!current.Contains(owner))
            { error = "未能持有本插件的 YesAlready 暂停请求，停止后续自动操作。"; return false; }
            return true;
        }
        catch(Exception ex)
        {
            error = $"YesAlready 协作暂停失败（{ex.GetType().Name}）；不会继续自动操作界面。";
            return false;
        }
    }

    // Managed shared-data operation only; no game/native cleanup is sent during plugin disposal.
    // If removal throws, retain the reference so a subsequent cleanup attempt can retry our key.
    public void Release()
    {
        if(held == null) return;
        held.Remove(owner);
        held = null;
    }

    public void Dispose()
    {
        disposed = true;
        Release();
        if(borrowed)
        {
            pi.RelinquishData(Tag);
            borrowed = false;
        }
    }
}
