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

public sealed class ScanWatchService(AgentConfigProvider configProvider)
{
    private readonly ConcurrentDictionary<string, ActiveWatch> watches = new();

    public ScanWatchState Start(StartScanWatchRequest request)
    {
        var config = configProvider.Current;
        var profile = ResolveProfile(config, request.ProfileId);
        if (!string.Equals(profile.ScannerMode, ScannerModes.NoritsuDailyWatch, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Profile '{profile.Name}' is not a Noritsu daily watch profile.");
        }

        var sourceRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.SourceDir, config.AllowedSourceRoots, "source");
        var watchDir = ScannerFileSystem.GetNoritsuDailyFolder(sourceRoot);
        Directory.CreateDirectory(watchDir);
        var destinationRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.DestinationDir, config.AllowedDestinationRoots, "destination");
        if (!Directory.Exists(destinationRoot) || !FileSystemSafety.CanWriteToDirectory(destinationRoot))
        {
            throw new IOException($"Destination directory is not writable: {destinationRoot}");
        }

        var watchId = $"watch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var activeWatch = new ActiveWatch(
            watchId,
            profile,
            watchDir,
            request.OrderNumber,
            request.RollNumber,
            request.ScanKind,
            request.RescanNumber);
        if (!watches.TryAdd(watchId, activeWatch))
        {
            throw new InvalidOperationException("Could not create scan watch.");
        }

        activeWatch.Start(() => watches.TryRemove(watchId, out _));
        return activeWatch.ToState();
    }

    public ScanWatchState Get(string watchId)
    {
        if (!watches.TryGetValue(watchId, out var watch))
        {
            throw new InvalidOperationException($"Unknown scan watch '{watchId}'.");
        }

        return watch.ToState();
    }

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

    private static ScannerProfile ResolveProfile(AgentConfig config, string profileId) =>
        config.Profiles.SingleOrDefault(profile => profile.Id == profileId)
        ?? throw new InvalidOperationException($"Unknown scanner profile '{profileId}'.");

    private sealed class ActiveWatch(
        string watchId,
        ScannerProfile profile,
        string watchDir,
        string orderNumber,
        string rollNumber,
        string scanKind,
        int? rescanNumber)
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly HashSet<string> initialChildren = Directory.Exists(watchDir)
            ? Directory.EnumerateDirectories(watchDir).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        private readonly object gate = new();
        private string status = "watching";
        private string? message = "Watching for next Noritsu roll folder.";
        private SourceCandidate? candidate;
        private DateTimeOffset? completedAt;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public void Start(Action remove)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Run(cancellation.Token);
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

        public ScanWatchState ToState(string? overrideStatus = null, string? overrideMessage = null)
        {
            lock (gate)
            {
                return new ScanWatchState(
                    watchId,
                    profile.Id,
                    overrideStatus ?? status,
                    watchDir,
                    candidate,
                    StartedAt,
                    completedAt,
                    overrideMessage ?? message);
            }
        }

        private async Task Run(CancellationToken token)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, profile.SettleTimeoutSeconds) + 3600);
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var found = Directory.EnumerateDirectories(watchDir)
                    .Select(Path.GetFullPath)
                    .Where(path => !initialChildren.Contains(path))
                    .OrderBy(path => Directory.GetCreationTimeUtc(path))
                    .FirstOrDefault();
                if (found is not null)
                {
                    SetState("settling", null, $"Found roll folder: {Path.GetFileName(found)}");
                    await ScannerFileSystem.WaitUntilStable(
                        found,
                        profile.SettleStableSeconds,
                        profile.SettleTimeoutSeconds,
                        profile.SettlePollSeconds,
                        status => SetState("settling", null, status),
                        token);

                    var imageCount = ScannerFileSystem.CountImageFiles(found);
                    SetState(
                        "ready",
                        new SourceCandidate(
                            found,
                            Path.GetFileName(Path.TrimEndingDirectorySeparator(found)),
                            imageCount,
                            ScannerFileSystem.GetNewestImageModifiedAt(found),
                            false,
                            profile.ScannerMode,
                            ScanProcessor.BuildFinalDirectoryPreview(
                                profile.DestinationDir,
                                orderNumber,
                                rollNumber,
                                profile.WeeklyDestination,
                                scanKind,
                                rescanNumber)),
                        imageCount > 0
                            ? $"Ready: {imageCount} image(s) detected."
                            : "Ready, but no image files were detected.");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, profile.SettlePollSeconds)), token);
            }

            SetState("error", null, "Timed out waiting for a Noritsu roll folder.");
        }

        private void SetState(string nextStatus, SourceCandidate? nextCandidate, string? nextMessage)
        {
            lock (gate)
            {
                status = nextStatus;
                candidate = nextCandidate ?? candidate;
                message = nextMessage;
                if (nextStatus is "ready" or "error" or "cancelled")
                {
                    completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }
    }
}
