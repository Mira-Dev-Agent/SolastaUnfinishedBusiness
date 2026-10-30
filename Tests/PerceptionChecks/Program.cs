using System;
using System.Collections;
using System.Linq;
using SolastaUnfinishedBusiness.Models;

static class Program
{
    static int checks;
    static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++;
    }
    static bool Valid(int value) => value >= 0 && value <= 3;
    static void Main()
    {
        // same requests on two computers -> same single roll, never rerolled
        var a = new PerceptionLedger();
        var b = new PerceptionLedger();
        int rollsA = 0, rollsB = 0;
        a.TryResolve(1, 2, 0, () => { rollsA++; return 1; }, out var outcomeA);
        b.TryResolve(1, 2, 0, () => { rollsB++; return 1; }, out var outcomeB);
        Check(outcomeA == outcomeB && rollsA == 1 && rollsB == 1, "one roll per pair");
        for (var i = 0; i < 100; i++) a.TryResolve(1, 2, 0, () => ++rollsA, out _);
        Check(rollsA == 1, "repeated queries never reroll");
        a.TryResolve(2, 1, 0, () => 2, out _);
        Check(a.TryGet(2, 1, out var reverse) && reverse == 2 && a.Count == 2, "directions are separate pairs");

        // snapshots: canonical, validated, atomic
        var snapshot = a.Capture();
        Check(snapshot[0] == 2 && (snapshot.Length - 1) % 4 == 0, "snapshot version and shape");
        Check(b.Restore(snapshot, Valid), "recover ledger");
        b.TryResolve(1, 2, 0, () => ++rollsB, out _);
        Check(rollsB == 1 && b.Capture().SequenceEqual(snapshot), "recovery does not reroll");
        Check(!b.Restore(new long[] { 2, 1, 2, 1, 0, 1, 2, 2, 0 }, Valid), "reject duplicate pair");
        Check(b.Capture().SequenceEqual(snapshot), "invalid restore is atomic");
        Check(!b.Restore(new long[] { 2, 1, 2, 99, 0 }, Valid), "reject invalid outcome");
        Check(!b.Restore(new long[] { 1, 0 }, Valid), "reject old format");
        Check(!b.Restore(new long[] { 2, 1, 2, 1 }, Valid), "reject truncated entry");
        Check(!b.Restore(new long[] { 2, 1, 2, 1, -1 }, Valid), "reject negative phase");
        Check(!b.Restore(new long[] { 2, 0, 2, 1, 0 }, Valid), "reject empty guid");
        var ordered = new PerceptionLedger();
        ordered.TryResolve(2, 1, 0, () => 2, out _);
        ordered.TryResolve(1, 2, 0, () => 1, out _);
        Check(ordered.Capture().SequenceEqual(snapshot), "canonical snapshot order");

        // phases: outside combat carries into round 1; a previous fight's results don't; later rounds start fresh
        var phases = new PerceptionLedger();
        phases.TryResolve(10, 11, 0, () => 1, out _);   // rolled outside combat
        phases.TryResolve(12, 13, 3, () => 1, out _);   // left over from an earlier fight's round 3
        phases.KeepOnlyOutOfCombat();                   // round 1 starts
        Check(phases.TryGet(10, 11, out _) && !phases.TryGet(12, 13, out _) && phases.Count == 1,
            "round 1 keeps only out-of-combat checks");
        phases.TryResolve(10, 11, 1, () => throw new Exception("rerolled in round 1"), out _);
        phases.TryResolve(14, 15, 1, () => 1, out _);   // rolled in round 1
        phases.Clear();                                 // round 2 starts
        Check(phases.Count == 0, "round 2 starts fresh");
        phases.TryResolve(10, 11, 2, () => 3, out var fresh);
        Check(fresh == 3, "a new round rolls once again");

        // re-entrancy and failures
        var loaded = new PerceptionLedger();
        int recursiveRolls = 0;
        loaded.TryResolve(3, 4, 0, () =>
        {
            recursiveRolls++;
            Check(!loaded.TryResolve(3, 4, 0, () => ++recursiveRolls, out _), "reentrant notification cannot reroll");
            return 1;
        }, out _);
        Check(recursiveRolls == 1, "one recursive roll only");
        try { loaded.TryResolve(5, 6, 0, () => throw new InvalidOperationException(), out _); }
        catch (InvalidOperationException) { }
        Check(!loaded.TryGet(5, 6, out _), "throwing roll not marked complete");
        Check(loaded.TryResolve(5, 6, 0, () => 1, out _), "throw releases reentry guard");

        int depth = 0;
        Func<IDisposable> enter = () => { depth++; return new Cleanup(() => depth--); };
        IEnumerator Child()
        {
            Check(depth == 1, "child runs in scope");
            yield return null;
            Check(depth == 1, "child resumes in scope");
        }
        IEnumerator Parent()
        {
            Check(depth == 1, "parent runs in scope");
            yield return Child();
            Check(depth == 1, "parent resumes in scope");
        }
        using var wrapper = new ScopedEnumerator(Parent(), enter, _ => true);
        Check(depth == 0 && wrapper.MoveNext() && depth == 0, "scope absent at yield");
        var child = (IEnumerator)wrapper.Current;
        Check(child.MoveNext() && depth == 0, "scope absent while child suspended");
        Check(!child.MoveNext() && depth == 0, "child completion restores scope");
        Check(!wrapper.MoveNext() && depth == 0, "parent completion restores scope");
        Check(!wrapper.MoveNext(), "completed iterator inert");
        IEnumerator Fault()
        {
            Check(depth == 1, "fault starts scoped");
            throw new InvalidOperationException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        using var fault = new ScopedEnumerator(Fault(), enter, _ => true);
        try { fault.MoveNext(); } catch (InvalidOperationException) { }
        Check(depth == 0 && !fault.MoveNext(), "exception releases scope and disposes");
        Console.WriteLine($"{checks} checks passed");
    }
    sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
}
