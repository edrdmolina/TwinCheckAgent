namespace TwinCheck.Agent.Core;

public sealed class ScanProcessor
{
    private readonly AgentConfigProvider configProvider;
    private readonly OperationStore operationStore;
    private readonly IScanImageConverter imageConverter;

    public ScanProcessor(
        AgentConfigProvider configProvider,
        OperationStore operationStore,
        IScanImageConverter imageConverter)
    {
        this.configProvider = configProvider;
        this.operationStore = operationStore;
        this.imageConverter = imageConverter;
    }

    public ScanProcessor(AgentConfigProvider configProvider, OperationStore operationStore)
        : this(configProvider, operationStore, UnavailableScanImageConverter.Instance)
    {
    }

    public ScanProcessor(AgentConfig config, OperationStore operationStore)
        : this(new AgentConfigProvider(config), operationStore, UnavailableScanImageConverter.Instance)
    {
    }

    public ScanProcessor(AgentConfig config, OperationStore operationStore, IScanImageConverter imageConverter)
        : this(new AgentConfigProvider(config), operationStore, imageConverter)
    {
    }

    public ProcessScanResult Process(ProcessScanRequest request, Action<ScanProgress>? onProgress = null)
    {
        var config = configProvider.Current;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        }

        var profile = config.Profiles.SingleOrDefault(profile => profile.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        var effectiveOptions = request.Options ?? profile.Options ?? new ScanOptions();
        var scannerMode = ScannerModes.Normalize(profile.ScannerMode);
        if (scannerMode is null)
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
            return existingManifest.CompletedAt is not null
                ? ToResult(existingManifest)
                : CompletePendingManifest(existingManifest, onProgress);
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

        if (scannerMode == ScannerModes.FrontierSentinelWatch
            && !File.Exists(Path.Combine(sourceDir, FileSystemSafety.ExportSentinelFileName)))
        {
            throw new ScanReadinessException(
                "source-not-ready",
                $"Source is missing {FileSystemSafety.ExportSentinelFileName}: {sourceDir}");
        }

        Directory.CreateDirectory(destinationRoot);
        if (!FileSystemSafety.CanWriteToDirectory(destinationRoot))
        {
            throw new IOException($"Destination directory is not writable: {destinationRoot}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var files = ScannerFileSystem.GetFilesNatural(sourceDir)
            .Where(path => !FileSystemSafety.IsIgnoredControlFile(path))
            .ToArray();

        var imageFiles = ScannerFileSystem.GetImageFilesNatural(sourceDir);
        var reviewFiles = files.Except(imageFiles).ToArray();
        var totalBytes = files.Sum(path => new FileInfo(path).Length);
        var fileCount = files.Length;

        if (request.DryRun)
        {
            var manifests = new List<ScanFileManifest>();
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Hashing, 0, fileCount, 0, totalBytes, "Calculating dry-run checksums."));
            long hashedBytes = 0;
            string? dryRunStagingDir = null;
            try
            {
                for (var index = 0; index < imageFiles.Length; index++)
                {
                    var sourcePath = imageFiles[index];
                    var sourceSize = new FileInfo(sourcePath).Length;
                    var sourceHash = FileSystemSafety.ComputeSha256(sourcePath);
                    var outputPath = sourcePath;
                    var outputExtension = Path.GetExtension(sourcePath);
                    string? conversion = null;
                    if (ShouldConvertBmp(sourcePath, effectiveOptions))
                    {
                        dryRunStagingDir ??= CreateStagingDirectory(
                            destinationRoot,
                            $"dry-run-{SafePathToken(request.IdempotencyKey)}-{Guid.NewGuid():N}");
                        outputPath = Path.Combine(dryRunStagingDir, $"{index + 1:D6}.tif");
                        ConvertBmpToTiff(sourcePath, outputPath);
                        outputExtension = ".tif";
                        conversion = ScanFileConversions.BmpToTiff;
                    }

                    var outputSize = new FileInfo(outputPath).Length;
                    var outputHash = conversion is null ? sourceHash : FileSystemSafety.ComputeSha256(outputPath);
                    hashedBytes += sourceSize;
                    var plannedPath = ResolveDestinationPath(
                        finalDir,
                        request.Naming ?? profile.NamingPattern,
                        request.OrderNumber,
                        request.RollNumber,
                        index + 1,
                        outputExtension,
                        outputHash);

                    manifests.Add(new ScanFileManifest
                    {
                        SourcePath = sourcePath,
                        DestinationPath = plannedPath.Path,
                        FileName = Path.GetFileName(sourcePath),
                        SourceSize = sourceSize,
                        SourceSha256 = sourceHash,
                        Size = outputSize,
                        Sha256 = outputHash,
                        Conversion = conversion,
                        Kind = ScanFileKind.Image,
                        Outcome = plannedPath.Outcome == ScanFileOutcome.Copied ? ScanFileOutcome.Planned : plannedPath.Outcome,
                        Message = plannedPath.Message
                    });
                    onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Hashing, manifests.Count, fileCount, hashedBytes, totalBytes));
                }

                foreach (var sourcePath in reviewFiles)
                {
                    var size = new FileInfo(sourcePath).Length;
                    var sourceHash = FileSystemSafety.ComputeSha256(sourcePath);
                    hashedBytes += size;
                    manifests.Add(new ScanFileManifest
                    {
                        SourcePath = sourcePath,
                        DestinationPath = Path.Combine(destinationRoot, "_review", SafePathToken(request.IdempotencyKey), Path.GetFileName(sourcePath)),
                        FileName = Path.GetFileName(sourcePath),
                        SourceSize = size,
                        SourceSha256 = sourceHash,
                        Size = size,
                        Sha256 = sourceHash,
                        Kind = ScanFileKind.Review,
                        Outcome = ScanFileOutcome.Planned,
                        Message = "Not a supported image extension; routed to review instead of deleting."
                    });
                    onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Hashing, manifests.Count, fileCount, hashedBytes, totalBytes));
                }

                var dryRunManifest = BuildManifest(request, sourceDir, destinationRoot, finalDir, true, startedAt, null, manifests, effectiveOptions);
                return ToResult(dryRunManifest);
            }
            finally
            {
                TryDeleteDirectory(dryRunStagingDir);
            }
        }

        var stagingDir = CreateStagingDirectory(destinationRoot, SafePathToken(request.IdempotencyKey));
        var preparedFiles = new List<PreparedScanFile>();
        var manifestsForFailure = new List<ScanFileManifest>();
        var provisionalManifestWritten = false;

        try
        {
            var inputs = imageFiles.Select((path, index) => new InputScanFile(path, ScanFileKind.Image, index + 1))
                .Concat(reviewFiles.Select(path => new InputScanFile(path, ScanFileKind.Review, 0)))
                .ToArray();
            long copiedBytes = 0;
            long lastReportedBytes = 0;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, 0, fileCount, 0, totalBytes, "Copying source files into staging while calculating checksums."));

            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                var sourceExtension = Path.GetExtension(input.SourcePath).ToLowerInvariant();
                var stagedSourcePath = Path.Combine(stagingDir, $"{index + 1:D6}-source{sourceExtension}");
                var copied = FileSystemSafety.CopyWithSha256(input.SourcePath, stagedSourcePath, delta =>
                {
                    copiedBytes += delta;
                    if (copiedBytes - lastReportedBytes >= 64L * 1024 * 1024)
                    {
                        lastReportedBytes = copiedBytes;
                        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index, fileCount, copiedBytes, totalBytes));
                    }
                });

                var stagedOutputPath = stagedSourcePath;
                var outputExtension = sourceExtension;
                var outputSize = copied.Length;
                var outputHash = copied.Sha256;
                string? conversion = null;
                if (input.Kind == ScanFileKind.Image && ShouldConvertBmp(input.SourcePath, effectiveOptions))
                {
                    stagedOutputPath = Path.Combine(stagingDir, $"{index + 1:D6}-output.tif");
                    try
                    {
                        ConvertBmpToTiff(stagedSourcePath, stagedOutputPath);
                        outputExtension = ".tif";
                        outputSize = new FileInfo(stagedOutputPath).Length;
                        outputHash = FileSystemSafety.ComputeSha256(stagedOutputPath);
                        conversion = ScanFileConversions.BmpToTiff;
                        File.Delete(stagedSourcePath);
                    }
                    catch (Exception exception)
                    {
                        manifestsForFailure.Add(new ScanFileManifest
                        {
                            SourcePath = input.SourcePath,
                            FileName = Path.GetFileName(input.SourcePath),
                            SourceSize = copied.Length,
                            SourceSha256 = copied.Sha256,
                            Conversion = ScanFileConversions.BmpToTiff,
                            Kind = input.Kind,
                            Outcome = ScanFileOutcome.Failed,
                            Message = $"BMP-to-TIFF conversion failed: {exception.Message}"
                        });
                        throw;
                    }
                }

                var destination = input.Kind == ScanFileKind.Image
                    ? ResolveDestinationPath(
                        finalDir,
                        request.Naming ?? profile.NamingPattern,
                        request.OrderNumber,
                        request.RollNumber,
                        input.ImageNumber,
                        outputExtension,
                        outputHash)
                    : ResolveReviewDestination(
                        Path.Combine(destinationRoot, "_review", SafePathToken(request.IdempotencyKey), Path.GetFileName(input.SourcePath)),
                        outputHash);

                var alreadyAtDestination = destination.Outcome == ScanFileOutcome.AlreadyDone;
                var fileManifest = new ScanFileManifest
                {
                    SourcePath = input.SourcePath,
                    DestinationPath = destination.Path,
                    FileName = Path.GetFileName(input.SourcePath),
                    SourceSize = copied.Length,
                    SourceSha256 = copied.Sha256,
                    Size = outputSize,
                    Sha256 = outputHash,
                    Conversion = conversion,
                    Kind = input.Kind,
                    Outcome = input.Kind == ScanFileKind.Review ? ScanFileOutcome.MovedToReview : destination.Outcome,
                    Message = input.Kind == ScanFileKind.Review
                        ? "Not a supported image extension; routed to review instead of deleting."
                        : destination.Message
                };
                preparedFiles.Add(new PreparedScanFile(fileManifest, stagedOutputPath, alreadyAtDestination));
                manifestsForFailure.Add(fileManifest);
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index + 1, fileCount, copiedBytes, totalBytes));
            }

            var outputTotalBytes = preparedFiles.Sum(file => file.Manifest.Size);
            long verifiedBytes = 0;
            long lastVerifiedReport = 0;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, 0, fileCount, 0, outputTotalBytes, "Verifying staged checksums."));
            for (var index = 0; index < preparedFiles.Count; index++)
            {
                var prepared = preparedFiles[index];
                FileSystemSafety.VerifySha256(prepared.StagedPath, prepared.Manifest.Size, prepared.Manifest.Sha256!, delta =>
                {
                    verifiedBytes += delta;
                    if (verifiedBytes - lastVerifiedReport >= 64L * 1024 * 1024)
                    {
                        lastVerifiedReport = verifiedBytes;
                        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, index, fileCount, verifiedBytes, outputTotalBytes));
                    }
                });
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, index + 1, fileCount, verifiedBytes, outputTotalBytes));
            }

            var finalizedFiles = new List<ScanFileManifest>();
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Finalizing, 0, fileCount, 0, outputTotalBytes, "Finalizing verified files."));
            for (var index = 0; index < preparedFiles.Count; index++)
            {
                var prepared = preparedFiles[index];
                if (prepared.AlreadyAtDestination)
                {
                    File.Delete(prepared.StagedPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(prepared.Manifest.DestinationPath!)!);
                    File.Move(prepared.StagedPath, prepared.Manifest.DestinationPath!, overwrite: false);
                }

                finalizedFiles.Add(prepared.Manifest);
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Finalizing, index + 1, fileCount, finalizedFiles.Sum(file => file.Size), outputTotalBytes));
            }

            var archiveDir = BuildSourceArchiveDirectory(sourceDir, destinationRoot, request.IdempotencyKey);
            var manifest = BuildManifest(
                request,
                sourceDir,
                destinationRoot,
                finalDir,
                false,
                startedAt,
                null,
                finalizedFiles,
                effectiveOptions,
                sourceArchiveDir: archiveDir);

            operationStore.WriteManifest(manifest);
            provisionalManifestWritten = true;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Archiving, 0, fileCount, 0, totalBytes, "Archiving original source folder."));
            ArchiveSourceFolder(sourceDir, archiveDir);

            var completedManifest = manifest with { CompletedAt = DateTimeOffset.UtcNow };
            operationStore.WriteManifest(completedManifest);
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Archiving, fileCount, fileCount, totalBytes, totalBytes, "Source archive completed."));
            return ToResult(completedManifest);
        }
        catch
        {
            if (provisionalManifestWritten)
            {
                throw;
            }

            var failedManifest = BuildManifest(
                request,
                sourceDir,
                destinationRoot,
                finalDir,
                false,
                startedAt,
                DateTimeOffset.UtcNow,
                manifestsForFailure.Select(file => file with { Outcome = file.Outcome == ScanFileOutcome.AlreadyDone ? file.Outcome : ScanFileOutcome.Failed }).ToArray(),
                effectiveOptions,
                warnings: ["Operation failed before source archive; source originals remain in place."]);

            operationStore.WriteManifest(failedManifest);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private ProcessScanResult CompletePendingManifest(OperationManifest manifest, Action<ScanProgress>? onProgress)
    {
        var files = manifest.Files.Where(file => !string.IsNullOrWhiteSpace(file.DestinationPath)).ToArray();
        var totalBytes = files.Sum(file => file.Size);
        long verifiedBytes = 0;
        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, 0, files.Length, 0, totalBytes, "Recovering a finalized scan operation."));
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            if (!File.Exists(file.DestinationPath))
            {
                throw new IOException($"Cannot recover scan operation because a finalized file is missing: {file.DestinationPath}");
            }

            FileSystemSafety.VerifySha256(file.DestinationPath!, file.Size, file.Sha256!, delta => verifiedBytes += delta);
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, index + 1, files.Length, verifiedBytes, totalBytes));
        }

        var archiveDir = manifest.SourceArchiveDir
            ?? BuildSourceArchiveDirectory(manifest.SourceDir, manifest.DestinationDir, manifest.IdempotencyKey);
        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Archiving, 0, files.Length, 0, totalBytes, "Recovering source archival."));
        ArchiveSourceFolder(manifest.SourceDir, archiveDir);

        var completed = manifest with
        {
            SourceArchiveDir = archiveDir,
            CompletedAt = DateTimeOffset.UtcNow
        };
        operationStore.WriteManifest(completed);
        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Archiving, files.Length, files.Length, totalBytes, totalBytes));
        return ToResult(completed);
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
        ScanOptions effectiveOptions,
        string? sourceArchiveDir = null,
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
            SourceArchiveDir = sourceArchiveDir,
            ScanKind = string.IsNullOrWhiteSpace(request.ScanKind) ? ScanKinds.Original : request.ScanKind,
            RescanNumber = request.RescanNumber,
            EffectiveOptions = effectiveOptions,
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

    private static (string Path, ScanFileOutcome Outcome, string? Message) ResolveReviewDestination(string candidate, string sourceHash)
    {
        if (!File.Exists(candidate))
        {
            return (candidate, ScanFileOutcome.MovedToReview, null);
        }

        if (string.Equals(FileSystemSafety.ComputeSha256(candidate), sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            return (candidate, ScanFileOutcome.AlreadyDone, "Review destination already contains an identical file.");
        }

        var directory = Path.GetDirectoryName(candidate)!;
        var baseName = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var version = 2; version < 1000; version++)
        {
            var versioned = Path.Combine(directory, $"{baseName}-v{version}{extension}");
            if (!File.Exists(versioned))
            {
                return (versioned, ScanFileOutcome.ConflictRenamed, $"Review destination name exists; wrote version {version}.");
            }
        }

        throw new IOException($"Could not resolve a collision-free review destination for '{candidate}'.");
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

    private static string BuildSourceArchiveDirectory(string sourceDir, string destinationRoot, string idempotencyKey)
    {
        var processedRoot = Path.Combine(destinationRoot, "_processed");
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        return Path.Combine(processedRoot, $"{sourceName}-{SafePathToken(idempotencyKey)}");
    }

    internal static void ArchiveSourceFolder(string sourceDir, string archiveDir, bool forceCopy = false)
    {
        if (!Directory.Exists(sourceDir))
        {
            if (Directory.Exists(archiveDir))
            {
                return;
            }

            throw new DirectoryNotFoundException($"Neither the source nor its expected archive exists: {sourceDir}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(archiveDir)!);
        if (Directory.Exists(archiveDir))
        {
            VerifyDirectoryEquivalent(sourceDir, archiveDir);
            Directory.Delete(sourceDir, recursive: true);
            return;
        }

        try
        {
            if (forceCopy)
            {
                throw new IOException("Cross-filesystem archive fallback requested.");
            }
            Directory.Move(sourceDir, archiveDir);
            return;
        }
        catch (IOException) when (Directory.Exists(sourceDir) && !Directory.Exists(archiveDir))
        {
            // Directory.Move cannot cross filesystems. Copy into a private partial archive,
            // verify each file, then atomically publish it before deleting the source.
        }

        var partialArchiveDir = $"{archiveDir}.partial";
        if (Directory.Exists(partialArchiveDir))
        {
            Directory.Delete(partialArchiveDir, recursive: true);
        }

        CopyDirectory(sourceDir, partialArchiveDir);
        Directory.Move(partialArchiveDir, archiveDir);
        Directory.Delete(sourceDir, recursive: true);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir))
        {
            var destinationFile = Path.Combine(destinationDir, Path.GetFileName(sourceFile));
            FileSystemSafety.CopyAndVerify(sourceFile, destinationFile);
        }

        foreach (var sourceChildDir in Directory.EnumerateDirectories(sourceDir))
        {
            var destinationChildDir = Path.Combine(destinationDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceChildDir)));
            CopyDirectory(sourceChildDir, destinationChildDir);
        }
    }

    private static void VerifyDirectoryEquivalent(string sourceDir, string destinationDir)
    {
        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(sourceDir, path), StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(destinationDir, path), StringComparer.OrdinalIgnoreCase);
        if (!sourceFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(destinationFiles.Keys))
        {
            throw new IOException($"Source archive file list mismatch for '{sourceDir}'.");
        }

        foreach (var (relativePath, sourcePath) in sourceFiles)
        {
            var destinationPath = destinationFiles[relativePath];
            var sourceInfo = new FileInfo(sourcePath);
            FileSystemSafety.VerifySha256(destinationPath, sourceInfo.Length, FileSystemSafety.ComputeSha256(sourcePath));
        }
    }

    private static string SafePathToken(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));

    private static bool ShouldConvertBmp(string sourcePath, ScanOptions options) =>
        options.BmpToTiff
        && string.Equals(Path.GetExtension(sourcePath), ".bmp", StringComparison.OrdinalIgnoreCase);

    private void ConvertBmpToTiff(string sourcePath, string destinationPath)
    {
        try
        {
            imageConverter.ConvertBmpToTiff(sourcePath, destinationPath);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Could not convert BMP to TIFF: '{sourcePath}'.", exception);
        }
    }

    private static string CreateStagingDirectory(string destinationRoot, string directoryName)
    {
        var stagingDir = Path.Combine(destinationRoot, ".twincheck-staging", directoryName);
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        Directory.CreateDirectory(stagingDir);
        return stagingDir;
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrWhiteSpace(parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch
        {
            // Staging cleanup is best effort; the next operation recreates its private directory.
        }
    }

    private static ProcessScanResult ToResult(OperationManifest manifest) =>
        new(
            manifest.Ok,
            manifest.Files.Count(file => file.Kind == ScanFileKind.Image),
            manifest,
            manifest.Files.Where(file => file.Outcome == ScanFileOutcome.ConflictRenamed).ToArray(),
            manifest.Files.Where(file => file.Kind == ScanFileKind.Review).ToArray());

    private sealed record InputScanFile(string SourcePath, ScanFileKind Kind, int ImageNumber);
    private sealed record PreparedScanFile(ScanFileManifest Manifest, string StagedPath, bool AlreadyAtDestination);
}
