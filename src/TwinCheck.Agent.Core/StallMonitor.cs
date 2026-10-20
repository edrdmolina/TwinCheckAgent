namespace TwinCheck.Agent.Core;

/// <summary>
/// Notices work that stops making progress. A thread stuck inside a hung network write can't be cancelled,
/// so the monitor only reports it, once per stall, and reports again when progress resumes.
/// </summary>
internal sealed class StallMonitor : IDisposable
{
    private readonly TimeSpan stallAfter;
    private readonly Action<string, string, TimeSpan> onStalled;
    private readonly Action<string> onResumed;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;

    public StallMonitor(TimeSpan stallAfter, TimeSpan checkInterval, Action<string, string, TimeSpan> onStalled, Action<string> onResumed)
    {
        this.stallAfter = stallAfter;
        this.onStalled = onStalled;
        this.onResumed = onResumed;
        loop = RunAsync(checkInterval);
    }

    /// <summary>Records progress on <paramref name="key"/>; <paramref name="step"/> describes what it is doing.</summary>
    public void Touch(string key, string step)
    {
        bool resumed;
        lock (entries)
        {
            resumed = entries.TryGetValue(key, out var entry) && entry.Stalled;
            entries[key] = new Entry(step, DateTimeOffset.UtcNow, Stalled: false);
        }

        if (resumed) onResumed(key);
    }

    public void Clear(string key)
    {
        lock (entries) entries.Remove(key);
    }

    public bool IsStalled(string key)
    {
        lock (entries) return entries.TryGetValue(key, out var entry) && entry.Stalled;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled during shutdown.
        }
        cancellation.Dispose();
    }

    private async Task RunAsync(TimeSpan checkInterval)
    {
        using var timer = new PeriodicTimer(checkInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                var now = DateTimeOffset.UtcNow;
                List<(string Key, Entry Entry)> stalled;
                lock (entries)
                {
                    stalled = entries
                        .Where(pair => !pair.Value.Stalled && now - pair.Value.LastProgressAt >= stallAfter)
                        .Select(pair => (pair.Key, pair.Value))
                        .ToList();
                    foreach (var (key, entry) in stalled) entries[key] = entry with { Stalled = true };
                }

                foreach (var (key, entry) in stalled)
                {
                    try
                    {
                        onStalled(key, entry.Step, now - entry.LastProgressAt);
                    }
                    catch
                    {
                        // Reporting is best effort; the next tick keeps watching other work.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private sealed record Entry(string Step, DateTimeOffset LastProgressAt, bool Stalled);
}
