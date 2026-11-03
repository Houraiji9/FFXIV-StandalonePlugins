namespace RetainerCraftBridge;

internal readonly record struct CraftIngredient(uint ItemId, int Amount);
internal readonly record struct CraftStock(long Normal, long HighQuality);
internal readonly record struct CraftAssignment(uint ItemId, byte Normal, byte HighQuality);
internal readonly record struct CraftMaterialSelection(uint ItemId, int Amount, int Normal, int HighQuality);
internal enum CraftMaterialAction { Waiting, Complete, AddHighQuality, Invalid }
internal readonly record struct CraftMaterialStep(CraftMaterialAction Action, int Index = -1, int NextAmount = 0);
internal enum CraftReconciliation { Waiting, Complete, Failed, Invalid }

internal static class CraftRules
{
    public static bool ActiveRecipeMatches(CraftMode mode, ushort expected, ushort standardRecipe, ushort quickRecipe) =>
        expected != 0 && (mode == CraftMode.Standard ? standardRecipe == expected : mode == CraftMode.Quick && quickRecipe == expected);

    public static CraftMaterialStep NextMaterialStep(IReadOnlyList<CraftMaterialSelection> current,
        IReadOnlyList<CraftAssignment> target, int pendingIndex, int pendingAmount)
    {
        if(current.Count != target.Count || pendingIndex >= current.Count) return new(CraftMaterialAction.Invalid);
        for(var i = 0; i < current.Count; i++)
        {
            var actual = current[i]; var plan = target[i];
            if(actual.ItemId != plan.ItemId || actual.Amount != plan.Normal + plan.HighQuality ||
               actual.Normal != plan.Normal || actual.HighQuality < 0 || actual.HighQuality > plan.HighQuality)
                return new(CraftMaterialAction.Invalid);
        }
        if(pendingIndex >= 0)
        {
            var actual = current[pendingIndex].HighQuality;
            if(actual == pendingAmount - 1) return new(CraftMaterialAction.Waiting);
            if(actual != pendingAmount) return new(CraftMaterialAction.Invalid);
        }
        for(var i = 0; i < current.Count; i++)
            if(current[i].HighQuality < target[i].HighQuality)
                return new(CraftMaterialAction.AddHighQuality, i, current[i].HighQuality + 1);
        return new(CraftMaterialAction.Complete);
    }

    // NQ first, then only the exact HQ remainder. Reserve duplicate ingredient rows cumulatively.
    public static CraftAssignment[] Assign(IReadOnlyList<CraftIngredient> ingredients, IReadOnlyDictionary<uint, CraftStock> stock)
    {
        var available = stock.ToDictionary(x => x.Key, x => x.Value);
        var result = new CraftAssignment[ingredients.Count];
        for(var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            if(ingredient.Amount == 0) continue;
            if(ingredient.ItemId == 0 || ingredient.Amount < 0 || ingredient.Amount > byte.MaxValue)
                throw new InvalidOperationException("配方材料字段不受支持。");
            var current = available.GetValueOrDefault(ingredient.ItemId);
            if(current.Normal < 0 || current.HighQuality < 0 || current.Normal + current.HighQuality < ingredient.Amount)
                throw new InvalidOperationException($"材料 #{ingredient.ItemId} 不足。");
            var normal = (int)Math.Min(current.Normal, ingredient.Amount);
            var hq = ingredient.Amount - normal;
            result[i] = new(ingredient.ItemId, (byte)normal, (byte)hq);
            available[ingredient.ItemId] = new(current.Normal - normal, current.HighQuality - hq);
        }
        return result;
    }

    public static int QuickBatch(int remaining, IEnumerable<CraftIngredient> ingredients, IReadOnlyDictionary<uint, CraftStock> stock)
    {
        if(remaining < 1) return 0;
        long possible = Math.Min(99, remaining);
        foreach(var ingredient in ingredients.Where(x => x.ItemId != 0 && x.Amount > 0).GroupBy(x => x.ItemId))
        {
            var current = stock.GetValueOrDefault(ingredient.Key);
            var need = ingredient.Sum(x => (long)x.Amount);
            possible = Math.Min(possible, (current.Normal + current.HighQuality) / need);
        }
        return (int)Math.Max(0, possible);
    }

    // Only a real observed start/end and bounded exact product change can settle an attempt.
    public static CraftReconciliation Reconcile(bool started, bool ended, int attempts, int maxAttempts,
        int yield, long before, long after, bool standard, bool progressComplete, bool failureObserved)
    {
        if(attempts < 0 || attempts > maxAttempts || maxAttempts < 1 || yield < 1 || after < before)
            return CraftReconciliation.Invalid;
        var delta = after - before;
        if(delta > (long)attempts * yield || delta % yield != 0) return CraftReconciliation.Invalid;
        if(!started || !ended) return CraftReconciliation.Waiting;
        if(standard)
        {
            if(attempts != 1) return CraftReconciliation.Invalid;
            if(progressComplete && delta == yield) return CraftReconciliation.Complete;
            if(failureObserved && delta == 0) return CraftReconciliation.Failed;
            return CraftReconciliation.Waiting;
        }
        return delta == (long)attempts * yield ? CraftReconciliation.Complete : CraftReconciliation.Waiting;
    }
}

// A single production scheduler is shared by all native actions and pure delay/cancellation tests.
internal sealed class CraftClock
{
    private string? pending;
    private long due, deadline, lastAt = long.MinValue / 2, lastFrame = -1;
    public string? Pending => pending;
    public long Remaining(long now) => pending == null ? 0 : Math.Max(0, due - now);
    public bool Expired(long now) => now > deadline;
    public void Begin(long now, int timeoutSeconds) { pending = null; deadline = now + timeoutSeconds * 1000L; }
    public void Cancel() { pending = null; }
    public bool Dispatch(string action, int delay, long now, long frame)
    {
        if(pending == null)
        {
            pending = action; due = now + Math.Clamp(delay, 100, 30_000);
            deadline += due - now;
            return false;
        }
        if(pending != action) throw new InvalidOperationException("A different craft action is already pending.");
        if(now < due || frame == lastFrame || now - lastAt < 100) return false;
        pending = null; lastAt = now; lastFrame = frame;
        return true;
    }
}
