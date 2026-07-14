namespace TwinCheck.Agent.Core;

public sealed class ScanProcessor(AgentConfigProvider configProvider, OperationStore operationStore)
{
    public ScanProcessor(AgentConfig config, OperationStore operationStore)
        : this(new AgentConfigProvider(config), operationStore)
    {
    }

    public ProcessScanResult Process(ProcessScanRequest request)
    {
        var config = configProvider.Current;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        }

        var profile = config.Profiles.SingleOrDefault(profile => profile.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        if (!ScannerModes.IsValid(profile.ScannerMode))
        {
            throw new InvalidOperationException($"Unknown scanner mode '{profile.ScannerMode}' for profile '{profile.Id}'.");
        }

        var scanKind = string.IsNullOrWhiteSpace(request.ScanKind) ? ScanKinds.Original : request.ScanKind;
        if (!ScanKinds.IsValid(scanKind))
        {
            throw new InvalidOperationException($"Unknown scan kind '{request.ScanKind}'.");
        }

        var destinationRoot = FileSystemSafety.EnsureInsideAnyRoot(
            request.DestinationDir ?? profile.DestinationDir,
            config.AllowedDestinationRoots,
            "destination");

        var finalDir = BuildFinalDirectory(destinationRoot, request.OrderNumber, request.RollNumber, profile.WeeklyDestination, scanKind, request.RescanNumber);
        var existingManifest = operationStore.TryReadManifest(finalDir, request.IdempotencyKey);
        if (existingManifest is not null && !request.DryRun)
        {
            return ToResult(existingManifest);
        }

        var sourceDir = FileSystemSafety.EnsureInsideAnyRoot(
            request.SourceDir ?? profile.SourceDir,
            config.AllowedSourceRoots,
            "source");

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");
        }

        sourceDir = ResolveSourceDirectory(sourceDir);

