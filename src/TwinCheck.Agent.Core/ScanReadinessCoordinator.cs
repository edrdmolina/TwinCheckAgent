namespace TwinCheck.Agent.Core;

public sealed record ScanReadinessProgress(
    string Phase,
    SourceCandidate? Candidate,
    string Message);

public sealed class ScanReadinessException(string code, string message, string? sourceDir = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    /// <summary>The folder the error is about, when it isn't the job's candidate (for example the watch folder).</summary>
    public string? SourceDir { get; } = sourceDir;
}

public sealed class ScanReadinessCoordinator(AgentConfigProvider configProvider)
{
    /// <summary>TwinCheckN shows a folder picker for this code and resends the job with the chosen folder.</summary>
    public const string MultipleSourceCandidatesCode = "multiple-source-candidates";

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
        var defaultWatchRoot = scannerMode == ScannerModes.NoritsuWatch
            ? ScannerFileSystem.GetNoritsuDailyFolder(configuredSource)
            : configuredSource;
        // A chosen source is a roll folder. The watch folder itself is never a roll, so choosing it means "watch".
        if (explicitSource is not null && (PathsEqual(explicitSource, configuredSource) || PathsEqual(explicitSource, defaultWatchRoot)))
        {
            explicitSource = null;
        }
        var watchRoot = explicitSource ?? defaultWatchRoot;

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
        var watching = explicitSource is null;
        var initialCandidate = FindCandidates(profile, scannerMode, watchRoot, !watching, request).FirstOrDefault();

        onProgress?.Invoke(new ScanReadinessProgress(
            ScanOperationPhases.Watching,
            initialCandidate,
            BuildWaitingMessage(scannerMode, initialCandidate)));

        // Take the newest ready folder. Without an explicit source, a second folder in the watch
        // folder means the agent can't tell which roll belongs to this job, so the operator picks.
        SourceCandidate candidate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new ScanReadinessException("watch-timeout", BuildWatchTimeoutMessage(scannerMode));
            }

            var candidates = FindCandidates(profile, scannerMode, watchRoot, !watching, request);
            var newestReady = candidates.FirstOrDefault(found => IsReadyToTake(scannerMode, found));
            if (newestReady is not null)
            {
                if (watching) EnsureSingleCandidate(candidates, watchRoot);
                candidate = newestReady;
                break;
            }

            var newest = candidates.FirstOrDefault() ?? initialCandidate;
            onProgress?.Invoke(new ScanReadinessProgress(
                ScanOperationPhases.Watching,
                newest,
                BuildWaitingMessage(scannerMode, newest)));
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

            // The scanner may rename the roll folder (for example WAIT to Ready) while it settles.
            // Follow it when it is still the only folder; otherwise ask the operator.
            var current = watching ? FindCandidates(profile, scannerMode, watchRoot, false, request) : [];
            if (watching) EnsureSingleCandidate(current, watchRoot);
            var renamed = current.FirstOrDefault(found => !PathsEqual(found.Path, candidate.Path) && IsReadyToTake(scannerMode, found));

            if (!stable)
            {
                if (renamed is not null)
                {
                    candidate = renamed;
                    continue;
                }

                throw new ScanReadinessException(
                    "settle-timeout",
                    $"Files did not stabilize within {profile.SettleTimeoutSeconds} seconds. Nothing was processed.");
            }

            if (renamed is not null)
            {
                candidate = renamed;
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

    private static bool IsReadyToTake(string scannerMode, SourceCandidate candidate) =>
        scannerMode == ScannerModes.FrontierSentinelWatch
            ? candidate.HasSentinel
            : candidate.ImageCount > 0;

    private static void EnsureSingleCandidate(IReadOnlyList<SourceCandidate> candidates, string watchRoot)
    {
        if (candidates.Count <= 1) return;

        const int listed = 5;
        var names = string.Join(", ", candidates.Take(listed).Select(found => found.Name));
        var more = candidates.Count > listed ? $", and {candidates.Count - listed} more" : "";
        throw new ScanReadinessException(
            MultipleSourceCandidatesCode,
            $"{candidates.Count} folders are in the watch folder ({names}{more}). Choose which one to process.",
            Path.GetFullPath(watchRoot));
    }

    /// <summary>Newest first: folders with images or the export sentinel. Empty folders are ignored.</summary>
    private static IReadOnlyList<SourceCandidate> FindCandidates(
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
            .ToArray();

    /// <summary>
    /// Rolls are always folders inside the watch folder. Loose files in the watch folder are ignored, and so
    /// are <c>_processed-…</c> folders waiting for their upload.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidateDirectories(string watchRoot, bool explicitSource)
    {
        if (!Directory.Exists(watchRoot))
        {
            return [];
        }

        if (explicitSource)
        {
            return [Path.GetFullPath(watchRoot)];
        }

        return Directory.EnumerateDirectories(watchRoot)
            .Where(path => !FileSystemSafety.IsProcessedFolder(path))
            .Select(Path.GetFullPath)
            .ToArray();
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
        PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
