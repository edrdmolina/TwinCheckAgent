using System;
using System.Threading;

namespace TwinCheck.Agent.Gui.Desktop;

/// <summary>
/// Keeps one tray app per Windows session. A second launch asks the running copy to show
/// its window and then exits, so double-clicking the shortcut never starts a second API.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TwinCheckScanAgent";
    private const string ShowEventName = @"Local\TwinCheckScanAgent.Show";

    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent;
    private readonly CancellationTokenSource stopping = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        this.mutex = mutex;
        this.showEvent = showEvent;
    }

    /// <summary>Returns null after signalling the already-running instance.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (createdNew) return new SingleInstance(mutex, showEvent);

        showEvent.Set();
        showEvent.Dispose();
        mutex.Dispose();
        return null;
    }

    public void ListenForShowRequests(Action onShow)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { showEvent, stopping.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) onShow();
        })
        { IsBackground = true, Name = "TwinCheck single-instance listener" };
        thread.Start();
    }

    public void Dispose()
    {
        stopping.Cancel();
        showEvent.Dispose();
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
    }
}