        Directory.CreateDirectory(destinationRoot);
        if (!FileSystemSafety.CanWriteToDirectory(destinationRoot))
        {
            throw new IOException($"Destination directory is not writable: {destinationRoot}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var files = ScannerFileSystem.GetFilesNatural(sourceDir);

        var imageFiles = ScannerFileSystem.GetImageFilesNatural(sourceDir);
        var reviewFiles = files.Except(imageFiles).ToArray();

        var manifests = new List<ScanFileManifest>();
        for (var index = 0; index < imageFiles.Length; index++)
        {
            var sourcePath = imageFiles[index];
            var sourceHash = FileSystemSafety.ComputeSha256(sourcePath);
            var plannedPath = ResolveDestinationPath(
                finalDir,
                request.Naming ?? profile.NamingPattern,
                request.OrderNumber,
                request.RollNumber,
                index + 1,
                Path.GetExtension(sourcePath),
                sourceHash);

            manifests.Add(new ScanFileManifest
            {
                SourcePath = sourcePath,
                DestinationPath = plannedPath.Path,
                FileName = Path.GetFileName(sourcePath),
                Size = new FileInfo(sourcePath).Length,
                Sha256 = sourceHash,
                Kind = ScanFileKind.Image,
                Outcome = request.DryRun ? ScanFileOutcome.Planned : plannedPath.Outcome,
                Message = plannedPath.Message
            });
        }

        foreach (var sourcePath in reviewFiles)
        {
            manifests.Add(new ScanFileManifest
            {
                SourcePath = sourcePath,
                DestinationPath = Path.Combine(destinationRoot, "_review", request.IdempotencyKey, Path.GetFileName(sourcePath)),
                FileName = Path.GetFileName(sourcePath),
                Size = new FileInfo(sourcePath).Length,
                Sha256 = FileSystemSafety.ComputeSha256(sourcePath),
                Kind = ScanFileKind.Review,
                Outcome = request.DryRun ? ScanFileOutcome.Planned : ScanFileOutcome.MovedToReview,
                Message = "Not a supported image extension; routed to review instead of deleting."
            });
        }

        if (request.DryRun)
        {
            var dryRunManifest = BuildManifest(request, sourceDir, destinationRoot, finalDir, true, startedAt, null, manifests);
            return ToResult(dryRunManifest);
        }

        var stagingDir = Path.Combine(destinationRoot, ".twincheck-staging", request.IdempotencyKey);
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        Directory.CreateDirectory(stagingDir);
        var committedFiles = new List<ScanFileManifest>();

        try
        {
            foreach (var file in manifests.Where(file => file.Kind == ScanFileKind.Image))
            {
                if (file.Outcome == ScanFileOutcome.AlreadyDone)
                {
                    committedFiles.Add(file);
                    continue;
                }

                var stagedPath = Path.Combine(stagingDir, Path.GetFileName(file.DestinationPath!));
                FileSystemSafety.CopyAndVerify(file.SourcePath, stagedPath);
                committedFiles.Add(file with { DestinationPath = stagedPath });
            }

            Directory.CreateDirectory(finalDir);
            var finalizedFiles = new List<ScanFileManifest>();
            foreach (var file in committedFiles)
            {
                if (file.Outcome == ScanFileOutcome.AlreadyDone)
                {
                    finalizedFiles.Add(file);
                    continue;
                }

                var finalPath = Path.Combine(finalDir, Path.GetFileName(file.DestinationPath!));
                File.Move(file.DestinationPath!, finalPath, overwrite: false);
                finalizedFiles.Add(file with
                {
                    DestinationPath = finalPath,
                    Outcome = file.Outcome == ScanFileOutcome.ConflictRenamed
                        ? ScanFileOutcome.ConflictRenamed
                        : ScanFileOutcome.Copied
                });
            }

            foreach (var file in manifests.Where(file => file.Kind == ScanFileKind.Review))
            {
                FileSystemSafety.CopyAndVerify(file.SourcePath, file.DestinationPath!);
                finalizedFiles.Add(file);
            }

            ArchiveSourceFolder(sourceDir, destinationRoot, request.IdempotencyKey);

            var manifest = BuildManifest(
                request,
                sourceDir,
                destinationRoot,
                finalDir,
                false,
                startedAt,
                DateTimeOffset.UtcNow,
                finalizedFiles);

            operationStore.WriteManifest(manifest);
            return ToResult(manifest);
        }
        catch
        {
            var failedManifest = BuildManifest(
                request,
                sourceDir,
                destinationRoot,
                finalDir,
                false,
                startedAt,
                DateTimeOffset.UtcNow,
                manifests.Select(file => file with { Outcome = file.Outcome == ScanFileOutcome.AlreadyDone ? file.Outcome : ScanFileOutcome.Failed }).ToArray(),
                ["Operation failed before source archive; source originals remain in place."]);

            operationStore.WriteManifest(failedManifest);
            throw;
        }
    }

    private static OperationManifest BuildManifest(
        ProcessScanRequest request,
        string sourceDir,
        string destinationRoot,
        string finalDir,
        bool dryRun,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        IReadOnlyList<ScanFileManifest> files,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            IdempotencyKey = request.IdempotencyKey,
            ProfileId = request.ProfileId,
            OrderNumber = request.OrderNumber,
            RollNumber = request.RollNumber,
            SourceDir = sourceDir,
            DestinationDir = destinationRoot,
            FinalDir = finalDir,
            ScanKind = string.IsNullOrWhiteSpace(request.ScanKind) ? ScanKinds.Original : request.ScanKind,
            RescanNumber = request.RescanNumber,
            DryRun = dryRun,
            Ok = files.All(file => file.Outcome != ScanFileOutcome.Failed),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Files = files,
            Warnings = warnings ?? []
        };

    private static (string Path, ScanFileOutcome Outcome, string? Message) ResolveDestinationPath(
        string finalDir,
        string namingPattern,
        string orderNumber,
        string rollNumber,
        int imageNumber,
        string extension,
        string sourceHash)
    {
        var baseName = namingPattern
            .Replace("{orderNumber}", orderNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{rollNumber}", rollNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{imgNumber}", imageNumber.ToString(), StringComparison.OrdinalIgnoreCase);

        var candidate = Path.Combine(finalDir, baseName + extension.ToLowerInvariant());
        if (!File.Exists(candidate))
        {
            return (candidate, ScanFileOutcome.Copied, null);
        }

        if (string.Equals(FileSystemSafety.ComputeSha256(candidate), sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            return (candidate, ScanFileOutcome.AlreadyDone, "Destination already contains an identical file.");
        }

        for (var version = 2; version < 1000; version++)
        {
            var versioned = Path.Combine(finalDir, $"{baseName}-v{version}{extension.ToLowerInvariant()}");
            if (!File.Exists(versioned))
            {
                return (versioned, ScanFileOutcome.ConflictRenamed, $"Destination name exists with different content; wrote version {version}.");
            }
        }

        throw new IOException($"Could not resolve a collision-free destination for '{candidate}'.");
    }

    public static string BuildFinalDirectoryPreview(
        string destinationRoot,
        string orderNumber,
        string rollNumber,
        bool weeklyDestination = true,
        string scanKind = ScanKinds.Original,
        int? rescanNumber = null) =>
        BuildFinalDirectory(destinationRoot, orderNumber, rollNumber, weeklyDestination, scanKind, rescanNumber);

    private static string BuildFinalDirectory(
        string destinationRoot,
        string orderNumber,
        string rollNumber,
        bool weeklyDestination,
        string scanKind,
        int? rescanNumber)
    {
        var orderRoot = weeklyDestination
            ? Path.Combine(destinationRoot, ScannerFileSystem.GetWeekFolder(), orderNumber)
            : Path.Combine(destinationRoot, orderNumber);
        var folderName = $"{orderNumber}-{rollNumber}";
        if (string.Equals(scanKind, ScanKinds.Rescan, StringComparison.OrdinalIgnoreCase))
        {
            folderName = $"{folderName}-rescan-{ResolveRescanNumber(orderRoot, folderName, rescanNumber)}";
        }

        return Path.Combine(orderRoot, folderName);
    }

    private static int ResolveRescanNumber(string orderRoot, string folderName, int? requested)
    {
        if (requested is >= 2)
        {
            return requested.Value;
        }

        if (!Directory.Exists(orderRoot))
        {
            return 2;
        }

        var prefix = $"{folderName}-rescan-";
        var max = Directory.EnumerateDirectories(orderRoot, $"{prefix}*")
            .Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))
            .Select(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : "")
            .Select(value => int.TryParse(value, out var number) ? number : 0)
            .DefaultIfEmpty(1)
            .Max();

        return Math.Max(2, max + 1);
    }

