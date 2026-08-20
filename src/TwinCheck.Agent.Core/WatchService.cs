using System.Collections.Concurrent;

namespace TwinCheck.Agent.Core;

public sealed record StartScanWatchRequest
{
    public required string ProfileId { get; init; }
    public required string OrderNumber { get; init; }
    public required string RollNumber { get; init; }
    public string ScanKind { get; init; } = ScanKinds.Original;
    public int? RescanNumber { get; init; }
}

public sealed record ScanWatchState(
    string WatchId,
    string ProfileId,
    string Status,
    string WatchDir,
    SourceCandidate? Candidate,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message);

public sealed class ScanWatchService
{
    private readonly AgentConfigProvider configProvider;
    private readonly ScanReadinessCoordinator readinessCoordinator;
    private readonly ConcurrentDictionary<string, ActiveWatch> watches = new();

    public ScanWatchService(AgentConfigProvider configProvider)
        : this(configProvider, new ScanReadinessCoordinator(configProvider))
    {
    }

    public ScanWatchService(AgentConfigProvider configProvider, ScanReadinessCoordinator readinessCoordinator)
    {
        this.configProvider = configProvider;
        this.readinessCoordinator = readinessCoordinator;
    }

    internal ScanReadinessCoordinator ReadinessCoordinator => readinessCoordinator;

    public ScanWatchState Start(StartScanWatchRequest request)
    {
        var config = configProvider.Current;
        var profile = config.Profiles.SingleOrDefault(candidate => candidate.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        var scannerMode = ScannerModes.Normalize(profile.ScannerMode)
            ?? throw new InvalidOperationException($"Unknown scanner mode '{profile.ScannerMode}' for profile '{profile.Id}'.");
        var sourceRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.SourceDir, config.AllowedSourceRoots, "source");
        var watchDir = scannerMode == ScannerModes.NoritsuWatch
            ? ScannerFileSystem.GetNoritsuDailyFolder(sourceRoot)
            : sourceRoot;

        var watchId = $"watch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var activeWatch = new ActiveWatch(watchId, watchDir, request, readinessCoordinator);
        if (!watches.TryAdd(watchId, activeWatch))
        {
            throw new InvalidOperationException("Could not create scan watch.");
        }

        activeWatch.Start();
        return activeWatch.ToState();
    }

    public ScanWatchState Get(string watchId) =>
        watches.TryGetValue(watchId, out var watch)
            ? watch.ToState()
            : throw new InvalidOperationException($"Unknown scan watch '{watchId}'.");

    public ScanWatchState Cancel(string watchId)
    {
        if (!watches.TryRemove(watchId, out var watch))
        {
            throw new InvalidOperationException($"Unknown scan watch '{watchId}'.");
        }

        watch.Cancel();
        return watch.ToState("cancelled", "Watch cancelled.");
    }

    public IReadOnlyList<ScanWatchState> ListActive() =>
        watches.Values.Select(watch => watch.ToState()).ToArray();

    public void MarkProcessing(string? watchId, string operationId)
    {
        if (!string.IsNullOrWhiteSpace(watchId) && watches.TryGetValue(watchId, out var watch))
        {
            watch.SetExternalState("processing", $"Processing scan operation {operationId}.");
        }
    }

    public void MarkCompleted(string? watchId, string operationId, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(watchId) && watches.TryGetValue(watchId, out var watch))
        {
            watch.SetExternalState("completed", detail ?? $"Scan operation {operationId} completed.");
        }
    }

    public void MarkFailed(string? watchId, string operationId, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(watchId) && watches.TryGetValue(watchId, out var watch))
        {
            watch.SetExternalState("failed", detail ?? $"Scan operation {operationId} failed.");
        }
    }

    private sealed class ActiveWatch(
        string watchId,
        string watchDir,
        StartScanWatchRequest request,
        ScanReadinessCoordinator readinessCoordinator)
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly object gate = new();
        private string status = "watching";
        private string? message = "Waiting for scanner output.";
        private SourceCandidate? candidate;
        private DateTimeOffset? completedAt;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public void Start()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var ready = await readinessCoordinator.WaitForReadyAsync(
                        new ProcessScanRequest
                        {
                            IdempotencyKey = watchId,
                            ProfileId = request.ProfileId,
                            OrderNumber = request.OrderNumber,
                            RollNumber = request.RollNumber,
                            ScanKind = request.ScanKind,
                            RescanNumber = request.RescanNumber,
                            WaitForReady = true,
                            DryRun = true
                        },
                        progress => SetState(progress.Phase, progress.Candidate, progress.Message),
                        cancellation.Token);
                    SetState("ready", ready, ready.ReadinessMessage);
                }
                catch (OperationCanceledException)
                {
                    SetState("cancelled", null, "Watch cancelled.");
                }
                catch (Exception exception)
                {
                    SetState("error", null, exception.Message);
                }
            });
        }

        public void Cancel() => cancellation.Cancel();

        public void SetExternalState(string nextStatus, string nextMessage) =>
            SetState(nextStatus, null, nextMessage);

        public ScanWatchState ToState(string? overrideStatus = null, string? overrideMessage = null)
        {
            lock (gate)
            {
                return new ScanWatchState(
                    watchId,
                    request.ProfileId,
                    overrideStatus ?? status,
                    watchDir,
                    candidate,
                    StartedAt,
                    completedAt,
                    overrideMessage ?? message);
            }
        }

        private void SetState(string nextStatus, SourceCandidate? nextCandidate, string? nextMessage)
        {
            lock (gate)
            {
                status = nextStatus;
                candidate = nextCandidate ?? candidate;
                message = nextMessage;
                if (nextStatus is "processing")
                {
                    completedAt = null;
                }
                else if (nextStatus is "ready" or "completed" or "failed" or "error" or "cancelled")
                {
                    completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }
    }
}
