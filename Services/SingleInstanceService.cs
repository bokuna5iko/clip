namespace Clip.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\Clip.SingleInstance";
    private const string ShowEventName = @"Local\Clip.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action _onShowRequested;

    public bool IsFirstInstance { get; }

    private SingleInstanceService(bool isFirst, Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        IsFirstInstance = isFirst;
        _mutex = mutex;
        _showEvent = showEvent;
        _onShowRequested = onShowRequested;
    }

    public static SingleInstanceService? TryAcquireFirstInstance(Action onShowRequested)
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(0))
            {
                SignalExistingInstance();
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous process crashed. This process now owns the mutex.
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return new SingleInstanceService(true, mutex, showEvent, onShowRequested);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch
        {
            // ignore
        }
    }

    public void StartListening()
    {
        Task.Run(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_showEvent.WaitOne(500))
                        _onShowRequested();
                }
                catch
                {
                    // ignore
                }
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