    private static string ResolveSourceDirectory(string configuredSourceDir)
    {
        var topLevelFiles = Directory.EnumerateFiles(configuredSourceDir).ToArray();
        if (topLevelFiles.Length > 0)
        {
            return configuredSourceDir;
        }

        var candidateDirs = Directory.EnumerateDirectories(configuredSourceDir)
            .Select(path => new
            {
                Path = path,
                ImageCount = Directory.EnumerateFiles(path).Count(FileSystemSafety.IsImageFile)
            })
            .Where(candidate => candidate.ImageCount > 0)
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidateDirs.Length == 1)
        {
            return candidateDirs[0].Path;
        }

        if (candidateDirs.Length > 1)
        {
            var names = string.Join(", ", candidateDirs.Select(candidate => Path.GetFileName(candidate.Path)));
            throw new MultipleSourceCandidatesException(
                configuredSourceDir,
                $"Source root contains multiple candidate roll folders. Select the exact roll folder before processing: {names}");
        }

        throw new InvalidOperationException($"Source directory contains no image files: {configuredSourceDir}");
    }

    private static void ArchiveSourceFolder(string sourceDir, string destinationRoot, string idempotencyKey)
    {
        var processedRoot = Path.Combine(destinationRoot, "_processed");
        Directory.CreateDirectory(processedRoot);
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        var archiveDir = Path.Combine(processedRoot, $"{sourceName}-{idempotencyKey}");

        if (Directory.Exists(archiveDir))
        {
            archiveDir = Path.Combine(processedRoot, $"{sourceName}-{idempotencyKey}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        }

        Directory.Move(sourceDir, archiveDir);
    }

    private static ProcessScanResult ToResult(OperationManifest manifest) =>
        new(
            manifest.Ok,
            manifest.Files.Count(file => file.Kind == ScanFileKind.Image),
            manifest,
            manifest.Files.Where(file => file.Outcome == ScanFileOutcome.ConflictRenamed).ToArray(),
            manifest.Files.Where(file => file.Kind == ScanFileKind.Review).ToArray());
}
