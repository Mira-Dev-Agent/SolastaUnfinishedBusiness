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

        // phases: outside combat carries into round 1 (as round 1); an earlier fight's results don't; later rounds fresh
        var phases = new PerceptionLedger();
        phases.TryResolve(10, 11, 0, () => 1, out _);   // rolled outside combat
        phases.TryResolve(12, 13, 3, () => 1, out _);   // left over from an earlier fight's round 3
        phases.CarryIntoRound1();                       // round 1 starts
        Check(phases.TryGet(10, 11, out _) && !phases.TryGet(12, 13, out _) && phases.Count == 1,
            "round 1 keeps only out-of-combat checks");
        Check(phases.Capture().Last() == 1, "carried checks become round 1 checks");
        phases.TryResolve(10, 11, 1, () => throw new Exception("rerolled in round 1"), out _);
        phases.CarryIntoRound1();                       // another fight's round 1 (this one ended in round 1)
        Check(phases.Count == 0, "a later fight never inherits carried checks");
        phases.TryResolve(14, 15, 1, () => 1, out _);
        phases.Clear();                                 // round 2 starts
        Check(phases.Count == 0, "round 2 starts fresh");
        phases.TryResolve(10, 11, 2, () => 3, out var fresh);
        Check(fresh == 3, "a new round rolls once again");

        // validate before change
        Check(PerceptionLedger.Parse(snapshot, Valid) != null, "parse a valid snapshot");
        Check(PerceptionLedger.Parse(new long[] { 2, 1, 2, 99, 0 }, Valid) == null, "parse rejects without change");

        // request bookkeeping: generations, stale commands, the round 1 exception, cooldowns, bounds
        Check(PerceptionRequests.Applies(4, 2, 4, 2), "same generation applies");
        Check(!PerceptionRequests.Applies(3, 2, 4, 3), "older generation in combat is stale");
        Check(PerceptionRequests.Applies(3, 0, 4, 1), "outside-combat request applies in the next round 1");
        Check(!PerceptionRequests.Applies(3, 0, 4, 2), "but not in round 2");
        Check(!PerceptionRequests.Applies(2, 0, 4, 1), "nor two generations later");
        Check(!PerceptionRequests.Applies(5, 1, 4, 1), "a future generation never applies");
        var requests = new PerceptionRequests();
        Check(requests.TryMarkPending((1, 2), 7, 0, 0f, out var first) && requests.IsPending((1, 2)), "mark pending");
        Check(first.Generation == 7 && first.Phase == 0, "a request remembers its own generation and phase");
        Check(!requests.TryMarkPending((1, 2), 8, 1, 0f, out _), "no duplicate request");
        // Codex re-review repro 2: queued in generation 7, a round starts, its command runs later: it still clears it,
        // because completion matches the request id, not the current generation
        requests.CompleteOwn((1, 2), first.Id + 99, true, 0.5f);
        Check(requests.IsPending((1, 2)), "another request's command can't clear it");
        requests.CompleteOwn((1, 2), first.Id, false, 1f);
        Check(!requests.IsPending((1, 2)), "its own command clears it across a round start");
        Check(!requests.TryMarkPending((1, 2), 8, 1, 2f, out _), "unresolved pair cools down");
        Check(requests.TryMarkPending((1, 2), 8, 1, 3.1f, out var retry), "and can be requested again after the cooldown");
        Check(retry.Id > first.Id, "request ids only increase");
        // Codex re-review repro 5b: a retry in the same generation is not cleared by the earlier request's late command
        requests.Prune(3.1f + PerceptionRequests.MaxPendingSeconds + 0.1f);
        Check(!requests.IsPending((1, 2)), "a request whose command never came expires");
        Check(requests.TryMarkPending((1, 2), 8, 1, 20f, out var again), "and the pair can be requested again");
        requests.CompleteOwn((1, 2), retry.Id, true, 20.5f);
        Check(requests.IsPending((1, 2)), "the expired request's late command can't clear the new one");
        requests.Resolved((1, 2));
        Check(!requests.IsPending((1, 2)), "a result from anyone settles the pair");
        Check(requests.TryMarkPending((1, 2), 9, 2, 21f, out var afterResult), "a resolved pair has no cooldown");
        requests.Clear();
        Check(requests.TryMarkPending((1, 2), 0, 0, 22f, out var afterClear) && afterClear.Id > afterResult.Id && afterResult.Id > again.Id,
            "ids keep increasing across a load (reset)");
        var bounded = new PerceptionRequests();
        for (ulong i = 1; i <= PerceptionRequests.MaxPending + 10; i++) bounded.TryMarkPending((i, i + 1), 0, 0, 0f, out _);
        Check(bounded.PendingCount == PerceptionRequests.MaxPending, "pending requests are bounded");
        bounded.Clear();
        Check(bounded.PendingCount == 0 && bounded.TryMarkPending((1, 2), 0, 0, 0f, out _), "clear forgets everything");

        // the last change tick (a correction applies only where no result changed at or after its capture tick)
        ulong now = 5;
        var ticks = new PerceptionLedger { Clock = () => now };
        Check(ticks.LastChangeTick == 0, "no change yet");
        ticks.TryResolve(1, 2, 0, () => 1, out _);
        Check(ticks.LastChangeTick == 5, "a roll records its tick");
        now = 7;
        ticks.TryResolve(1, 2, 0, () => 2, out _);
        Check(ticks.LastChangeTick == 5, "a read doesn't");
        ticks.CarryIntoRound1();
        Check(ticks.LastChangeTick == 7, "round 1 records its tick");
        now = 9;
        ticks.Clear();
        Check(ticks.LastChangeTick == 9, "a later round start records its tick");
        // Codex third-review repro: two computers in step, a correction captured at tick 10, a check rolled at tick 12
        // before it applies: both see the change after the capture and both reject it
        var peerA = new PerceptionLedger { Clock = () => now };
        var peerB = new PerceptionLedger { Clock = () => now };
        now = 3;
        peerA.TryResolve(1, 2, 0, () => 1, out _);
        peerB.TryResolve(1, 2, 0, () => 1, out _);
        const ulong captureTick = 10;
        Check(peerA.LastChangeTick < captureTick && peerB.LastChangeTick < captureTick,
            "no change after the capture: both apply");
        now = 12;
        peerA.TryResolve(3, 4, 0, () => 1, out _);
        peerB.TryResolve(3, 4, 0, () => 2, out _);
        Check(peerA.LastChangeTick >= captureTick && peerB.LastChangeTick >= captureTick,
            "a check rolled after the capture: both reject, whatever its result");
        var inCaptureTick = new PerceptionLedger { Clock = () => captureTick };
        inCaptureTick.TryResolve(1, 2, 0, () => 1, out _);
        Check(inCaptureTick.LastChangeTick >= captureTick, "a change in the capture tick itself rejects (safe side)");
        var restored = new PerceptionLedger { Clock = () => now };
        now = 20;
        restored.Replace(new System.Collections.Generic.Dictionary<(ulong sensor, ulong target), (int outcome, int phase)> { [(1, 2)] = (1, 0) });
        Check(restored.LastChangeTick == 20, "a restore records its tick");
        restored.ResetForLoad();
        Check(restored.LastChangeTick == 0 && restored.Count == 0, "a load starts with no change and no results");

        // a blinded sensor's results are dropped (user direction): its checks roll again; other sensors keep theirs
        now = 30;
        var blinded = new PerceptionLedger { Clock = () => now };
        blinded.TryResolve(1, 2, 3, () => 1, out _);
        blinded.TryResolve(1, 4, 3, () => 1, out _);
        blinded.TryResolve(5, 2, 3, () => 1, out _);
        now = 31;
        Check(blinded.ForgetSensor(1) && !blinded.TryGet(1, 2, out _) && !blinded.TryGet(1, 4, out _),
            "a blinded sensor loses all its results");
        Check(blinded.TryGet(5, 2, out _) && blinded.Count == 1, "other sensors keep theirs");
        Check(blinded.LastChangeTick == 31, "dropping them is a change at that tick");
        now = 32;
        Check(!blinded.ForgetSensor(1) && blinded.LastChangeTick == 32,
            "nothing to drop here: still a change at that tick (Codex review of 410803bf)");
        var rerolled = 0;
        blinded.TryResolve(1, 2, 3, () => { rerolled++; return 2; }, out var rerolledOutcome);
        Check(rerolled == 1 && rerolledOutcome == 2, "the next check rolls again");

        // blindness ends: only the failed checks are dropped (outcome 1 stands for a success here)
        var unblinded = new PerceptionLedger { Clock = () => now };
        unblinded.TryResolve(1, 2, 3, () => 1, out _);
        unblinded.TryResolve(1, 4, 3, () => 0, out _);
        Check(unblinded.ForgetSensor(1, x => x != 1) && unblinded.TryGet(1, 2, out _) && !unblinded.TryGet(1, 4, out _),
            "blindness ending keeps what it perceived and drops what it failed");
        Check(!unblinded.ForgetSensor(1, x => x != 1), "no failures left: nothing dropped");

        // Codex review of 410803bf: host success and client failure at tick 5, correction captured at 10, blindness ends
        // everywhere at 12. Both must reject the correction (the same last change tick), not only the client.
        now = 5;
        var host = new PerceptionLedger { Clock = () => now };
        var client = new PerceptionLedger { Clock = () => now };
        host.TryResolve(1, 2, 3, () => 1, out _);
        client.TryResolve(1, 2, 3, () => 0, out _);
        now = 12;
        host.ForgetSensor(1, x => x != 1);
        client.ForgetSensor(1, x => x != 1);
        Check(host.LastChangeTick == 12 && client.LastChangeTick == 12, "divergent results: both stamp the event");
        var missing = new PerceptionLedger { Clock = () => now };
        missing.ForgetSensor(1);
        Check(missing.LastChangeTick == 12, "a computer missing the entries stamps it too");

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

        // the AI's 3 s count only time spent waiting, added up over the passes; deciding doesn't use them
        var realTime = 0f;
        var budget = new AiWaitBudget(3f, () => realTime);
        realTime = 5f; // a slow first decision
        budget.Start();
        Check(budget.HasTimeLeft, "deciding time doesn't count");
        realTime = 6f;
        budget.Stop();
        realTime = 20f; // a slow second decision
        budget.Start();
        realTime = 21.9f;
        Check(budget.HasTimeLeft, "waits add up: 1 + 1.9 s");
        realTime = 22f;
        Check(!budget.HasTimeLeft, "3 s of waiting in total");
        budget.Stop();
        realTime = 40f;
        budget.Start();
        Check(!budget.HasTimeLeft, "nothing left for a later pass");
        budget.Stop();

        // a legendary package's retry starts from the earlier packages' scores and up-to-date contexts
        var scores = new System.Collections.Generic.List<int> { 9, 4 };
        var upToDate = new System.Collections.Generic.Dictionary<int, bool> { [1] = true };
        var restoreScores = AiRetryState.Keep(scores);
        var restoreUpToDate = AiRetryState.Keep(upToDate);
        scores.Add(12); // the discarded pass scores with a check still missing, then sorts and cuts the list
        scores.Sort((x, y) => y.CompareTo(x));
        scores.RemoveAt(scores.Count - 1);
        upToDate[2] = true;
        restoreScores();
        restoreUpToDate();
        Check(scores.SequenceEqual(new[] { 9, 4 }), "earlier packages' scores kept, the discarded pass's gone");
        Check(upToDate.Count == 1 && upToDate[1], "contexts the discarded pass built are built again");
        scores.Add(7);
        restoreScores();
        Check(scores.SequenceEqual(new[] { 9, 4 }), "each retry starts from the same state");
        Console.WriteLine($"{checks} checks passed");
    }
    sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
}
