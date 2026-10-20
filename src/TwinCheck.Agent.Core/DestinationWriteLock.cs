namespace TwinCheck.Agent.Core;

/// <summary>
/// Serializes every write to scan destinations (uploads, manifests, cleanup deletes) so the NAS only ever
/// receives one file at a time from this agent, however many scanners or requests are active.
/// </summary>
public sealed class DestinationWriteLock
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public T Run<T>(Func<T> action)
    {
        gate.Wait();
        try
        {
            return action();
        }
        finally
        {
            gate.Release();
        }
    }

    public void Run(Action action) => Run(() =>
    {
        action();
        return true;
    });
}
