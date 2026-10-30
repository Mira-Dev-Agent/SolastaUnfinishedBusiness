using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// the AI's wait for shared check results: the limit counts only the time spent waiting, added up over all passes of
// one decision, so the AI's own planning time can't use it up
internal sealed class AiWaitBudget(float seconds, Func<float> clock)
{
    private float _left = seconds;
    private float _start;

    internal bool HasTimeLeft => clock() - _start < _left;

    internal void Start()
    {
        _start = clock();
    }

    internal void Stop()
    {
        _left = Math.Max(0f, _left - (clock() - _start));
    }
}

// state a decision pass adds to and a retry must start without; each returned action puts the entries back as they
// were when it was made
internal static class AiRetryState
{
    internal static Action Keep<T>(List<T> list)
    {
        var copy = list.ToList();

        return () =>
        {
            list.Clear();
            list.AddRange(copy);
        };
    }

    internal static Action Keep<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
    {
        var copy = dictionary.ToList();

        return () =>
        {
            dictionary.Clear();

            foreach (var entry in copy)
            {
                dictionary.Add(entry.Key, entry.Value);
            }
        };
    }
}
