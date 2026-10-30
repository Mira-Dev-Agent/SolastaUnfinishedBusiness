using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using TA;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// The 10-cell perception roll (EnableChanceToPerceiveCloseRange) in multiplayer: every computer must roll the same
// checks in the same order. A check is rolled only at a synchronized point: an action step, or a perception command sent
// through the class level command. In multiplayer both run only in fixed steps (NetworkingDefinitions.CanUpdate is
// Time.inFixedTimeStep; NetworkingManager.FixedUpdate runs the delayed commands), so they interleave the same way on
// every computer. Previews, the UI and AI planning only read results; a missing one is requested and reads as not
// perceived until its command runs. Single player rolls as before.
internal static class PerceptionContext
{
    private const string CommandMarker = "FBPerception:";
    private const int PairsPerCommand = 64;
    private const int DiagnosticLimit = 128;
    private const int AiMaxDecisions = 3; // decisions that may ask for new checks, before a last one that uses them
    private const float AiMaxWaitSeconds = 3f; // user direction: then the host rolls what is still missing itself
    private const ulong AiBatchTicks = 2; // network ticks of 50 ms: the AI's requests go out in windows of about 100 ms
    private const float PruneSeconds = 1f;
    private const float HoldMaxSeconds = 10f; // a correction that never applies can't hold requests for longer

    // the cursor's hover runs with this deterministic lock (CursorManager.ProcessPointerPosition)
    private const DeterministicRandom.DeterministicLockFlag CursorLock = (DeterministicRandom.DeterministicLockFlag)1;

    private static readonly PerceptionLedger Ledger = new() { Clock = () => CurrentTick };
    private static readonly PerceptionRequests Requests = new();

    // host only: the AI's local planning rolls (sync off, or a decision's last pass), never read by real actions
    private static readonly Dictionary<(ulong, ulong), RollOutcome> LegacyAiCache = [];

    private static readonly List<((ulong, ulong) key, PerceptionRequests.Entry entry)> PlayerQueue = [];
    private static readonly List<((ulong, ulong) key, PerceptionRequests.Entry entry)> AiQueue = [];
    private static readonly Queue<string> Diagnostics = new();

    private static readonly FieldInfo[] RandomFields = typeof(UnityEngine.Random.State)
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .OrderBy(x => x.Name)
        .ToArray();

    private static int _emitted;
    private static int _syncDepth;
    private static string _syncContext;
    private static AiScope _ai;
    private static ulong _aiQueuedTick;
    private static float _nextPrune;
    private static int _hoverPendingFrame = -1;
    private static ulong _holdUntilTick;
    private static float _holdUntilTime;

    // changes only at synchronized points (a load, a location change, a round start), so it is equal on every computer
    private static int _generation;

    // set while the AI wrapper asks the brain for another decision, so that call isn't wrapped twice
    internal static bool CreatingAiRerun { get; private set; }

    // UI: true while the cursor's hover this frame (or the last) touched a check that is still pending
    internal static bool HoverPending => _hoverPendingFrame >= Time.frameCount - 1;

    // the network tick of the last change to the shared results (equal on every computer in step)
    internal static ulong LastChangeTick => Ledger.LastChangeTick;

    // while a desync correction is on its way, new requests wait in their queues, so their checks can't roll between
    // its capture and its tick (requests already sent still run; the correction rejects itself if one changes a result)
    private static bool Held => CurrentTick <= _holdUntilTick && Time.realtimeSinceStartup < _holdUntilTime;

    // nothing requested and nothing waiting: a desync correction can be captured or applied safely
    internal static bool IsIdle => Requests.PendingCount == 0 && PlayerQueue.Count == 0 && AiQueue.Count == 0;

    private static int CurrentPhase =>
        ServiceRepository.GetService<IGameLocationBattleService>()?.Battle?.CurrentRound ?? 0;

    #region Queries

