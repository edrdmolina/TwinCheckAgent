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
        var scannerMode = ScannerModes.Normalize(profile.ScannerMode)
            ?? throw new InvalidOperationException($"Unknown scanner mode '{profile.ScannerMode}' for profile '{profile.Id}'.");

        var sourceRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.SourceDir, config.AllowedSourceRoots, "source");
        var watchDir = scannerMode == ScannerModes.NoritsuWatch
            ? ScannerFileSystem.GetNoritsuDailyFolder(sourceRoot)
            : sourceRoot;
        if (scannerMode == ScannerModes.NoritsuWatch)
        {
            Directory.CreateDirectory(watchDir);
        }

        var destinationRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.DestinationDir, config.AllowedDestinationRoots, "destination");
        if (!Directory.Exists(destinationRoot) || !FileSystemSafety.CanWriteToDirectory(destinationRoot))
        {
            throw new IOException($"Destination directory is not writable: {destinationRoot}");
        }

        var watchId = $"watch-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var activeWatch = new ActiveWatch(
            watchId,
            profile,
            scannerMode,
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
        string scannerMode,
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
        private string? message = BuildInitialMessage(scannerMode);
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
                var found = FindCandidateDirectory();
                if (found is not null)
                {
                    SetState("settling", CreateCandidate(found), BuildFoundMessage(found));
                    await ScannerFileSystem.WaitUntilStable(
                        found,
                        profile.SettleStableSeconds,
                        profile.SettleTimeoutSeconds,
                        profile.SettlePollSeconds,
                        status => SetState("settling", CreateCandidate(found), status),
                        token);

                    var imageCount = ScannerFileSystem.CountImageFiles(found);
                    SetState(
                        "ready",
                        CreateCandidate(found),
                        imageCount > 0
                            ? $"Ready: {imageCount} image(s) detected."
                            : "Ready, but no image files were detected.");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, profile.SettlePollSeconds)), token);
            }

            SetState("error", null, BuildTimeoutMessage(scannerMode));
        }

        private string? FindCandidateDirectory()
        {
            var directories = Directory.Exists(watchDir)
                ? Directory.EnumerateDirectories(watchDir).Select(Path.GetFullPath).ToArray()
                : [];

            return scannerMode switch
            {
                ScannerModes.FrontierPollingWatch => directories
                    .Where(path => !initialChildren.Contains(path))
                    .Where(path => ScannerFileSystem.CountImageFiles(path) > 0)
                    .OrderBy(path => Directory.GetCreationTimeUtc(path))
                    .FirstOrDefault(),
                ScannerModes.FrontierSentinelWatch => directories
                    .Where(path => !initialChildren.Contains(path))
                    .Where(path => File.Exists(Path.Combine(path, FileSystemSafety.ExportSentinelFileName)))
                    .OrderBy(path => Directory.GetCreationTimeUtc(path))
                    .FirstOrDefault(),
                ScannerModes.NoritsuWatch => directories
                    .Where(path => !initialChildren.Contains(path))
                    .OrderBy(path => Directory.GetCreationTimeUtc(path))
                    .FirstOrDefault(),
                _ => null
            };
        }

        private SourceCandidate CreateCandidate(string found)
        {
            return new SourceCandidate(
                found,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(found)),
                ScannerFileSystem.CountImageFiles(found),
                ScannerFileSystem.GetNewestImageModifiedAt(found),
                false,
                scannerMode,
                ScanProcessor.BuildFinalDirectoryPreview(
                    profile.DestinationDir,
                    orderNumber,
                    rollNumber,
                    profile.WeeklyDestination,
                    scanKind,
                    rescanNumber));
        }

        private static string BuildInitialMessage(string mode) => mode switch
        {
            ScannerModes.FrontierPollingWatch => "Watching Frontier folder for the next roll folder.",
            ScannerModes.FrontierSentinelWatch => $"Waiting for Frontier {FileSystemSafety.ExportSentinelFileName}.",
            ScannerModes.NoritsuWatch => "Watching for next Noritsu roll folder.",
            _ => "Watching for scan output."
        };

        private string BuildFoundMessage(string found)
        {
            var imageCount = ScannerFileSystem.CountImageFiles(found);
            return scannerMode switch
            {
                ScannerModes.FrontierPollingWatch => $"Found Frontier folder: {Path.GetFileName(found)} ({imageCount} image(s)).",
                ScannerModes.FrontierSentinelWatch => $"Found Frontier sentinel in {Path.GetFileName(found)} ({imageCount} image(s)).",
                ScannerModes.NoritsuWatch => $"Found Noritsu folder: {Path.GetFileName(found)} ({imageCount} image(s)).",
                _ => $"Found roll folder: {Path.GetFileName(found)} ({imageCount} image(s))."
            };
        }

        private static string BuildTimeoutMessage(string mode) => mode switch
        {
            ScannerModes.FrontierPollingWatch => "Timed out waiting for a Frontier roll folder.",
            ScannerModes.FrontierSentinelWatch => $"Timed out waiting for Frontier {FileSystemSafety.ExportSentinelFileName}.",
            ScannerModes.NoritsuWatch => "Timed out waiting for a Noritsu roll folder.",
            _ => "Timed out waiting for scan output."
        };

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
