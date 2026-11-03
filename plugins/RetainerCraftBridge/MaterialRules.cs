namespace RetainerCraftBridge;

[Serializable]
public sealed class MaterialDelays
{
    public int BellMs { get; set; } = 800;
    public int SelectRetainerMs { get; set; } = 500;
    public int OpenInventoryMs { get; set; } = 500;
    public int WithdrawMs { get; set; } = 500;
    public int SubmitQuantityMs { get; set; } = 500;
    public int CloseUiMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 90;

    public MaterialDelays Freeze() => new()
    {
        BellMs = Step(BellMs), SelectRetainerMs = Step(SelectRetainerMs), OpenInventoryMs = Step(OpenInventoryMs),
        WithdrawMs = Step(WithdrawMs), SubmitQuantityMs = Step(SubmitQuantityMs), CloseUiMs = Step(CloseUiMs),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 15, 600)
    };
    private static int Step(int value) => Math.Clamp(value, 100, 30_000);
}

internal readonly record struct MaterialKey(uint ItemId, bool HighQuality);
internal readonly record struct MaterialIngredient(uint ItemId, int PerCraft);
internal readonly record struct MaterialBagSlot(MaterialKey Key, int Quantity);
internal enum MaterialTransferResult { Waiting, Complete, Invalid }
internal enum MaterialResponse { None, BellList, RetainerMenu, Inventory, Quantity, ReturnMenu, ReturnList }
internal enum MaterialCleanupDecision { Wait, Released, SessionInvalidated }

internal static class MaterialRules
{
    // An elapsed timeout, absence of windows, or a failed state does not prove that a previously
    // submitted native request was cancelled. Only a bound response (then released UI) or a lost
    // character session can retire that request without starting another automatic operation.
    public static MaterialCleanupDecision CleanupDecision(MaterialResponse response, bool observed,
        bool uiReleased, bool stable, bool transferPending, bool sessionMatches)
    {
        if(!sessionMatches) return MaterialCleanupDecision.SessionInvalidated;
        if(response != MaterialResponse.None && !observed) return MaterialCleanupDecision.Wait;
        return uiReleased && stable && !transferPending ? MaterialCleanupDecision.Released : MaterialCleanupDecision.Wait;
    }

    public static Dictionary<uint, long> Requirements(IEnumerable<MaterialIngredient> ingredients, int count)
    {
        if(count < 1 || count > 10_000_000) throw new ArgumentOutOfRangeException(nameof(count));
        var result = new Dictionary<uint, long>();
        foreach(var ingredient in ingredients)
        {
            if(ingredient.ItemId == 0 || ingredient.PerCraft == 0) continue;
            if(ingredient.ItemId == 1 || ingredient.PerCraft < 0) throw new ArgumentException("Invalid recipe ingredient.");
            var amount = checked((long)ingredient.PerCraft * count);
            result[ingredient.ItemId] = checked(result.GetValueOrDefault(ingredient.ItemId) + amount);
        }
        if(result.Count == 0) throw new ArgumentException("Recipe has no supported ingredients.");
        return result;
    }

    public static Dictionary<MaterialKey, long> Totals(IEnumerable<MaterialBagSlot> slots) => slots
        .Where(x => x.Key.ItemId != 0 && x.Quantity > 0).GroupBy(x => x.Key)
        .ToDictionary(x => x.Key, x => x.Sum(y => (long)y.Quantity));

    public static Dictionary<uint, long> Missing(IReadOnlyDictionary<uint, long> required, IReadOnlyDictionary<MaterialKey, long> current)
    {
        var result = new Dictionary<uint, long>();
        foreach(var (id, count) in required)
        {
            var present = current.GetValueOrDefault(new(id, false)) + current.GetValueOrDefault(new(id, true));
            if(count > present) result[id] = count - present;
        }
        return result;
    }

    public static bool Crystal(uint id) => id is >= 2 and <= 19;

    public static long Capacity(MaterialKey key, int stackLimit, IReadOnlyList<MaterialBagSlot> ordinary,
        IReadOnlyDictionary<MaterialKey, long> current)
    {
        if(stackLimit < 1) return 0;
        if(Crystal(key.ItemId))
            return key.HighQuality ? 0 : Math.Max(0, (long)stackLimit - current.GetValueOrDefault(key));
        long amount = 0;
        foreach(var slot in ordinary)
        {
            if(slot.Quantity == 0) amount += stackLimit;
            else if(slot.Key == key && slot.Quantity > 0) amount += Math.Max(0, stackLimit - slot.Quantity);
        }
        return amount;
    }

    // A withdrawal changes one source and one item/quality total. The two server updates may arrive separately.
    public static MaterialTransferResult Observe(IReadOnlyDictionary<MaterialKey, long> before,
        IReadOnlyDictionary<MaterialKey, long> after, MaterialKey key, int amount, int sourceBefore, int sourceAfter)
    {
        if(amount <= 0 || amount > sourceBefore || sourceAfter < 0 || sourceAfter > sourceBefore)
            return MaterialTransferResult.Invalid;
        var taken = sourceBefore - sourceAfter;
        if(taken > amount) return MaterialTransferResult.Invalid;
        foreach(var item in before.Keys.Concat(after.Keys).Distinct())
        {
            var change = after.GetValueOrDefault(item) - before.GetValueOrDefault(item);
            if(item == key ? change < 0 || change > amount : change != 0) return MaterialTransferResult.Invalid;
        }
        var received = after.GetValueOrDefault(key) - before.GetValueOrDefault(key);
        return taken == amount && received == amount ? MaterialTransferResult.Complete : MaterialTransferResult.Waiting;
    }
}
