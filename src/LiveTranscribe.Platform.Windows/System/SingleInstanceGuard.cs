namespace LiveTranscribe.Platform.Windows.System;

/// <summary>Only one instance may own the hotkey and the microphone (FR-12.1).</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstanceGuard(Mutex? mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; }

    public static SingleInstanceGuard Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
        return createdNew ? new SingleInstanceGuard(mutex, true) : new SingleInstanceGuard(mutex, false);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (IsOwner)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released during shutdown.
            }
        }

        _mutex.Dispose();
    }
}
