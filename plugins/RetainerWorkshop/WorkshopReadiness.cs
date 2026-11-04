using RetainerCraftBridge;

namespace RetainerWorkshop;

internal static class WorkshopReadiness
{
    // During a run, collecting the last venture can legitimately leave no running ventures.
    // That means no next round is ready; it does not invalidate a sale or craft already authorized.
    public static bool IsFirstReady(ReadinessCharacter character, RetainerTiming timing)
    {
        if(!character.Enabled || character.Excluded)
            throw new InvalidOperationException("开头角色已禁用或排除；循环暂停。");
        return ReadinessRules.Ready(character, timing.AdvanceTimer, timing.MultiWaitForAll);
    }

    // Manual startup still requires evidence that both configured endpoints can participate.
    public static void RequireCollectionEndpoint(ReadinessCharacter character)
    {
        if(!character.Enabled || character.Excluded || character.Remaining.Count == 0)
            throw new InvalidOperationException("开头与收尾角色均须启用雇员轮换，且有已勾选的执行中探险。");
    }
}
