using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Completed 10-cell perception checks, by directional (sensor, target) pair, each tagged with the phase it was rolled
// in: 0 outside combat, n in combat round n. Reading it never consumes randomness.
internal sealed class PerceptionLedger
{
    private const long SnapshotVersion = 2;
    private readonly Dictionary<(ulong sensor, ulong target), (int outcome, int phase)> _entries = new();
    private readonly HashSet<(ulong sensor, ulong target)> _resolving = [];

    internal int Count => _entries.Count;

    // the network tick of the last change to the results; changes happen only at synchronized points, so it is equal
    // on every computer in step
    internal ulong LastChangeTick { get; private set; }

    // the current network tick
    internal Func<ulong> Clock { get; set; } = () => 0;

    internal bool TryGet(ulong sensor, ulong target, out int outcome)
    {
        var found = _entries.TryGetValue((sensor, target), out var entry);
        outcome = entry.outcome;
        return found;
    }

    // rolls only if the pair has no result; a roll's notifications can query perception again, never roll a pair twice
    internal bool TryResolve(ulong sensor, ulong target, int phase, Func<int> roll, out int outcome)
    {
        if (TryGet(sensor, target, out outcome))
        {
            return true;
        }

        var key = (sensor, target);

        if (!_resolving.Add(key))
        {
            return false;
        }

        try
        {
            outcome = roll();
            _entries[key] = (outcome, phase);
            Changed();
            return true;
        }
        finally
        {
            _resolving.Remove(key);
        }
    }

    internal void Clear()
    {
        _entries.Clear();
        Changed();
    }

    // a load: every computer starts from the same empty results, with no change since (network ticks restart)
    internal void ResetForLoad()
    {
        _entries.Clear();
        LastChangeTick = 0;
    }

    private void Changed()
    {
        LastChangeTick = Clock();
    }

    // the sensor was blinded: its results (rolled, or seen with its senses) no longer hold, so its next checks roll
    // again; with a filter, only the results it matches (blindness ending drops only the failures). True if anything
    // was dropped.
    internal bool ForgetSensor(ulong sensor, Func<int, bool> outcomes = null)
    {
        var keys = _entries
            .Where(x => x.Key.sensor == sensor && (outcomes == null || outcomes(x.Value.outcome)))
            .Select(x => x.Key)
            .ToList();

        if (keys.Count == 0)
        {
            return false;
        }

        keys.ForEach(x => _entries.Remove(x));
        Changed();

        return true;
    }

    // round 1 of a fight: keeps the checks rolled outside combat, now as round 1 checks, so a later fight (even one
    // that ends in round 1) can't inherit them; drops everything left over from an earlier fight
    internal void CarryIntoRound1()
    {
        Changed();

        foreach (var entry in _entries.ToList())
        {
            if (entry.Value.phase == 0)
            {
                _entries[entry.Key] = (entry.Value.outcome, 1);
            }
            else
            {
                _entries.Remove(entry.Key);
            }
        }
    }

    internal long[] Capture()
    {
        var values = new List<long> { SnapshotVersion };

        foreach (var entry in _entries.OrderBy(x => x.Key.sensor).ThenBy(x => x.Key.target))
        {
            values.Add(unchecked((long)entry.Key.sensor));
            values.Add(unchecked((long)entry.Key.target));
            values.Add(entry.Value.outcome);
            values.Add(entry.Value.phase);
        }

        return values.ToArray();
    }

    // validates a whole snapshot without changing anything; null if it is invalid
    internal static Dictionary<(ulong sensor, ulong target), (int outcome, int phase)> Parse(
        long[] values, Func<int, bool> validOutcome)
    {
        if (values == null || values.Length < 1 || values[0] != SnapshotVersion || (values.Length - 1) % 4 != 0 ||
            values.Length > 1 + (4 * 65536))
        {
            return null;
        }

        var replacement = new Dictionary<(ulong sensor, ulong target), (int outcome, int phase)>();

        for (var i = 1; i < values.Length; i += 4)
        {
            var key = (unchecked((ulong)values[i]), unchecked((ulong)values[i + 1]));

            if (key.Item1 == 0 || key.Item2 == 0 ||
                values[i + 2] is < int.MinValue or > int.MaxValue || !validOutcome((int)values[i + 2]) ||
                values[i + 3] is < 0 or > int.MaxValue || replacement.ContainsKey(key))
            {
                return null;
            }

            replacement.Add(key, ((int)values[i + 2], (int)values[i + 3]));
        }

        return replacement;
    }

    internal bool CanReplace => _resolving.Count == 0;

    internal void Replace(Dictionary<(ulong sensor, ulong target), (int outcome, int phase)> replacement)
    {
        _entries.Clear();

        foreach (var entry in replacement)
        {
            _entries.Add(entry.Key, entry.Value);
        }

        Changed();
    }

    // validates the whole snapshot before replacing anything
    internal bool Restore(long[] values, Func<int, bool> validOutcome)
    {
        var replacement = CanReplace ? Parse(values, validOutcome) : null;

        if (replacement == null)
        {
            return false;
        }

        Replace(replacement);

        return true;
    }
}