    internal static bool TryGetResolved(
        GameLocationCharacter sensor, GameLocationCharacter target, out RollOutcome outcome)
    {
        outcome = RollOutcome.Failure;

        if (sensor == null || target == null || !Ledger.TryGet(sensor.Guid, target.Guid, out var value))
        {
            return false;
        }

        outcome = (RollOutcome)value;

        return true;
    }

    internal static bool IsPending(GameLocationCharacter sensor, GameLocationCharacter target)
    {
        return sensor != null && target != null && Requests.IsPending((sensor.Guid, target.Guid));
    }

    // ordinary senses saw the target: kept as a success, as before (in multiplayer only at synchronized points, and never
    // from AI planning, which runs on the host alone)
    internal static void RecordVisible(GameLocationCharacter sensor, GameLocationCharacter target)
    {
        if (sensor == null || target == null ||
            (Global.IsMultiplayer && (_ai != null || _syncDepth == 0)))
        {
            return;
        }

        Ledger.TryResolve(sensor.Guid, target.Guid, CurrentPhase, () => (int)RollOutcome.Success, out _);
    }

    // the 10-cell roll, called by MyIsCellPerceivedByCharacter once ordinary senses failed and the pair is eligible
    internal static RollOutcome Resolve(
        GameLocationCharacter sensor, GameLocationCharacter target, int dc, AdvantageType advantage)
    {
        var key = (sensor.Guid, target.Guid);

        if (Ledger.TryGet(key.Item1, key.Item2, out var cached))
        {
            return (RollOutcome)cached;
        }

        if (!Global.IsMultiplayer)
        {
            return Ledger.TryResolve(key.Item1, key.Item2, CurrentPhase,
                () => Roll(sensor, target, dc, advantage, "single player", false), out var local)
                ? (RollOutcome)local
                : RollOutcome.Failure;
        }

        // AI planning runs on the host alone, so it never rolls a shared check, even nested in an action step. With
        // sync off, and in the last pass after the wait's time limit (user direction), it rolls missing checks locally
        // as before: those rolls happen on the host alone, so they can desync.
        if (_ai != null)
        {
            if (!_ai.Synchronized || _ai.LocalRolls)
            {
                // sync off: a shared check requested before the setting changed is still coming, don't also roll it
                if (!_ai.Synchronized && Requests.IsPending(key))
                {
                    return RollOutcome.Failure;
                }

                if (!LegacyAiCache.TryGetValue(key, out var legacy))
                {
                    legacy = (RollOutcome)Roll(sensor, target, dc, advantage, "legacy AI planning", false);
                    LegacyAiCache[key] = legacy;
                }

                return legacy;
            }

            if (_ai.Collecting && (Requests.IsPending(key) || Request(key, false)))
            {
                _ai.Dependencies.Add(key);
            }

            return RollOutcome.Failure;
        }

        if (_syncDepth > 0)
        {
            return Ledger.TryResolve(key.Item1, key.Item2, CurrentPhase,
                () => Roll(sensor, target, dc, advantage, _syncContext, true), out var shared)
                ? (RollOutcome)shared
                : RollOutcome.Failure;
        }

        // previews and the UI: request it (or hurry along the AI's request for it); only the cursor's own hover shows
        // the pending note
        var waiting = Requests.IsPending(key) ? PromoteToPlayer(key) : Request(key, true);

        if (waiting && DeterministicRandom.lockFlags.Contains(CursorLock))
        {
            _hoverPendingFrame = Time.frameCount;
        }

        return RollOutcome.Failure;
    }

    #endregion

    #region Rolling

