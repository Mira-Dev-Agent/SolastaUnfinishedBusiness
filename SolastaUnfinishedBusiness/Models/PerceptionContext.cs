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
    private const int AiMaxDecisions = 3;
    private const float AiMaxWaitSeconds = 8f;
    private const float AiBatchSeconds = 0.1f;
    private const float PruneSeconds = 1f;

    // the cursor's hover runs with this deterministic lock (CursorManager.ProcessPointerPosition)
    private const DeterministicRandom.DeterministicLockFlag CursorLock = (DeterministicRandom.DeterministicLockFlag)1;

    private static readonly PerceptionLedger Ledger = new();
    private static readonly PerceptionRequests Requests = new();

    // host only, AI sync off: the AI's local planning rolls, never read by real actions
    private static readonly Dictionary<(ulong, ulong), RollOutcome> LegacyAiCache = [];

    private static readonly List<(ulong, ulong)> PlayerQueue = [];
    private static readonly List<(ulong, ulong)> AiQueue = [];
    private static readonly Queue<string> Diagnostics = new();

    private static readonly FieldInfo[] RandomFields = typeof(UnityEngine.Random.State)
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .OrderBy(x => x.Name)
        .ToArray();

    private static int _emitted;
    private static int _syncDepth;
    private static string _syncContext;
    private static AiScope _ai;
    private static float _aiQueuedAt;
    private static float _nextPrune;
    private static int _hoverPendingFrame = -1;

    // changes only at synchronized points (a load, a location change, a round start), so it is equal on every computer
    private static int _generation;

    // set while the AI wrapper asks the brain for another decision, so that call isn't wrapped twice
    internal static bool CreatingAiRerun { get; private set; }

    // UI: true while the cursor's hover this frame (or the last) touched a check that is still pending
    internal static bool HoverPending => _hoverPendingFrame >= Time.frameCount - 1;

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

        // AI planning runs on the host alone, so it never rolls a shared check, even nested in an action step
        if (_ai != null)
        {
            if (!_ai.Synchronized)
            {
                if (!LegacyAiCache.TryGetValue(key, out var legacy))
                {
                    legacy = (RollOutcome)Roll(sensor, target, dc, advantage, "legacy AI planning", false);
                    LegacyAiCache[key] = legacy;
                }

                return legacy;
            }

            if (Requests.IsPending(key) || Request(key, false))
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

        // previews and the UI: request it; only the cursor's own hover shows the pending note
        if ((Requests.IsPending(key) || Request(key, true)) && DeterministicRandom.lockFlags.Contains(CursorLock))
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

    // true if the pair was queued now (not already waiting, not handled moments ago without a result)
    private static bool Request((ulong, ulong) key, bool player)
    {
        if (!Requests.TryMarkPending(key, _generation, Time.realtimeSinceStartup))
        {
            // a player now needs a check the AI queued: it goes out with the player's requests
            if (player && AiQueue.Remove(key))
            {
                PlayerQueue.Add(key);
            }

            return false;
        }

        if (player)
        {
            PlayerQueue.Add(key);
        }
        else
        {
            if (AiQueue.Count == 0)
            {
                _aiQueuedAt = Time.realtimeSinceStartup;
            }

            AiQueue.Add(key);
        }

        return true;
    }

    // every frame: player requests go out at once; the AI's go out in windows of about 100 ms (and when a decision ends)
    internal static void FlushRequests()
    {
        var now = Time.realtimeSinceStartup;

        Send(PlayerQueue, "player");

        if (AiQueue.Count > 0 && now - _aiQueuedAt >= AiBatchSeconds)
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

    private static void Send(List<(ulong, ulong)> queue, string origin)
    {
        if (queue.Count == 0)
        {
            return;
        }

        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            // no network to send through: forget the requests rather than wait for an answer that cannot come
            queue.ForEach(x => Requests.Complete(x, _generation, false, Time.realtimeSinceStartup));
            queue.Clear();

            return;
        }

        var phase = CurrentPhase;

        for (var i = 0; i < queue.Count; i += PairsPerCommand)
        {
            var pairs = string.Join(";", queue.Skip(i).Take(PairsPerCommand).Select(x => $"{x.Item1}-{x.Item2}"));

            networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
                networking.CommandTick, 0L, $"{CommandMarker}{_generation}:{phase}:{pairs}", false);
            Trace($"request origin={origin} generation={_generation} phase={phase} pairs={pairs}");
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
        var parts = payload.Split([':'], 3);

        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var phase))
        {
            Trace($"dropped malformed command {payload}");

            return;
        }

        var currentPhase = CurrentPhase;
        var applies = PerceptionRequests.Applies(generation, phase, _generation, currentPhase);
        var entities = ServiceRepository.GetService<IRulesetEntityService>();
        var now = Time.realtimeSinceStartup;

        using (new SyncScope("perception command"))
        {
            foreach (var pair in parts[2].Split(';'))
            {
                var dash = pair.IndexOf('-');

                if (dash < 0 ||
                    !ulong.TryParse(pair.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var sensorGuid) ||
                    !ulong.TryParse(pair.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var targetGuid))
                {
                    continue;
                }

                if (applies && !Ledger.TryGet(sensorGuid, targetGuid, out _) &&
                    Find(entities, sensorGuid) is { } sensor && Find(entities, targetGuid) is { } target)
                {
                    // rolls the check if the pair is still eligible in this step, exactly like an action would
                    sensor.CanPerceiveTarget(target);
                }

                // only clears the marker this command was sent for; a newer request for the pair keeps waiting. A pair
                // tried here without a result (not eligible in this step) isn't requested again for a moment.
                Requests.Complete((sensorGuid, targetGuid), generation,
                    !applies || Ledger.TryGet(sensorGuid, targetGuid, out _), now);
            }
        }

        Trace($"command generation={generation}/{_generation} phase={phase}/{currentPhase} applied={applies} " +
              $"pairs={parts[2]}");
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
    // its command, and decide again with the results. The mode is fixed for the whole decision, so changing the setting
    // mid-way can't strand anything; pending AI requests are always sent. Bounded so a lost command can't stall the AI:
    // after about 8 s or three decisions it keeps the last one, logging the checks it still lacked.
    internal static IEnumerator WrapAiDecision(IEnumerator first, Func<IEnumerator> decideAgain)
    {
        if (first == null)
        {
            yield break;
        }

        var synchronized = Global.IsMultiplayer && Main.Settings.SyncAiPerceptionChecks;
        var deadline = Time.realtimeSinceStartup + AiMaxWaitSeconds;
        var inner = first;

        for (var decision = 1;; decision++)
        {
            var scope = new AiScope(synchronized);

            yield return new ScopedEnumerator(inner, () => scope.Enter(), child => child is not CustomYieldInstruction);

            if (scope.Dependencies.Count == 0)
            {
                yield break;
            }

            Send(AiQueue, "ai");

            while (scope.Dependencies.Any(Requests.IsPending) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            var unresolved = scope.Dependencies.Count(x => !Ledger.TryGet(x.Item1, x.Item2, out _));

            if (decision >= AiMaxDecisions || Time.realtimeSinceStartup >= deadline)
            {
                Trace($"ai decision kept after {decision} decisions; {unresolved} of " +
                      $"{scope.Dependencies.Count} checks still without a result");

                yield break;
            }

            Trace($"ai decision waited for {scope.Dependencies.Count} checks ({unresolved} without a result), " +
                  $"deciding again ({decision + 1})");

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
    private sealed class AiScope(bool synchronized)
    {
        internal bool Synchronized { get; } = synchronized;
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

    // a load or a location change: every computer starts empty together
    internal static void Reset(string reason)
    {
        Ledger.Clear();
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

    internal static string[] RecentDiagnostics()
    {
        return Diagnostics.ToArray();
    }

    #endregion
}
