using System.Diagnostics;

namespace TwinCheck.Agent.Core;

/// <summary>
/// Either a finished result (a retry of a completed roll, or a dry run), or a roll processed on local disk
/// that still has to be uploaded.
/// </summary>
public sealed record IngestResult(ProcessScanResult? Completed, UploadRoll? Upload);

public sealed class ScanProcessor
{
    // Uploads force data to the NAS at this interval instead of in one flush when the file closes.
    private const long UploadFlushBytes = 64L * 1024 * 1024;
    private const long ProgressReportBytes = 64L * 1024 * 1024;
    private const long StagingHeadroomBytes = 1024L * 1024 * 1024;

    private readonly AgentConfigProvider configProvider;
    private readonly OperationStore operationStore;
    private readonly IScanImageConverter imageConverter;
    private readonly IScanMetadataWriter metadataWriter;
    private readonly DestinationWriteLock destinationWrites;
    private readonly LocalAgentLogger? logger;

    public ScanProcessor(
        AgentConfigProvider configProvider,
        OperationStore operationStore,
        IScanImageConverter imageConverter,
        IScanMetadataWriter? metadataWriter = null,
        DestinationWriteLock? destinationWrites = null,
        LocalAgentLogger? logger = null)
    {
        this.configProvider = configProvider;
        this.operationStore = operationStore;
        this.imageConverter = imageConverter;
        this.metadataWriter = metadataWriter ?? UnavailableScanMetadataWriter.Instance;
        this.destinationWrites = destinationWrites ?? new DestinationWriteLock();
        this.logger = logger;
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

    /// <summary>Test seam for the free-space check: returns the bytes available at a local folder.</summary>
    internal Func<string, long>? AvailableSpaceProbe { get; set; }

    internal DestinationWriteLock DestinationWrites => destinationWrites;

    /// <summary>Processes a roll locally, then uploads it inline. Used by the direct process endpoint and tests.</summary>
    public ProcessScanResult Process(ProcessScanRequest request, Action<ScanProgress>? onProgress = null)
    {
        var ingested = Ingest(request, onProgress);
        if (ingested.Completed is { } completed)
        {
            return completed;
        }

        var roll = ingested.Upload! with { Status = UploadStatuses.Uploading, StartedAt = DateTimeOffset.UtcNow };
        var files = roll.Files.ToList();
        var totalBytes = roll.TotalBytes;
        long uploadedBytes = 0;
        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Uploading, 0, files.Count, 0, totalBytes, "Uploading to the destination."));
        for (var index = 0; index < files.Count; index++)
        {
            var completedBefore = uploadedBytes;
            files[index] = UploadFile(files[index], delta =>
            {
                uploadedBytes += delta;
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Uploading, index, files.Count, uploadedBytes, totalBytes));
            });
            uploadedBytes = completedBefore + files[index].Size;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Uploading, index + 1, files.Count, uploadedBytes, totalBytes));
        }

        return FinishUpload(roll with { Files = files }).Result;
    }

    /// <summary>
    /// Copies the roll to local staging, converts it, writes metadata and verifies it, all on local disk.
    /// Then it renames the roll folder to <c>_processed-{order}-{roll}</c> so the watch folder is clear for
    /// the next roll. <paramref name="beforeHandoff"/> runs just before that rename, so the caller can record
    /// the roll durably first.
    /// </summary>
    public IngestResult Ingest(
        ProcessScanRequest request,
        Action<ScanProgress>? onProgress = null,
        Action<UploadRoll>? beforeHandoff = null)
    {
        var config = configProvider.Current;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        }

        var profile = config.Profiles.SingleOrDefault(profile => profile.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        var effectiveOptions = request.Options ?? profile.Options ?? new ScanOptions();
        // Scanner identity and fallback artist are owned by the local profile, never request.Options.
        effectiveOptions = effectiveOptions with { Exif = profile.Options?.Exif };
        var photoMetadata = PhotoMetadata.Resolve(request.Metadata, profile.Options?.Exif);
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
            return new IngestResult(
                existingManifest.CompletedAt is not null
                    ? ToResult(existingManifest)
                    : CompletePendingManifest(existingManifest, onProgress),
                null);
        }

        var sourceDir = FileSystemSafety.EnsureInsideAnyRoot(
            request.SourceDir ?? profile.SourceDir,
            config.AllowedSourceRoots,
            "source");

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");
        }

        sourceDir = ResolveRollFolder(sourceDir, profile);

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
        if (imageFiles.Length == 0)
        {
            throw new InvalidOperationException($"Source directory contains no image files: {sourceDir}");
        }

        var reviewFiles = files.Except(imageFiles).ToArray();
        var totalBytes = files.Sum(path => new FileInfo(path).Length);
        var fileCount = files.Length;
        var stagingRoot = config.ResolveStagingRoot();
        var namingPattern = request.Naming ?? profile.NamingPattern;
        var reviewDir = Path.Combine(destinationRoot, "_review", SafePathToken(request.IdempotencyKey));

        if (request.DryRun)
        {
            return new IngestResult(DryRun(), null);
        }

        EnsureStagingSpace(stagingRoot, files);
        var stagingDir = CreateStagingDirectory(stagingRoot, SafePathToken(request.IdempotencyKey));
        var uploadFiles = new List<UploadFile>();
        var manifestsForFailure = new List<ScanFileManifest>();
        var handedOff = false;

        try
        {
            var inputs = imageFiles.Select((path, index) => new InputScanFile(path, ScanFileKind.Image, index + 1))
                .Concat(reviewFiles.Select(path => new InputScanFile(path, ScanFileKind.Review, 0)))
                .ToArray();
            long copiedBytes = 0;
            long lastReportedBytes = 0;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, 0, fileCount, 0, totalBytes, "Copying source files to local staging while calculating checksums."));

            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                var frame = $"frame {index + 1}/{fileCount}";
                var timer = Stopwatch.StartNew();
                var sourceExtension = Path.GetExtension(input.SourcePath).ToLowerInvariant();
                var stagedSourcePath = Path.Combine(stagingDir, $"{index + 1:D6}-source{sourceExtension}");
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index, fileCount, copiedBytes, totalBytes, $"Copying {frame} to local staging."));
                var copied = FileSystemSafety.CopyWithSha256(input.SourcePath, stagedSourcePath, delta =>
                {
                    copiedBytes += delta;
                    if (copiedBytes - lastReportedBytes >= ProgressReportBytes)
                    {
                        lastReportedBytes = copiedBytes;
                        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index, fileCount, copiedBytes, totalBytes));
                    }
                });
                var copySeconds = timer.Elapsed.TotalSeconds;

                var stagedOutputPath = stagedSourcePath;
                var outputExtension = sourceExtension;
                var outputSize = copied.Length;
                var outputHash = copied.Sha256;
                string? conversion = null;
                double? convertSeconds = null;
                if (input.Kind == ScanFileKind.Image && ShouldConvertBmp(input.SourcePath, effectiveOptions))
                {
                    timer.Restart();
                    onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index, fileCount, copiedBytes, totalBytes, $"Converting {frame} to TIFF."));
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
                    convertSeconds = timer.Elapsed.TotalSeconds;
                }

                double? metadataSeconds = null;
                if (input.Kind == ScanFileKind.Image && photoMetadata.HasValues)
                {
                    timer.Restart();
                    onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index, fileCount, copiedBytes, totalBytes, $"Writing metadata to {frame}."));
                    try
                    {
                        metadataWriter.Write(stagedOutputPath, photoMetadata);
                        outputSize = new FileInfo(stagedOutputPath).Length;
                        outputHash = FileSystemSafety.ComputeSha256(stagedOutputPath);
                    }
                    catch (Exception exception)
                    {
                        manifestsForFailure.Add(new ScanFileManifest
                        {
                            SourcePath = input.SourcePath, FileName = Path.GetFileName(input.SourcePath),
                            SourceSize = copied.Length, SourceSha256 = copied.Sha256, Conversion = conversion,
                            Kind = input.Kind, Outcome = ScanFileOutcome.Failed,
                            Message = $"Photo metadata write failed: {exception.Message}"
                        });
                        throw new ScanMetadataWriteException($"Could not write photo metadata for '{Path.GetFileName(input.SourcePath)}': {exception.Message}", exception);
                    }
                    metadataSeconds = timer.Elapsed.TotalSeconds;
                }

                logger?.Info($"Scan operation {request.IdempotencyKey} {frame} {Path.GetFileName(input.SourcePath)}: "
                    + $"copy {copySeconds:F1}s"
                    + (convertSeconds is { } convert ? $", convert {convert:F1}s" : "")
                    + (metadataSeconds is { } metadata ? $", metadata {metadata:F1}s" : "")
                    + ".");

                var file = new UploadFile
                {
                    Sequence = index + 1,
                    Kind = input.Kind,
                    SourcePath = input.SourcePath,
                    FileName = Path.GetFileName(input.SourcePath),
                    StagedPath = stagedOutputPath,
                    PlannedPath = input.Kind == ScanFileKind.Image
                        ? PlanImagePath(finalDir, namingPattern, request.OrderNumber, request.RollNumber, input.ImageNumber, outputExtension)
                        : Path.Combine(reviewDir, Path.GetFileName(input.SourcePath)),
                    SourceSize = copied.Length,
                    SourceSha256 = copied.Sha256,
                    Size = outputSize,
                    Sha256 = outputHash,
                    Conversion = conversion
                };
                uploadFiles.Add(file);
                manifestsForFailure.Add(file.ToManifest());
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Copying, index + 1, fileCount, copiedBytes, totalBytes));
            }

            var stagedBytes = uploadFiles.Sum(file => file.Size);
            long verifiedBytes = 0;
            long lastVerifiedReport = 0;
            onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, 0, fileCount, 0, stagedBytes, "Verifying staged checksums."));
            for (var index = 0; index < uploadFiles.Count; index++)
            {
                var file = uploadFiles[index];
                FileSystemSafety.VerifySha256(file.StagedPath, file.Size, file.Sha256, delta =>
                {
                    verifiedBytes += delta;
                    if (verifiedBytes - lastVerifiedReport >= ProgressReportBytes)
                    {
                        lastVerifiedReport = verifiedBytes;
                        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, index, fileCount, verifiedBytes, stagedBytes));
                    }
                });
                onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Verifying, index + 1, fileCount, verifiedBytes, stagedBytes));
            }

            var archiveDir = ResolveProcessedFolder(sourceDir, request.OrderNumber, request.RollNumber);
            var roll = new UploadRoll
            {
                IdempotencyKey = request.IdempotencyKey,
                ProfileId = request.ProfileId,
                OrderNumber = request.OrderNumber,
                RollNumber = request.RollNumber,
                SourceDir = sourceDir,
                SourceArchiveDir = archiveDir,
                StagingDir = stagingDir,
                FinalDir = finalDir,
                DestinationRoot = destinationRoot,
                Manifest = BuildManifest(request, sourceDir, destinationRoot, finalDir, false, startedAt, null, [], effectiveOptions, sourceArchiveDir: archiveDir),
                Files = uploadFiles,
                Status = UploadStatuses.Handoff
            };
            beforeHandoff?.Invoke(roll);

            // Reserve the final folder (and with it the rescan number), then clear the watch folder.
            destinationWrites.Run(() => Directory.CreateDirectory(finalDir));
            Directory.Move(sourceDir, archiveDir);
            handedOff = true;
            logger?.Info($"Scan operation {request.IdempotencyKey} processed locally; renamed the roll folder to {archiveDir}.");
            return new IngestResult(null, roll with { Status = UploadStatuses.Waiting });
        }
        catch
        {
            if (!handedOff)
            {
                var failedManifest = BuildManifest(
                    request,
                    sourceDir,
                    destinationRoot,
                    finalDir,
                    false,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    manifestsForFailure.Select(file => file with { Outcome = ScanFileOutcome.Failed }).ToArray(),
                    effectiveOptions,
                    warnings: ["Operation failed before upload; source originals remain in place."]);

                TryDeleteDirectory(stagingDir);
                try
                {
                    destinationWrites.Run(() => operationStore.WriteManifest(failedManifest));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Keep the processing error; the failure record is only a convenience on the destination.
                    logger?.Warning($"Could not write the failure manifest for {request.IdempotencyKey}: {exception.Message}");
                }
            }

            throw;
        }

        ProcessScanResult DryRun()
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
                            stagingRoot,
                            $"dry-run-{SafePathToken(request.IdempotencyKey)}-{Guid.NewGuid():N}");
                        outputPath = Path.Combine(dryRunStagingDir, $"{index + 1:D6}.tif");
                        ConvertBmpToTiff(sourcePath, outputPath);
                        outputExtension = ".tif";
                        conversion = ScanFileConversions.BmpToTiff;
                    }

                    var outputSize = new FileInfo(outputPath).Length;
                    var outputHash = conversion is null ? sourceHash : FileSystemSafety.ComputeSha256(outputPath);
                    hashedBytes += sourceSize;
                    var plannedPath = ResolveImageCollision(
                        PlanImagePath(finalDir, namingPattern, request.OrderNumber, request.RollNumber, index + 1, outputExtension),
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
                        DestinationPath = Path.Combine(reviewDir, Path.GetFileName(sourcePath)),
                        FileName = Path.GetFileName(sourcePath),
                        SourceSize = size,
                        SourceSha256 = sourceHash,
                        Size = size,
                        Sha256 = sourceHash,
                        Kind = ScanFileKind.Review,
                        Outcome = ScanFileOutcome.Planned,
                        Message = ReviewMessage
                    });
                    onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Hashing, manifests.Count, fileCount, hashedBytes, totalBytes));
                }

                return ToResult(BuildManifest(request, sourceDir, destinationRoot, finalDir, true, startedAt, null, manifests, effectiveOptions));
            }
            finally
            {
                TryDeleteDirectory(dryRunStagingDir);
            }
        }
    }

    /// <summary>
    /// Uploads one staged file: copy to a hidden partial name, verify it on the destination, then rename.
    /// Safe to repeat after a crash at any point.
    /// </summary>
    /// <param name="onPlanned">Called with the chosen final name before the rename, so it survives a crash.</param>
    internal UploadFile UploadFile(UploadFile file, Action<long>? onBytes = null, Action<UploadFile>? onPlanned = null)
    {
        if (file.State == UploadFileStates.Uploaded)
        {
            return file;
        }

        return destinationWrites.Run(() =>
        {
            if (file.State == UploadFileStates.Publishing
                && file.DestinationPath is { } chosen
                && File.Exists(chosen)
                && string.Equals(FileSystemSafety.ComputeSha256(chosen), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The previous attempt finished the rename but not the bookkeeping.
                TryDeleteFile(file.StagedPath);
                return file with { State = UploadFileStates.Uploaded };
            }

            if (!File.Exists(file.StagedPath))
            {
                throw new UploadStagedFileException($"The local copy of {file.FileName} is missing: {file.StagedPath}");
            }

            try
            {
                FileSystemSafety.VerifySha256(file.StagedPath, file.Size, file.Sha256);
            }
            catch (IOException exception)
            {
                throw new UploadStagedFileException($"The local copy of {file.FileName} is damaged ({exception.Message}): {file.StagedPath}");
            }

            var destination = file.Kind == ScanFileKind.Image
                ? ResolveImageCollision(file.PlannedPath, file.Sha256)
                : ResolveReviewCollision(file.PlannedPath, file.Sha256);
            var planned = file with
            {
                State = UploadFileStates.Publishing,
                DestinationPath = destination.Path,
                Outcome = file.Kind == ScanFileKind.Review ? ScanFileOutcome.MovedToReview : destination.Outcome,
                Message = file.Kind == ScanFileKind.Review ? ReviewMessage : destination.Message
            };
            if (destination.Outcome == ScanFileOutcome.AlreadyDone)
            {
                TryDeleteFile(file.StagedPath);
                return planned with { State = UploadFileStates.Uploaded };
            }

            onPlanned?.Invoke(planned);
            var directory = Path.GetDirectoryName(destination.Path)!;
            Directory.CreateDirectory(directory);
            var partialPath = Path.Combine(directory, $".{Path.GetFileName(destination.Path)}.twincheck-partial");
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            try
            {
                var copied = FileSystemSafety.CopyWithSha256(file.StagedPath, partialPath, onBytes, UploadFlushBytes);
                if (copied.Length != file.Size || !string.Equals(copied.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"The local copy of {file.FileName} changed during upload.");
                }

                FileSystemSafety.VerifySha256(partialPath, file.Size, file.Sha256);
                File.Move(partialPath, destination.Path, overwrite: false);
            }
            catch
            {
                TryDeleteFile(partialPath);
                throw;
            }

            TryDeleteFile(file.StagedPath);
            return planned with { State = UploadFileStates.Uploaded };
        });
    }

    /// <summary>
    /// Writes the completed manifest, then deletes the <c>_processed-…</c> folder and the local staging copy.
    /// The folder is kept, with a warning, when it holds anything that was not uploaded.
    /// </summary>
    internal (ProcessScanResult Result, UploadRoll Roll) FinishUpload(UploadRoll roll, Action<UploadRoll>? onManifestWritten = null)
    {
        OperationManifest manifest;
        if (roll.ManifestWritten)
        {
            manifest = operationStore.TryReadManifest(roll.FinalDir, roll.IdempotencyKey) ?? BuildCompletedManifest(roll);
        }
        else
        {
            manifest = BuildCompletedManifest(roll);
            destinationWrites.Run(() => operationStore.WriteManifest(manifest));
            roll = roll with { ManifestWritten = true, CompletedAt = manifest.CompletedAt };
            onManifestWritten?.Invoke(roll);
        }

        var warning = destinationWrites.Run(() => CleanUpProcessedFolder(roll));
        if (warning is not null)
        {
            logger?.Warning($"Scan operation {roll.IdempotencyKey}: {warning}");
        }

        TryDeleteDirectory(roll.StagingDir);
        return (ToResult(manifest), roll with { CleanupWarning = warning });
    }

    private static OperationManifest BuildCompletedManifest(UploadRoll roll)
    {
        var files = roll.Files.Select(file => file.ToManifest()).ToArray();
        return roll.Manifest with
        {
            Files = files,
            Ok = files.All(file => file.Outcome != ScanFileOutcome.Failed),
            SourceArchiveDir = roll.SourceArchiveDir,
            CompletedAt = roll.CompletedAt ?? DateTimeOffset.UtcNow
        };
    }

    private static string? CleanUpProcessedFolder(UploadRoll roll)
    {
        var folder = roll.SourceArchiveDir;
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var uploadedNames = roll.Files.Select(file => Path.GetFileName(file.SourcePath)).ToHashSet(FileNameComparer);
        var leftovers = Directory.EnumerateDirectories(folder)
            .Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + Path.DirectorySeparatorChar)
            .Concat(Directory.EnumerateFiles(folder)
                .Where(path => !FileSystemSafety.IsIgnoredControlFile(path) && !uploadedNames.Contains(Path.GetFileName(path)))
                .Select(Path.GetFileName))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (leftovers.Length > 0)
        {
            const int listed = 5;
            var names = string.Join(", ", leftovers.Take(listed)) + (leftovers.Length > listed ? $", and {leftovers.Length - listed} more" : "");
            return $"Kept {folder} because it contains items that were not uploaded: {names}. Review it in Cleanup.";
        }

        Directory.Delete(folder, recursive: true);
        return null;
    }

    private ProcessScanResult CompletePendingManifest(OperationManifest manifest, Action<ScanProgress>? onProgress)
    {
        // Recovers a roll left pending by an agent version that archived sources into _processed/.
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
        destinationWrites.Run(() => ArchiveSourceFolder(manifest.SourceDir, archiveDir));

        var completed = manifest with
        {
            SourceArchiveDir = archiveDir,
            CompletedAt = DateTimeOffset.UtcNow
        };
        destinationWrites.Run(() => operationStore.WriteManifest(completed));
        onProgress?.Invoke(new ScanProgress(ScanOperationPhases.Archiving, files.Length, files.Length, totalBytes, totalBytes));
        return ToResult(completed);
    }

    private void EnsureStagingSpace(string stagingRoot, IReadOnlyCollection<string> files)
    {
        Directory.CreateDirectory(stagingRoot);
        var sizes = files.Select(path => new FileInfo(path).Length).ToArray();
        // Room for the roll, one extra file for a conversion or metadata rewrite in progress, and headroom.
        var required = sizes.Sum() + sizes.DefaultIfEmpty(0).Max() + StagingHeadroomBytes;
        var available = (AvailableSpaceProbe ?? (path => new DriveInfo(path).AvailableFreeSpace))(stagingRoot);
        if (available < required)
        {
            throw new IOException(
                $"Not enough local disk space to process this roll: {FormatBytes(required)} needed in {stagingRoot}, "
                + $"{FormatBytes(available)} free. The roll was left in place.");
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
            : $"{bytes / (1024.0 * 1024):F0} MB";

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

    private const string ReviewMessage = "Not a supported image extension; routed to review instead of deleting.";

    private static string PlanImagePath(
        string finalDir,
        string namingPattern,
        string orderNumber,
        string rollNumber,
        int imageNumber,
        string extension)
    {
        var baseName = namingPattern
            .Replace("{orderNumber}", orderNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{rollNumber}", rollNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{imgNumber}", imageNumber.ToString(), StringComparison.OrdinalIgnoreCase);
        return Path.Combine(finalDir, baseName + extension.ToLowerInvariant());
    }

    private static (string Path, ScanFileOutcome Outcome, string? Message) ResolveImageCollision(string candidate, string hash)
    {
        if (!File.Exists(candidate))
        {
            return (candidate, ScanFileOutcome.Copied, null);
        }

        if (string.Equals(FileSystemSafety.ComputeSha256(candidate), hash, StringComparison.OrdinalIgnoreCase))
        {
            return (candidate, ScanFileOutcome.AlreadyDone, "Destination already contains an identical file.");
        }

        var directory = Path.GetDirectoryName(candidate)!;
        var baseName = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var version = 2; version < 1000; version++)
        {
            var versioned = Path.Combine(directory, $"{baseName}-v{version}{extension}");
            if (!File.Exists(versioned))
            {
                return (versioned, ScanFileOutcome.ConflictRenamed, $"Destination name exists with different content; wrote version {version}.");
            }
        }

        throw new IOException($"Could not resolve a collision-free destination for '{candidate}'.");
    }

    private static (string Path, ScanFileOutcome Outcome, string? Message) ResolveReviewCollision(string candidate, string hash)
    {
        if (!File.Exists(candidate))
        {
            return (candidate, ScanFileOutcome.MovedToReview, null);
        }

        if (string.Equals(FileSystemSafety.ComputeSha256(candidate), hash, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Rolls are always folders inside the watch folder. Given the watch folder, picks its only roll;
    /// loose files in the watch folder are never processed.
    /// </summary>
    private static string ResolveRollFolder(string sourceDir, ScannerProfile profile)
    {
        var configuredRoot = Path.GetFullPath(profile.SourceDir);
        var watchRoot = Path.GetFullPath(SourceCandidateService.ResolveCandidateRoot(profile));
        if (!PathsEqual(sourceDir, configuredRoot) && !PathsEqual(sourceDir, watchRoot))
        {
            if (FileSystemSafety.IsProcessedFolder(sourceDir))
            {
                throw new InvalidOperationException($"This roll folder was already processed and is waiting for its upload: {sourceDir}");
            }

            return sourceDir;
        }

        if (!Directory.Exists(watchRoot))
        {
            throw new DirectoryNotFoundException($"Watch folder does not exist: {watchRoot}");
        }

        var candidateDirs = Directory.EnumerateDirectories(watchRoot)
            .Where(path => !FileSystemSafety.IsProcessedFolder(path))
            .Select(path => new
            {
                Path = Path.GetFullPath(path),
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
                watchRoot,
                $"Source root contains multiple candidate roll folders. Select the exact roll folder before processing: {names}");
        }

        throw new InvalidOperationException(
            $"Watch folder contains no roll folders with images: {watchRoot}. Scans must be saved in a folder inside the watch folder.");
    }

    /// <summary>The roll folder's new name beside it in the watch folder: <c>_processed-{order}-{roll}</c>.</summary>
    private static string ResolveProcessedFolder(string sourceDir, string orderNumber, string rollNumber)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourceDir))
            ?? throw new IOException($"Roll folder has no parent folder: {sourceDir}");
        var baseName = $"{FileSystemSafety.ProcessedFolderPrefix}{SafePathToken(orderNumber)}-{SafePathToken(rollNumber)}";
        var candidate = Path.Combine(parent, baseName);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            if (suffix >= 1000)
            {
                throw new IOException($"Could not find a free name for the processed roll folder in {parent}.");
            }

            candidate = Path.Combine(parent, $"{baseName}-{suffix}");
        }

        return candidate;
    }

    private static string BuildSourceArchiveDirectory(string sourceDir, string destinationRoot, string idempotencyKey)
    {
        var processedRoot = Path.Combine(destinationRoot, "_processed");
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        return Path.Combine(processedRoot, $"{sourceName}-{SafePathToken(idempotencyKey)}");
    }

    /// <summary>Moves a source into the legacy <c>_processed/</c> archive; only used to recover older rolls.</summary>
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
            FileSystemSafety.CopyAndVerify(sourceFile, destinationFile, UploadFlushBytes);
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

    internal static string SafePathToken(string value) =>
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

    private static string CreateStagingDirectory(string stagingRoot, string directoryName)
    {
        var stagingDir = Path.Combine(stagingRoot, directoryName);
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
        }
        catch
        {
            // Staging cleanup is best effort; the next operation recreates its private directory.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover local copy is removed with the roll's staging folder.
        }
    }

    private static ProcessScanResult ToResult(OperationManifest manifest) =>
        new(
            manifest.Ok,
            manifest.Files.Count(file => file.Kind == ScanFileKind.Image),
            manifest,
            manifest.Files.Where(file => file.Outcome == ScanFileOutcome.ConflictRenamed).ToArray(),
            manifest.Files.Where(file => file.Kind == ScanFileKind.Review).ToArray());

    private static bool PathsEqual(string left, string right) =>
        FileNameComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static StringComparer FileNameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record InputScanFile(string SourcePath, ScanFileKind Kind, int ImageNumber);
}
