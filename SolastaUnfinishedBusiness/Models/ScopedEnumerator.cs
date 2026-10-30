using System;
using System.Collections;

namespace SolastaUnfinishedBusiness.Models;

// Enter only while the game advances an action, never while it is waiting for a frame or a reaction.
internal sealed class ScopedEnumerator : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Func<IDisposable> _enter;
    private readonly Func<IEnumerator, bool> _wrapChild;
    private bool _disposed;
    public object Current { get; private set; }

    internal ScopedEnumerator(IEnumerator inner, Func<IDisposable> enter, Func<IEnumerator, bool> wrapChild)
    {
        _inner = inner;
        _enter = enter;
        _wrapChild = wrapChild;
    }

    public bool MoveNext()
    {
        if (_disposed) return false;
        bool more;
        try
        {
            using (_enter())
            {
                more = _inner.MoveNext();
                Current = more ? _inner.Current : null;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        if (!more) { Dispose(); return false; }
        if (Current is IEnumerator child && child is not ScopedEnumerator && _wrapChild(child))
            Current = new ScopedEnumerator(child, _enter, _wrapChild);
        return true;
    }

    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Current = null;
        (_inner as IDisposable)?.Dispose();
    }
}
