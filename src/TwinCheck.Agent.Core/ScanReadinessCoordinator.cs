namespace TwinCheck.Agent.Core;

public sealed record ScanReadinessProgress(
    string Phase,
    SourceCandidate? Candidate,
    string Message);

public sealed class ScanReadinessException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ScanReadinessCoordinator(AgentConfigProvider configProvider)
{
    public async Task<SourceCandidate> WaitForReadyAsync(
        ProcessScanRequest request,
        Action<ScanReadinessProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var config = configProvider.Current;
        var profile = config.Profiles.SingleOrDefault(candidate => candidate.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        var scannerMode = ScannerModes.Normalize(profile.ScannerMode)
            ?? throw new InvalidOperationException($"Unknown scanner mode '{profile.ScannerMode}' for profile '{profile.Id}'.");

        var explicitSource = !string.IsNullOrWhiteSpace(request.SourceDir)
            ? FileSystemSafety.EnsureInsideAnyRoot(request.SourceDir, config.AllowedSourceRoots, "source")
            : null;
        var configuredSource = FileSystemSafety.EnsureInsideAnyRoot(profile.SourceDir, config.AllowedSourceRoots, "source");
        var watchRoot = explicitSource ?? (scannerMode == ScannerModes.NoritsuWatch
            ? ScannerFileSystem.GetNoritsuDailyFolder(configuredSource)
            : configuredSource);

        if (scannerMode == ScannerModes.NoritsuWatch && explicitSource is null)
        {
            Directory.CreateDirectory(watchRoot);
        }

        var destinationRoot = FileSystemSafety.EnsureInsideAnyRoot(profile.DestinationDir, config.AllowedDestinationRoots, "destination");
        if (!Directory.Exists(destinationRoot) || !FileSystemSafety.CanWriteToDirectory(destinationRoot))
        {
            throw new IOException($"Destination directory is not writable: {destinationRoot}");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, profile.WatchTimeoutSeconds));
        var initialCandidate = FindNewestCandidate(profile, scannerMode, watchRoot, explicitSource is not null, request);
        var initialSentinelDirectories = EnumerateCandidateDirectories(watchRoot, explicitSource is not null)
            .Where(path => File.Exists(Path.Combine(path, FileSystemSafety.ExportSentinelFileName)))
            .ToHashSet(PathComparer);

        onProgress?.Invoke(new ScanReadinessProgress(
            ScanOperationPhases.Watching,
            initialCandidate,
            BuildWaitingMessage(scannerMode, initialCandidate)));

        SourceCandidate candidate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new ScanReadinessException("watch-timeout", BuildWatchTimeoutMessage(scannerMode));
            }

            var newest = FindNewestCandidate(profile, scannerMode, watchRoot, explicitSource is not null, request);
            if (newest is not null && IsReadyToSettle(
                    scannerMode,
                    newest,
                    initialCandidate,
                    initialSentinelDirectories,
                    explicitSource is not null))
            {
                candidate = newest;
                break;
            }

