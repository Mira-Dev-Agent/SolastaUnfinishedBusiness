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
// through the class level command (same queue and tick as actions). Previews, the UI and AI planning only read results;
// a missing one is requested and reads as not perceived until the command arrives. Single player rolls as before.
internal static class PerceptionContext
{
    private const string CommandMarker = "FBPerception:";
    private const int PairsPerCommand = 64;
    private const int DiagnosticLimit = 128;
    private const int AiMaxRetries = 2;
    private const float AiMaxWaitSeconds = 5f;
    private const float RetryCooldownSeconds = 2f;

    private static readonly PerceptionLedger Ledger = new();

    // host only, AI sync off: the AI's local planning rolls, never read by real actions
    private static readonly Dictionary<(ulong, ulong), RollOutcome> LegacyAiCache = [];

    // local bookkeeping, never part of the shared state
    private static readonly HashSet<(ulong, ulong)> Pending = [];
    // when a command last handled a pair that stayed unresolved (not eligible at that moment): not requested again
    // for a moment, so a hypothetical position in a preview can't send a request every frame
    private static readonly Dictionary<(ulong, ulong), float> RecentlyAttemptedPairs = [];
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
    private static int _aiDepth;
    private static HashSet<(ulong, ulong)> _aiDependencies;
    private static int _hoverPendingFrame = -1;

    // set while the AI wrapper asks the brain for another decision, so that call isn't wrapped twice
    internal static bool CreatingAiRerun { get; private set; }

    // goes up whenever a check is requested, resolved or dropped
    internal static int Revision { get; private set; }

