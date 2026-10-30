using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Local bookkeeping for 10-cell perception requests: which pairs are waiting for their command, for which generation,
// and which were handled recently without a result. Pure state with explicit times, so it can be tested outside the
// game. Never part of the shared state: only the ledger is.
internal sealed class PerceptionRequests
{
    internal const int MaxPending = 1024;
    internal const float MaxPendingSeconds = 15f;
    internal const float RetryCooldownSeconds = 2f;

    private readonly Dictionary<(ulong, ulong), (int generation, float time)> _pending = [];
    private readonly Dictionary<(ulong, ulong), float> _attempted = [];

    internal int PendingCount => _pending.Count;

    // A command applies only in the generation it was sent in, or, if it was sent outside combat, in the generation
    // that started round 1 right after (outside-combat checks carry into round 1). Every computer reaches the same
    // answer: generation and phase only change at synchronized points.
    internal static bool Applies(int requestGeneration, int requestPhase, int currentGeneration, int currentPhase)
    {
        return requestGeneration == currentGeneration ||
               (requestPhase == 0 && currentPhase == 1 && requestGeneration == currentGeneration - 1);
    }

    internal bool IsPending((ulong, ulong) key)
    {
        return _pending.ContainsKey(key);
    }

    // true if the pair should be sent now: not already waiting, not handled moments ago without a result, room left
    internal bool TryMarkPending((ulong, ulong) key, int generation, float now)
    {
        if (_pending.ContainsKey(key) || RecentlyAttempted(key, now) || _pending.Count >= MaxPending)
        {
            return false;
        }

        _pending[key] = (generation, now);

        return true;
    }

    // a command for this pair ran: only clears the marker it was sent for, never a newer request's
    internal void Complete((ulong, ulong) key, int generation, bool resolved, float now)
    {
        if (_pending.TryGetValue(key, out var entry) && entry.generation == generation)
        {
            _pending.Remove(key);
        }

        if (!resolved)
        {
            _attempted[key] = now;
        }
    }

    internal bool RecentlyAttempted((ulong, ulong) key, float now)
    {
        return _attempted.TryGetValue(key, out var time) && now - time < RetryCooldownSeconds;
    }

    // drops markers whose command never came and expired cooldowns, so nothing grows or blocks forever
    internal void Prune(float now)
    {
        foreach (var key in _pending.Where(x => now - x.Value.time >= MaxPendingSeconds).Select(x => x.Key).ToList())
        {
            _pending.Remove(key);
        }

        foreach (var key in _attempted.Where(x => now - x.Value >= RetryCooldownSeconds).Select(x => x.Key).ToList())
        {
            _attempted.Remove(key);
        }
    }

    internal void Clear()
    {
        _pending.Clear();
        _attempted.Clear();
    }

    internal void ClearCooldowns()
    {
        _attempted.Clear();
    }
}
