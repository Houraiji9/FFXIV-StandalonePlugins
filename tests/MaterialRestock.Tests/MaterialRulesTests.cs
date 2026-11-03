using RetainerCraftBridge;

internal static class MaterialRulesTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if(!condition) throw new Exception("FAIL Material: " + label);
            count++;
            Console.WriteLine("PASS Material: " + label);
        }
        void Throws<T>(System.Action action, string label) where T : Exception
        {
            try { action(); }
            catch(T) { Check(true, label); return; }
            throw new Exception("FAIL Material: " + label);
        }
        var nq = new MaterialKey(100, false);
        var hq = new MaterialKey(100, true);
        var crystal = new MaterialKey(2, false);
        var other = new MaterialKey(200, false);
        var requirements = MaterialRules.Requirements([new(100, 3), new(2, 5), new(0, 0), new(100, 2)], 7);
        Check(requirements[100] == 35 && requirements[2] == 35, "full craft count includes crystals and duplicate ingredient rows");
        Throws<ArgumentOutOfRangeException>(() => MaterialRules.Requirements([new(100, 1)], 0), "zero crafts rejected");
        Throws<ArgumentOutOfRangeException>(() => MaterialRules.Requirements([new(100, 1)], 10_000_001), "unbounded craft count rejected");
        Throws<ArgumentException>(() => MaterialRules.Requirements([new(100, -1)], 1), "negative ingredient rejected");
        Throws<ArgumentException>(() => MaterialRules.Requirements([new(1, 3)], 1), "gil cannot be a material");
        Throws<ArgumentException>(() => MaterialRules.Requirements([new(0, 0)], 1), "empty recipe rejected");
        Check(MaterialRules.Requirements([new(100, 999)], 10_000_000)[100] == 9_990_000_000L, "large target stays 64-bit instead of wrapping");
        var totals = MaterialRules.Totals([new(nq, 10), new(hq, 5), new(nq, 4), new(crystal, 20), new(other, 0)]);
        Check(totals[nq] == 14 && totals[hq] == 5 && !totals.ContainsKey(other), "inventory combines stacks while keeping quality distinct");
        var missing = MaterialRules.Missing(requirements, totals);
        Check(missing[100] == 16 && missing[2] == 15, "existing NQ and HQ both reduce ingredient deficit");
        Check(MaterialRules.Missing(new Dictionary<uint,long> { [100] = 10 }, totals).Count == 0, "surplus materials do not request withdrawal");
        Check(MaterialRules.Crystal(2) && MaterialRules.Crystal(19) && !MaterialRules.Crystal(1) && !MaterialRules.Crystal(20), "crystal id range excludes gil and ordinary materials");
        var bag = new MaterialBagSlot[] { new(nq, 995), new(hq, 996), new(other, 999), new(default, 0) };
        Check(MaterialRules.Capacity(nq, 999, bag, totals) == 1003, "NQ capacity includes its partial stack and an empty slot");
        Check(MaterialRules.Capacity(hq, 999, bag, totals) == 1002, "HQ capacity never merges into NQ");
        Check(MaterialRules.Capacity(nq, 999, [new(hq, 990), new(other, 1)], totals) == 0, "different quality or item cannot receive the withdrawal");
        Check(MaterialRules.Capacity(crystal, 9999, [], totals) == 9979, "crystal capacity uses dedicated inventory cap");
        Check(MaterialRules.Capacity(new(2,true), 9999, bag, totals) == 0, "HQ crystal is rejected");
        Check(MaterialRules.Capacity(nq, 0, bag, totals) == 0, "unknown stack limit cannot authorize a withdrawal");
        var before = new Dictionary<MaterialKey,long> { [nq] = 10, [hq] = 2, [other] = 40 };
        Dictionary<MaterialKey,long> After(long received) => new(before) { [nq] = 10 + received };
        Check(MaterialRules.Observe(before, After(0), nq, 7, 20, 20) == MaterialTransferResult.Waiting, "no response stays pending");
        Check(MaterialRules.Observe(before, After(7), nq, 7, 20, 20) == MaterialTransferResult.Waiting, "destination first waits for source update");
        Check(MaterialRules.Observe(before, After(0), nq, 7, 20, 13) == MaterialTransferResult.Waiting, "source first waits for destination update");
        Check(MaterialRules.Observe(before, After(3), nq, 7, 20, 16) == MaterialTransferResult.Waiting, "partial packets never count as complete");
        Check(MaterialRules.Observe(before, After(7), nq, 7, 20, 13) == MaterialTransferResult.Complete, "exact two-sided increase/decrease confirms once");
        Check(MaterialRules.Observe(before, After(1), nq, 1, 1, 0) == MaterialTransferResult.Complete, "single-item direct branch confirms an emptied source");
        Check(MaterialRules.Observe(before, After(7), nq, 7, 20, 12) == MaterialTransferResult.Invalid, "over-withdrawal rejected");
        Check(MaterialRules.Observe(before, After(8), nq, 7, 20, 13) == MaterialTransferResult.Invalid, "excess destination increase rejected");
        Check(MaterialRules.Observe(before, After(-1), nq, 7, 20, 13) == MaterialTransferResult.Invalid, "consumption during transfer rejected");
        var wrongQuality = After(0); wrongQuality[hq] += 7;
        Check(MaterialRules.Observe(before, wrongQuality, nq, 7, 20, 13) == MaterialTransferResult.Invalid, "wrong quality cannot satisfy a pending transfer");
        var external = After(7); external[other] -= 1;
        Check(MaterialRules.Observe(before, external, nq, 7, 20, 13) == MaterialTransferResult.Invalid, "unrelated item change aborts automatic continuation");
        Check(MaterialRules.Observe(before, After(7), nq, 7, 20, -1) == MaterialTransferResult.Invalid, "lost or replaced source remains unverified");
        Check(MaterialRules.Observe(before, After(7), nq, 7, 6, 0) == MaterialTransferResult.Invalid, "cannot withdraw more than frozen source");
        Check(MaterialRules.Observe(before, After(0), nq, 0, 20, 20) == MaterialTransferResult.Invalid, "zero-quantity operation rejected");
        var delays = new MaterialDelays { BellMs = 0, WithdrawMs = 50_000, TimeoutSeconds = 1 };
        var frozen = delays.Freeze(); delays.BellMs = 999; delays.WithdrawMs = 100;
        Check(frozen.BellMs == 100 && frozen.WithdrawMs == 30_000 && frozen.TimeoutSeconds == 15, "run delays are clamped and frozen independently of UI edits");
        Check(new MaterialDelays { TimeoutSeconds = 999 }.Freeze().TimeoutSeconds == 600, "response timeout upper bound is enforced");
        foreach(var response in Enum.GetValues<MaterialResponse>().Where(x => x != MaterialResponse.None))
        {
            Check(MaterialRules.CleanupDecision(response, false, true, true, false, true) == MaterialCleanupDecision.Wait,
                $"{response}: unseen response cannot be retired by empty UI or a long stable/timeout interval");
            Check(MaterialRules.CleanupDecision(response, true, false, true, false, true) == MaterialCleanupDecision.Wait,
                $"{response}: observed response still blocks while its UI remains open");
            Check(MaterialRules.CleanupDecision(response, true, true, false, false, true) == MaterialCleanupDecision.Wait,
                $"{response}: observed and closed response still needs the stable release interval");
            Check(MaterialRules.CleanupDecision(response, true, true, true, false, true) == MaterialCleanupDecision.Released,
                $"{response}: observed then fully released response permits completion");
            Check(MaterialRules.CleanupDecision(response, false, false, false, true, false) == MaterialCleanupDecision.SessionInvalidated,
                $"{response}: lost character session invalidates old work without touching the new session");
        }
        Check(MaterialRules.CleanupDecision(MaterialResponse.None, true, true, true, true, true) == MaterialCleanupDecision.Wait,
            "an unverified sent transfer cannot be retired by empty UI");
        Check(MaterialRules.CleanupDecision(MaterialResponse.None, true, true, true, false, true) == MaterialCleanupDecision.Released,
            "a run with no outstanding response can finish after stable released UI");
        return count;
    }
}