    // UI: true while the hover evaluated this frame (or the last) touched a check that is still pending
    internal static bool HoverPending => _hoverPendingFrame >= Time.frameCount - 1;

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
        return sensor != null && target != null && Pending.Contains((sensor.Guid, target.Guid));
    }

    // ordinary senses saw the target: kept as a success, as before (only at synchronized points in multiplayer)
    internal static void RecordVisible(GameLocationCharacter sensor, GameLocationCharacter target)
    {
        if (sensor == null || target == null || (Global.IsMultiplayer && _syncDepth == 0))
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

        var multiplayer = Global.IsMultiplayer;

        if (!multiplayer)
        {
            return Ledger.TryResolve(key.Item1, key.Item2, CurrentPhase,
                () => Roll(sensor, target, dc, advantage, "single player", false), out var local)
                ? (RollOutcome)local
                : RollOutcome.Failure;
        }

        // AI planning runs on the host alone, so it never rolls a shared check, even nested in an action step
        if (_aiDepth > 0)
        {
            if (!Main.Settings.SyncAiPerceptionChecks)
            {
                if (!LegacyAiCache.TryGetValue(key, out var legacy))
                {
                    legacy = (RollOutcome)Roll(sensor, target, dc, advantage, "legacy AI planning", false);
                    LegacyAiCache[key] = legacy;
                }

                return legacy;
            }

            if (!RecentlyAttempted(key))
            {
                _aiDependencies?.Add(key);
                Request(key, false);
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

        if (!RecentlyAttempted(key))
        {
            _hoverPendingFrame = Time.frameCount;
            Request(key, true);
        }

        return RollOutcome.Failure;
    }

    private static bool RecentlyAttempted((ulong, ulong) key)
    {
        return RecentlyAttemptedPairs.TryGetValue(key, out var time) &&
               Time.realtimeSinceStartup - time < RetryCooldownSeconds;
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

    private static void Request((ulong, ulong) key, bool player)
    {
        if (Pending.Add(key))
        {
            (player ? PlayerQueue : AiQueue).Add(key);
            Revision++;

            return;
        }

        // a player now needs a check the AI queued: it goes out with the player's requests
        if (player && AiQueue.Remove(key))
        {
            PlayerQueue.Add(key);
        }
    }

    // every frame: player requests go out at once
    internal static void FlushPlayerRequests()
    {
        Send(PlayerQueue, "player");
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
            queue.ForEach(x => Pending.Remove(x));
            queue.Clear();
            Revision++;

            return;
        }

        var phase = CurrentPhase;

        for (var i = 0; i < queue.Count; i += PairsPerCommand)
        {
            var pairs = string.Join(";", queue.Skip(i).Take(PairsPerCommand).Select(x => $"{x.Item1}-{x.Item2}"));

            networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
                networking.CommandTick, 0L, $"{CommandMarker}{phase}:{pairs}", false);
            Trace($"request origin={origin} phase={phase} pairs={pairs}");
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

    // runs on every computer at the same tick: same state in, same rolls out
    private static void ExecuteCommand(string payload)
    {
        var separator = payload.IndexOf(':');

        if (separator < 0 || !int.TryParse(payload.Substring(0, separator), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var phase))
        {
            Trace($"dropped malformed command {payload}");

            return;
        }

        var current = CurrentPhase;
        // a request from outside combat still applies in round 1 (its results carry into the fight)
        var applies = phase == current || (phase == 0 && current == 1);
        var entities = ServiceRepository.GetService<IRulesetEntityService>();

        using (new SyncScope("perception command"))
        {
            foreach (var pair in payload.Substring(separator + 1).Split(';'))
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

                var key = (sensorGuid, targetGuid);

                if (applies && !Ledger.TryGet(sensorGuid, targetGuid, out _) &&
                    Find(entities, sensorGuid) is { } sensor && Find(entities, targetGuid) is { } target)
                {
                    // rolls the check if the pair is still eligible here and now, exactly like an action would
                    sensor.CanPerceiveTarget(target);
                }

                if (!Ledger.TryGet(sensorGuid, targetGuid, out _))
                {
                    RecentlyAttemptedPairs[key] = Time.realtimeSinceStartup;
                }

                Pending.Remove(key);
            }
        }

        Revision++;
        Trace($"command phase={phase} current={current} applied={applies} pairs={payload.Substring(separator + 1)}");
    }

    private static GameLocationCharacter Find(IRulesetEntityService entities, ulong guid)
    {
        return entities != null && entities.TryGetEntityByGuid(guid, out var entity) && entity is RulesetActor actor
            ? GameLocationCharacter.GetFromActor(actor)
            : null;
    }

    #endregion

    #region AI planning

    // host, multiplayer, AI sync on: decide; if the decision needed missing checks, request them, wait, decide again
    internal static IEnumerator WrapAiDecision(IEnumerator first, Func<IEnumerator> decideAgain)
    {
        if (first == null)
        {
            yield break;
        }

        var inner = first;
        var dependencies = new HashSet<(ulong, ulong)>();

        for (var attempt = 0;; attempt++)
        {
            dependencies.Clear();

            yield return new ScopedEnumerator(inner,
                () => new AiScope(dependencies),
                child => child is not CustomYieldInstruction);

            if (!Global.IsMultiplayer || !Main.Settings.SyncAiPerceptionChecks || dependencies.Count == 0)
            {
                yield break;
            }

            if (attempt >= AiMaxRetries)
            {
                Trace($"ai decision kept after {attempt} retries with {dependencies.Count} checks unresolved");

                yield break;
            }

            Send(AiQueue, "ai");

            var deadline = Time.realtimeSinceStartup + AiMaxWaitSeconds;

            while (dependencies.Any(Pending.Contains) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Trace($"ai decision waited for {dependencies.Count} checks, deciding again (attempt {attempt + 1})");

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

    private sealed class AiScope : IDisposable
    {
        private readonly HashSet<(ulong, ulong)> _previous;

        internal AiScope(HashSet<(ulong, ulong)> dependencies)
        {
            _previous = _aiDependencies;
            _aiDependencies = dependencies;
            _aiDepth++;
        }

        public void Dispose()
        {
            _aiDepth--;
            _aiDependencies = _previous;
        }
    }

    #endregion

    #region Phases, resets and recovery

    // round 1 keeps the checks rolled outside combat; later rounds start fresh
    internal static void OnRoundStart(int round)
    {
        if (round <= 1)
        {
            Ledger.KeepOnlyOutOfCombat();
        }
        else
        {
            Ledger.Clear();
        }

        LegacyAiCache.Clear();
        RecentlyAttemptedPairs.Clear();
        Revision++;
        Trace($"round start round={round} kept={Ledger.Count}");
    }

    // a load or a location change: every computer starts empty together
    internal static void Reset(string reason)
    {
        Ledger.Clear();
        LegacyAiCache.Clear();
        Pending.Clear();
        RecentlyAttemptedPairs.Clear();
        PlayerQueue.Clear();
        AiQueue.Clear();
        Revision++;
        Trace($"reset reason={reason}");
    }

    internal static long[] Capture()
    {
        return Ledger.Capture();
    }

    internal static bool Restore(long[] snapshot)
    {
        var restored = Ledger.Restore(snapshot, value => Enum.IsDefined(typeof(RollOutcome), value));

        if (restored)
        {
            LegacyAiCache.Clear();
            Revision++;
        }

        return restored;
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
