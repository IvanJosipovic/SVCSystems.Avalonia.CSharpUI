using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Threading;

/// <summary>
/// Collapses a storm of change notifications into one UI-thread callback. A request arriving while
/// the callback is running is posted again, so no change is lost. This is deliberately much smaller
/// than a render loop: there is no element tree to re-execute or diff.
/// </summary>
public sealed class ChangeCoalescer
{
    private int _pending;
    private readonly DispatcherPriority _priority;

    public ChangeCoalescer()
        : this(DispatcherPriority.Background)
    {
    }

    public ChangeCoalescer(DispatcherPriority priority)
    {
        _priority = priority;
    }

    public event Action? Changed;

    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(Flush, _priority);
    }

    private void Flush()
    {
        // Clear before invoking: a notification raised by the callback must schedule a new pass
        // rather than being swallowed by the in-flight flag.
        Volatile.Write(ref _pending, 0);
        Changed?.Invoke();
    }
}
