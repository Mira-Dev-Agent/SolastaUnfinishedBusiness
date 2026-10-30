using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Local bookkeeping for 10-cell perception requests: which pairs are waiting for their command, under which request
// (an id unique on this computer, never reused), generation and phase, and which were tried recently without a result.
// Pure state with explicit times, so it can be tested outside the game. Never part of the shared state: only the ledger
// is. Commands queued before a load or a location start can't arrive after it: the game empties its delayed command
// list then (NetworkingManager.ResetTick, from StartLocationRPC and GameStateChanged).
internal sealed class PerceptionRequests
{
    internal const int MaxPending = 1024;
    internal const float MaxPendingSeconds = 15f;
    internal const float RetryCooldownSeconds = 2f;

    private readonly Dictionary<(ulong, ulong), Entry> _pending = [];
    private readonly Dictionary<(ulong, ulong), float> _attempted = [];
    private long _lastId;

    internal int PendingCount => _pending.Count;

    // A command applies only in the generation its request was made in, or, if it was made outside combat, in the
    // generation that started round 1 right after (outside-combat checks carry into round 1). Every computer reaches
    // the same answer: generation and phase only change at synchronized points.
    internal static bool Applies(int requestGeneration, int requestPhase, int currentGeneration, int currentPhase)
    {
        return requestGeneration == currentGeneration ||
               (requestPhase == 0 && currentPhase == 1 && requestGeneration == currentGeneration - 1);
    }

    internal bool IsPending((ulong, ulong) key)
    {
        return _pending.ContainsKey(key);
    }

    internal bool TryGetPending((ulong, ulong) key, out Entry entry)
    {
        return _pending.TryGetValue(key, out entry);
    }

    // a new request for the pair, unless it is already waiting, was tried moments ago without a result, or there is no
    // room; the entry remembers its own id, generation and phase, which its command carries
    internal bool TryMarkPending((ulong, ulong) key, int generation, int phase, float now, out Entry entry)
    {
        entry = default;

        if (_pending.ContainsKey(key) || RecentlyAttempted(key, now) || _pending.Count >= MaxPending)
        {
            return false;
        }

        entry = new Entry(++_lastId, generation, phase, now);
        _pending[key] = entry;

        return true;
    }

    // this computer's command for the request ran: clears only that request, never a later one for the same pair
    internal void CompleteOwn((ulong, ulong) key, long id, bool resolved, float now)
    {
        if (!_pending.TryGetValue(key, out var entry) || entry.Id != id)
        {
            return;
        }

        _pending.Remove(key);

        if (!resolved)
        {
            _attempted[key] = now;
        }
    }

    // the pair has a result (from any computer's command or an action): nothing left to wait for
    internal void Resolved((ulong, ulong) key)
    {
        _pending.Remove(key);
    }

    internal bool RecentlyAttempted((ulong, ulong) key, float now)
    {
        return _attempted.TryGetValue(key, out var time) && now - time < RetryCooldownSeconds;
    }

    // drops requests whose command never came and expired cooldowns, so nothing grows or blocks forever
    internal void Prune(float now)
    {
        foreach (var key in _pending.Where(x => now - x.Value.Time >= MaxPendingSeconds).Select(x => x.Key).ToList())
        {
            _pending.Remove(key);
        }

        foreach (var key in _attempted.Where(x => now - x.Value >= RetryCooldownSeconds).Select(x => x.Key).ToList())
        {
            _attempted.Remove(key);
        }
    }

    // ids keep increasing across clears, so a request is never confused with an earlier one
    internal void Clear()
    {
        _pending.Clear();
        _attempted.Clear();
    }

    internal void ClearCooldowns()
    {
        _attempted.Clear();
    }

    internal readonly struct Entry(long id, int generation, int phase, float time)
    {
        internal long Id { get; } = id;
        internal int Generation { get; } = generation;
        internal int Phase { get; } = phase;
        internal float Time { get; } = time;
    }
}