    private static int Roll(GameLocationCharacter sensor, GameLocationCharacter target,
        int dc, AdvantageType advantage, string context, bool synchronized)
    {
        // the AI holds a deterministic lock across frames on the host only; a synchronized roll must use the saved
        // random state on every computer, so the lock is lifted for this roll alone
        var locks = synchronized && DeterministicRandom.lockFlags.Count > 0
            ? DeterministicRandom.lockFlags.ToList()
            : null;

        locks?.ForEach(x => DeterministicRandom.lockFlags.Remove(x));

        try
        {
            var before = RandomHash();
            var conditions = sensor.RulesetActor.AllConditions.Select(x => x.Guid).ToArray();

            sensor.RollAbilityCheck(AttributeDefinitions.Wisdom, SkillDefinitions.Perception, dc, advantage,
                new ActionModifier(), false, 0, out _, out _, out _, out _, out var outcome, out _, true, true, true);

            var remaining = new HashSet<ulong>(sensor.RulesetActor.AllConditions.Select(x => x.Guid));
            var removed = string.Join(",", conditions.Where(x => !remaining.Contains(x)));

            Trace($"roll context={context} sensor={sensor.Guid}@{sensor.LocationPosition} " +
                  $"target={target.Guid}@{target.LocationPosition} dc={dc} advantage={advantage} outcome={outcome} " +
                  $"rng={before}/{RandomHash()} liftedLocks={locks?.Count ?? 0} removedConditions=[{removed}]");

            return (int)outcome;
        }
        finally
        {
            locks?.ForEach(x => DeterministicRandom.lockFlags.Add(x));
        }
    }

    internal static IEnumerator WrapAction(IEnumerator inner, CharacterAction action)
    {
        return inner == null
            ? null
            : new ScopedEnumerator(inner,
                () => new SyncScope($"{action.ActionId}:{action.ActingCharacter?.Guid}"),
                child => child is not CustomYieldInstruction);
    }

    private sealed class SyncScope : IDisposable
    {
        private readonly string _previous;

        internal SyncScope(string context)
        {
            _previous = _syncContext;
            _syncContext = context;
            _syncDepth++;
        }

        public void Dispose()
        {
            _syncDepth--;
            _syncContext = _previous;
        }
    }

    #endregion

    #region Requests and the perception command

    // true if the pair was queued now (not already waiting, not tried moments ago without a result)
    private static bool Request((ulong, ulong) key, bool player)
    {
        if (!Requests.TryMarkPending(key, _generation, CurrentPhase, Time.realtimeSinceStartup, out var entry))
        {
            return false;
        }

        if (player)
        {
            PlayerQueue.Add((key, entry));
        }
        else
        {
            if (AiQueue.Count == 0)
            {
                _aiQueuedTick = CurrentTick;
            }

            AiQueue.Add((key, entry));
        }

        return true;
    }

    // a player needs a check the AI queued: it goes out with the player's requests (unchanged otherwise); always true,
    // since the pair is pending either way
    private static bool PromoteToPlayer((ulong, ulong) key)
    {
        var index = AiQueue.FindIndex(x => x.key.Equals(key));

        if (index >= 0)
        {
            PlayerQueue.Add(AiQueue[index]);
            AiQueue.RemoveAt(index);
        }

        return true;
    }

    // every frame: player requests go out at once; the AI's after a short window of network ticks (and when a decision
    // ends)
    internal static void FlushRequests()
    {
        var now = Time.realtimeSinceStartup;

        Send(PlayerQueue, "player");

        if (AiQueue.Count > 0 && CurrentTick - _aiQueuedTick >= AiBatchTicks)
        {
            Send(AiQueue, "ai");
        }

        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + PruneSeconds;
        Requests.Prune(now);
    }

    private static ulong CurrentTick =>
        (ServiceRepository.GetService<INetworkingService>() as NetworkingManager)?.tick?.LocalTick ?? 0;

    // each request goes out with the generation and phase it was made in; a command carries its sender and the
    // request ids, so a computer only clears its own requests
    private static void Send(List<((ulong, ulong) key, PerceptionRequests.Entry entry)> queue, string origin)
    {
        if (queue.Count == 0 || Held)
        {
            return;
        }

        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            // no network to send through: forget the requests rather than wait for an answer that cannot come
            queue.ForEach(x => Requests.CompleteOwn(x.key, x.entry.Id, false, Time.realtimeSinceStartup));
            queue.Clear();

            return;
        }

        var sender = PhotonNetwork.LocalPlayer?.ActorNumber ?? 0;

