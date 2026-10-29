using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Limits retained reports independently of packet order and the sender's claimed tick.
internal sealed class BoundedTickReports<T>(int capacity, ulong window)
{
    private readonly SortedDictionary<ulong, T> _reports = [];
    internal int Count => _reports.Count;

    internal bool Add(ulong tick, ulong localTick, T report)
    {
        if (capacity <= 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        Prune(localTick);
        if ((tick > localTick ? tick - localTick : localTick - tick) > window || _reports.ContainsKey(tick))
        {
            return false;
        }
        _reports.Add(tick, report);
        while (_reports.Count > capacity) { _reports.Remove(_reports.Keys.First()); }
        return true;
    }

    internal bool Take(ulong tick, out T report)
    {
        if (!_reports.TryGetValue(tick, out report)) { return false; }
        _reports.Remove(tick);
        return true;
    }

    internal void Prune(ulong localTick)
    {
        foreach (var tick in _reports.Keys.Where(t => t < localTick && localTick - t > window).ToArray())
        {
            _reports.Remove(tick);
        }
    }
}
