using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

internal sealed class CoordinatedTickBarrier
{
    private readonly HashSet<int> _roster;
    private readonly HashSet<int> _arrived = [];
    internal long Tick { get; }
    internal string Id { get; }
    internal bool Released { get; private set; }
    internal bool AllArrived => _roster.SetEquals(_arrived);

    internal CoordinatedTickBarrier(string id, long tick, int[] roster)
    {
        if (string.IsNullOrEmpty(id) || tick < 0 || roster.Length == 0 || roster.Any(x => x <= 0) ||
            roster.Distinct().Count() != roster.Length) { throw new ArgumentException("Invalid barrier"); }
        Id = id;
        Tick = tick;
        _roster = new HashSet<int>(roster);
    }

    internal bool Arrive(string id, int actor, long tick)
    {
        return id == Id && tick == Tick && _roster.Contains(actor) && _arrived.Add(actor);
    }

    internal bool RosterMatches(int[] roster) => roster.Length == _roster.Count && _roster.SetEquals(roster);
    internal bool Holds(long tick) => !Released && tick >= Tick;
    internal bool Release(string id, long localTick)
    {
        if (id != Id || localTick != Tick) { return false; }
        Released = true;
        return true;
    }
}