        foreach (var group in queue.GroupBy(x => (x.entry.Generation, x.entry.Phase)))
        {
            var items = group.ToList();

            for (var i = 0; i < items.Count; i += PairsPerCommand)
            {
                var pairs = string.Join(";", items.Skip(i).Take(PairsPerCommand)
                    .Select(x => $"{x.key.Item1}-{x.key.Item2}-{x.entry.Id}"));

                networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
                    networking.CommandTick, 0L,
                    $"{CommandMarker}{group.Key.Generation}:{group.Key.Phase}:{sender}:{pairs}", false);
                Trace($"request origin={origin} generation={group.Key.Generation} phase={group.Key.Phase} " +
                      $"pairs={pairs}");
            }
        }

        PhotonNetwork.SendAllOutgoingCommands();
        queue.Clear();
    }

    // replaces the vanilla class level coroutine when the class name is the perception marker
    internal static bool TryHandleCommand(string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null || !classDefinitionName.StartsWith(CommandMarker))
        {
            return false;
        }

        result = HandleCommand(classDefinitionName.Substring(CommandMarker.Length));

        return true;
    }

    private static IEnumerator HandleCommand(string payload)
    {
        ExecuteCommand(payload);

        yield break;
    }

    // runs on every computer in the same fixed step: same state in, same rolls out
    private static void ExecuteCommand(string payload)
    {
        var parts = payload.Split([':'], 4);

        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var phase) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sender))
        {
            Trace($"dropped malformed command {payload}");

            return;
        }

        var currentPhase = CurrentPhase;
        var applies = PerceptionRequests.Applies(generation, phase, _generation, currentPhase);
        var entities = ServiceRepository.GetService<IRulesetEntityService>();
        var own = sender == (PhotonNetwork.LocalPlayer?.ActorNumber ?? 0);
        var now = Time.realtimeSinceStartup;

        using (new SyncScope("perception command"))
        {
            foreach (var pair in parts[3].Split(';'))
            {
                var fields = pair.Split('-');

                if (fields.Length != 3 ||
                    !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sensorGuid) ||
                    !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var targetGuid) ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                var key = (sensorGuid, targetGuid);

                if (applies && !Ledger.TryGet(sensorGuid, targetGuid, out _) &&
                    Find(entities, sensorGuid) is { } sensor && Find(entities, targetGuid) is { } target)
                {
                    // rolls the check if the pair is still eligible in this step, exactly like an action would
                    sensor.CanPerceiveTarget(target);
                }

                if (Ledger.TryGet(sensorGuid, targetGuid, out _))
                {
                    Requests.Resolved(key);
                }
                else if (own)
                {
                    // only this request: a pair tried here without a result (not eligible in this step) isn't
                    // requested again for a moment; an out-of-date command didn't try it
                    Requests.CompleteOwn(key, id, !applies, now);
                }
            }
        }

        Trace($"command generation={generation}/{_generation} phase={phase}/{currentPhase} sender={sender} " +
              $"applied={applies} pairs={parts[3]}");
    }

    private static GameLocationCharacter Find(IRulesetEntityService entities, ulong guid)
    {
        return entities != null && entities.TryGetEntityByGuid(guid, out var entity) && entity is RulesetActor actor
            ? GameLocationCharacter.GetFromActor(actor)
            : null;
    }

    #endregion

    #region AI planning

    // host, multiplayer, AI sync on: decide; if the decision needed missing checks, send them, wait until every one has
    // its command, and decide again with the results. After three decisions that each found new missing checks, one
    // last decision uses the results that arrived without asking for more (a check still missing counts as failed).
    // After 3 s of waiting (a lost command can't stall the enemy turn), the last decision instead rolls any check still
    // missing on the host alone, as with sync off (user direction): those rolls can desync. The mode is fixed for the
    // whole decision.
    internal static IEnumerator WrapAiDecision(IEnumerator first, Func<IEnumerator> decideAgain)
    {
        if (first == null)
        {
            yield break;
        }

        var synchronized = Global.IsMultiplayer && Main.Settings.SyncAiPerceptionChecks;
        var deadline = Time.realtimeSinceStartup + AiMaxWaitSeconds;
        var inner = first;
        var passesWithNewChecks = 0;
        var finalPass = false;
        var localRolls = false;

        while (true)
        {
            var scope = new AiScope(synchronized, !finalPass, localRolls);

            yield return new ScopedEnumerator(inner, () => scope.Enter(), child => child is not CustomYieldInstruction);

            if (finalPass || scope.Dependencies.Count == 0)
            {
                yield break;
            }

            passesWithNewChecks++;
            Send(AiQueue, "ai");

            while (scope.Dependencies.Any(Requests.IsPending) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            var waiting = scope.Dependencies.Count(Requests.IsPending);

            localRolls = waiting > 0;
            finalPass = localRolls || passesWithNewChecks >= AiMaxDecisions;
            Trace(localRolls
                ? $"ai wait reached the time limit with {waiting} of {scope.Dependencies.Count} checks still waiting " +
                  "for their command, deciding again with local rolls (last pass)"
                : $"ai decision has the results of {scope.Dependencies.Count} checks, deciding again" +
                  (finalPass ? " (last pass)" : string.Empty));

            CreatingAiRerun = true;

            try
            {
                inner = decideAgain();
            }
            finally
            {
                CreatingAiRerun = false;
            }

            if (inner == null)
            {
                yield break;
            }
        }
    }

    // marks AI planning for the duration of each step of one decision
    private sealed class AiScope(bool synchronized, bool collecting, bool localRolls = false)
    {
        internal bool Synchronized { get; } = synchronized;
        internal bool Collecting { get; } = collecting;
        internal bool LocalRolls { get; } = localRolls;
        internal HashSet<(ulong, ulong)> Dependencies { get; } = [];

        internal IDisposable Enter()
        {
            return new Entered(this);
        }

        private sealed class Entered : IDisposable
        {
            private readonly AiScope _previous;

            internal Entered(AiScope scope)
            {
                _previous = _ai;
                _ai = scope;
            }

            public void Dispose()
            {
                _ai = _previous;
            }
        }
    }

    #endregion

    #region Phases, resets and recovery

    // round 1 keeps the checks rolled outside combat (as round 1 checks); later rounds start fresh
    internal static void OnRoundStart(int round)
    {
        if (round <= 1)
        {
            Ledger.CarryIntoRound1();
        }
        else
        {
            Ledger.Clear();
        }

        _generation++;
        LegacyAiCache.Clear();
        Requests.ClearCooldowns();
        Trace($"round start round={round} generation={_generation} kept={Ledger.Count}");
    }

    // a desync correction captured now or received now applies at this tick: new requests wait until it has passed
    internal static void HoldRequests(ulong applyTick)
    {
        if (applyTick > _holdUntilTick || !Held)
        {
            _holdUntilTick = applyTick;
        }

        _holdUntilTime = Time.realtimeSinceStartup + HoldMaxSeconds;
        Trace($"requests held until tick {_holdUntilTick}");
    }

    // a load or a location change: every computer starts empty together
    internal static void Reset(string reason)
    {
        Ledger.ResetForLoad();
        _holdUntilTick = 0;
        _holdUntilTime = 0f;
        LegacyAiCache.Clear();
        Requests.Clear();
        PlayerQueue.Clear();
        AiQueue.Clear();
        _generation = 0;
        Trace($"reset reason={reason}");
    }

    internal static long[] Capture()
    {
        return Ledger.Capture();
    }

    // a correction validates everything before changing anything: this only checks the snapshot
    internal static bool CanRestore(long[] snapshot)
    {
        return Ledger.CanReplace &&
               PerceptionLedger.Parse(snapshot, value => Enum.IsDefined(typeof(RollOutcome), value)) != null;
    }

    // then replaces the results and forgets local work tied to the old ones
    internal static void Restore(long[] snapshot)
    {
        var replacement = PerceptionLedger.Parse(snapshot, value => Enum.IsDefined(typeof(RollOutcome), value));

        if (replacement == null || !Ledger.CanReplace)
        {
            return;
        }

        Ledger.Replace(replacement);
        LegacyAiCache.Clear();
        Requests.Clear();
        PlayerQueue.Clear();
        AiQueue.Clear();
        Trace($"restored by a desync correction entries={Ledger.Count}");
    }

    internal static long Digest()
    {
        var hash = unchecked((long)14695981039346656037UL);

        foreach (var value in Capture())
        {
            hash = unchecked((hash ^ value) * 1099511628211L);
        }

        return hash;
    }

    #endregion

    #region Diagnostics

    private static long RandomHash()
    {
        if (ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return 0;
        }

        object state = game.randomState;
        var hash = 17L;

        foreach (var field in RandomFields)
        {
            hash = unchecked((hash * 31) + (int)field.GetValue(state));
        }

        return hash;
    }

    private static void Trace(string text)
    {
        var tick = (ServiceRepository.GetService<INetworkingService>() as NetworkingManager)?.tick?.LocalTick ?? 0;
        var line = $"[Perception] peer={PhotonNetwork.LocalPlayer?.ActorNumber ?? 0} " +
                   $"host={PhotonNetwork.IsMasterClient} tick={tick} phase={CurrentPhase} {text}";

        if (Diagnostics.Count == DiagnosticLimit)
        {
            Diagnostics.Dequeue();
        }

        Diagnostics.Enqueue(line);

        // a fixed budget per launch, so a long session can't fill the log
        if (_emitted++ < DiagnosticLimit)
        {
            Main.Info(line);
        }
    }

    // TEMPORARY [PerceptionRule] diagnostic (issues #1 and #2, 2026-10-30): why the 10-cell roll was or wasn't
    // reached between foes, and why the game's attack check rejected a target. Each sensor, target and reason is logged
    // once per launch, at most RuleDiagnosticLimit lines. Remove once the question is answered.
    private const int RuleDiagnosticLimit = 300;
    private static readonly HashSet<(ulong, ulong, string)> RuleDiagnosticsSeen = [];

    internal static bool TracesRule(GameLocationCharacter sensor, GameLocationCharacter target)
    {
        return Main.Settings.EnableChanceToPerceiveCloseRange && sensor != null && target != null &&
               sensor.IsOppositeSide(target.Side) && RuleDiagnosticsSeen.Count < RuleDiagnosticLimit;
    }

    internal static void TraceRule(
        GameLocationCharacter sensor, GameLocationCharacter target, string reason, string detail)
    {
        if (!TracesRule(sensor, target) || !RuleDiagnosticsSeen.Add((sensor.Guid, target.Guid, reason)))
        {
            return;
        }

        var conditions = string.Join(",",
            sensor.RulesetActor?.AllConditions.Select(x => x.ConditionDefinition?.Name) ?? []);

        Main.Info($"[PerceptionRule] {reason} sensor={sensor.Name}#{sensor.Guid}@{sensor.LocationPosition} " +
                  $"target={target.Name}#{target.Guid}@{target.LocationPosition} {detail} sensorConditions=[{conditions}]");
    }

    // the game's attack check rejected the target (the mod's lighting rules and the 10-cell roll then never run)
    internal static void TraceAttackRejected(BattleDefinitions.AttackEvaluationParams attackParams)
    {
        var attacker = attackParams.attacker;
        var defender = attackParams.defender;

        if (!TracesRule(attacker, defender))
        {
            return;
        }

        var failures = string.Join(",", attackParams.attackModifier?.FailureFlags ?? []);

        TraceRule(attacker, defender, $"attack-rejected[{failures}]",
            $"distance={int3.Distance(attacker.LocationPosition, defender.LocationPosition):0.0} " +
            $"inPerceivedFoes={attacker.PerceivedFoes.Contains(defender)} " +
            $"lineOfSight={attacker.LineOfSightRatio.ContainsKey(defender)}");
    }

    internal static string[] RecentDiagnostics()
    {
        return Diagnostics.ToArray();
    }

    #endregion
}