            onProgress?.Invoke(new ScanReadinessProgress(
                ScanOperationPhases.Watching,
                newest ?? initialCandidate,
                BuildWaitingMessage(scannerMode, newest ?? initialCandidate)));
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, profile.SettlePollSeconds)), cancellationToken);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke(new ScanReadinessProgress(
                ScanOperationPhases.Settling,
                candidate,
                $"Waiting for {Path.GetFileName(candidate.Path)} to remain stable for {profile.SettleStableSeconds} second(s)."));

            bool stable;
            try
            {
                stable = await ScannerFileSystem.WaitUntilStable(
                    candidate.Path,
                    profile.SettleStableSeconds,
                    profile.SettleTimeoutSeconds,
                    profile.SettlePollSeconds,
                    message => onProgress?.Invoke(new ScanReadinessProgress(ScanOperationPhases.Settling, candidate, message)),
                    cancellationToken);
            }
            catch (DirectoryNotFoundException)
            {
                stable = false;
            }

            if (!stable)
            {
                var replacement = explicitSource is null
                    ? FindNewestCandidate(profile, scannerMode, watchRoot, false, request)
                    : null;
                if (replacement is not null && !PathsEqual(replacement.Path, candidate.Path))
                {
                    candidate = replacement;
                    continue;
                }

                throw new ScanReadinessException(
                    "settle-timeout",
                    $"Files did not stabilize within {profile.SettleTimeoutSeconds} seconds. Nothing was processed.");
            }

            var newestAfterSettle = explicitSource is null
                ? FindNewestCandidate(profile, scannerMode, watchRoot, false, request)
                : candidate;
            if (newestAfterSettle is not null
                && !PathsEqual(newestAfterSettle.Path, candidate.Path)
                && IsReadyToSettle(scannerMode, newestAfterSettle, initialCandidate, initialSentinelDirectories, false))
            {
                candidate = newestAfterSettle;
                continue;
            }

            candidate = CreateCandidate(profile, scannerMode, candidate.Path, PathsEqual(candidate.Path, configuredSource), request);
            if (candidate.ImageCount == 0)
            {
                throw new ScanReadinessException("source-not-ready", $"Ready source contains no image files: {candidate.Path}");
            }

            if (scannerMode == ScannerModes.FrontierSentinelWatch && !candidate.HasSentinel)
            {
                throw new ScanReadinessException(
                    "source-not-ready",
                    $"Source is missing {FileSystemSafety.ExportSentinelFileName}: {candidate.Path}");
            }

            return candidate with
            {
                ReadyForSelection = true,
                ReadinessMessage = $"Ready: {candidate.ImageCount} image(s) detected."
            };
        }
    }

    private static bool IsReadyToSettle(
        string scannerMode,
        SourceCandidate candidate,
        SourceCandidate? initialCandidate,
        IReadOnlySet<string> initialSentinelDirectories,
        bool explicitSource)
    {
        if (scannerMode != ScannerModes.FrontierSentinelWatch)
        {
            return candidate.ImageCount > 0;
        }

        if (!candidate.HasSentinel)
        {
            return false;
        }

        if (explicitSource || initialCandidate?.HasSentinel == true)
        {
            return true;
        }

        return !initialSentinelDirectories.Contains(candidate.Path);
    }

    private static SourceCandidate? FindNewestCandidate(
        ScannerProfile profile,
        string scannerMode,
        string watchRoot,
        bool explicitSource,
        ProcessScanRequest request) =>
        EnumerateCandidateDirectories(watchRoot, explicitSource)
            .Where(Directory.Exists)
            .Select(path => CreateCandidate(profile, scannerMode, path, explicitSource, request))
            .Where(candidate => candidate.ImageCount > 0 || candidate.HasSentinel)
            .OrderByDescending(candidate => candidate.ModifiedAt)
            .ThenByDescending(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static IEnumerable<string> EnumerateCandidateDirectories(string watchRoot, bool explicitSource)
    {
        if (!Directory.Exists(watchRoot))
        {
            return [];
        }

        if (explicitSource
            || Directory.EnumerateFiles(watchRoot).Any(path => FileSystemSafety.IsImageFile(path)
                || string.Equals(Path.GetFileName(path), FileSystemSafety.ExportSentinelFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return [Path.GetFullPath(watchRoot)];
        }

        return Directory.EnumerateDirectories(watchRoot).Select(Path.GetFullPath).ToArray();
    }

    private static SourceCandidate CreateCandidate(
        ScannerProfile profile,
        string scannerMode,
        string path,
        bool isConfiguredRoot,
        ProcessScanRequest request)
    {
        var hasSentinel = File.Exists(Path.Combine(path, FileSystemSafety.ExportSentinelFileName));
        var imageCount = ScannerFileSystem.CountImageFiles(path);
        var modifiedAt = imageCount > 0
            ? ScannerFileSystem.GetNewestImageModifiedAt(path)
            : new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        var readyForSelection = scannerMode != ScannerModes.FrontierSentinelWatch || hasSentinel;
        return new SourceCandidate(
            Path.GetFullPath(path),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            imageCount,
            modifiedAt,
            isConfiguredRoot,
            scannerMode,
            ScanProcessor.BuildFinalDirectoryPreview(
                profile.DestinationDir,
                request.OrderNumber,
                request.RollNumber,
                profile.WeeklyDestination,
                request.ScanKind,
                request.RescanNumber),
            hasSentinel,
            readyForSelection,
            readyForSelection
                ? "Folder will be checked for stability before processing."
                : $"Waiting for {FileSystemSafety.ExportSentinelFileName}.");
    }

    private static string BuildWaitingMessage(string scannerMode, SourceCandidate? candidate)
    {
        var name = candidate is null ? "the newest scanner folder" : Path.GetFileName(candidate.Path);
        return scannerMode switch
        {
            ScannerModes.FrontierSentinelWatch => $"Waiting for {FileSystemSafety.ExportSentinelFileName} from {name}.",
            ScannerModes.FrontierPollingWatch => $"Watching {name} for stable Frontier output.",
            ScannerModes.NoritsuWatch => $"Watching {name} for stable Noritsu output.",
            _ => "Waiting for scanner output."
        };
    }

    private static string BuildWatchTimeoutMessage(string scannerMode) => scannerMode switch
    {
        ScannerModes.FrontierSentinelWatch => $"Timed out waiting for Frontier {FileSystemSafety.ExportSentinelFileName}.",
        ScannerModes.FrontierPollingWatch => "Timed out waiting for Frontier scanner output.",
        ScannerModes.NoritsuWatch => "Timed out waiting for Noritsu scanner output.",
        _ => "Timed out waiting for scanner output."
    };

    private static bool PathsEqual(string left, string right) =>
        PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
