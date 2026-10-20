using System.Collections.Concurrent;
using System.Diagnostics;

namespace TwinCheck.Agent.Core;

public static class ScanOperationStatuses
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) => status is Completed or Failed or Cancelled;
}

public static class ScanOperationPhases
{
    public const string Queued = "queued";
    public const string Watching = "watching";
    public const string Settling = "settling";
    public const string Hashing = "hashing";
    public const string Copying = "copying";
    public const string Verifying = "verifying";
    public const string Finalizing = "finalizing";
    public const string Archiving = "archiving";
    /// <summary>Processed on local disk; files are going to the destination through the upload queue.</summary>
    public const string Uploading = "uploading";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record ScanProgress(
    string Phase,
    int FilesCompleted,
    int FileCount,
    long BytesCompleted,
    long TotalBytes,
    string? Message = null);

public sealed record ScanOperationState
{
    public required string OperationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestFingerprint { get; init; }
    public required ProcessScanRequest Request { get; init; }
    public string Status { get; init; } = ScanOperationStatuses.Queued;
    public string Phase { get; init; } = ScanOperationPhases.Queued;
    public int FilesCompleted { get; init; }
    public int FileCount { get; init; }
    public long BytesCompleted { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorSourceDir { get; init; }
    public string? ResolvedSourceDir { get; init; }
    public SourceCandidate? Candidate { get; init; }
    public ProcessScanResult? Result { get; init; }

    public ScanOperationView ToView() =>
        new(
            OperationId,
            IdempotencyKey,
            Status,
            Phase,
            FilesCompleted,
            FileCount,
            BytesCompleted,
            TotalBytes,
            CreatedAt,
            StartedAt,
            CompletedAt,
            Message,
            Error,
            ErrorCode,
            ErrorSourceDir,
            ResolvedSourceDir,
            Candidate,
            Result,
            Request.ProfileId,
            Request.OrderNumber,
            Request.RollNumber);

    public ScanJobSummary ToSummary() => new(
        IdempotencyKey,
        Request.ProfileId,
        Request.OrderNumber,
        Request.RollNumber,
        Status,
        Phase,
        CreatedAt,
        CompletedAt,
        Message,
        Error,
        Candidate?.Path,
        ResolvedSourceDir,
        Result?.Manifest.FinalDir ?? Candidate?.DestinationPreview,
        FilesCompleted,
        FileCount);
}

public sealed record ScanOperationView(
    string OperationId,
    string IdempotencyKey,
    string Status,
    string Phase,
    int FilesCompleted,
    int FileCount,
    long BytesCompleted,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message,
    string? Error,
    string? ErrorCode,
    string? ErrorSourceDir,
    string? ResolvedSourceDir,
    SourceCandidate? Candidate,
    ProcessScanResult? Result,
    string ProfileId,
    string OrderNumber,
    string RollNumber);

public sealed record ScanJobSummary(
    string IdempotencyKey,
    string ProfileId,
    string OrderNumber,
    string RollNumber,
    string Status,
    string Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Message,
    string? Error,
    string? CandidatePath,
    string? ResolvedSourceDir,
    string? DestinationDir,
    int FilesCompleted,
    int FileCount);

public sealed record EnqueueScanOperationResult(ScanOperationState Operation, bool Created, bool Conflict);

public sealed class ScanOperationService : IDisposable
{
    private static readonly TimeSpan IngestStallAfter = TimeSpan.FromMinutes(5);

    private readonly ScanOperationStore store;
    private readonly ScanProcessor processor;
    private readonly ScanWatchService watchService;
    private readonly ScanReadinessCoordinator readinessCoordinator;
    private readonly LocalAgentLogger logger;
    private readonly UploadQueueService uploadQueue;
    private readonly bool ownsUploadQueue;
    private readonly StallMonitor ingestStalls;
    private readonly ConcurrentDictionary<string, Task> running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> operationCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> profileGates = new(StringComparer.Ordinal);
    // One roll is processed locally at a time across all scanners, so the NAS serves one reader at most.
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly object executionGate = new();
    private readonly Task maintenance;

    public ScanOperationService(
        ScanOperationStore store,
        ScanProcessor processor,
        ScanWatchService watchService,
        LocalAgentLogger logger)
        : this(store, processor, watchService, logger, new UploadQueueService(store, processor, watchService, logger), ownsUploadQueue: true)
    {
    }

    public ScanOperationService(
        ScanOperationStore store,
        ScanProcessor processor,
        ScanWatchService watchService,
        LocalAgentLogger logger,
        UploadQueueService uploadQueue)
        : this(store, processor, watchService, logger, uploadQueue, ownsUploadQueue: false)
    {
    }

    private ScanOperationService(
        ScanOperationStore store,
        ScanProcessor processor,
        ScanWatchService watchService,
        LocalAgentLogger logger,
        UploadQueueService uploadQueue,
        bool ownsUploadQueue)
    {
        this.store = store;
        this.processor = processor;
        this.watchService = watchService;
        readinessCoordinator = watchService.ReadinessCoordinator;
        this.logger = logger;
        this.uploadQueue = uploadQueue;
        this.ownsUploadQueue = ownsUploadQueue;
        ingestStalls = new StallMonitor(IngestStallAfter, TimeSpan.FromSeconds(30), OnIngestStalled, _ => { });

        foreach (var operation in store.ListActive())
        {
            if (operation.Phase == ScanOperationPhases.Uploading)
            {
                // The upload queue resumes these by itself.
                if (uploadQueue.Get(operation.IdempotencyKey) is null)
                {
                    FailOperation(operation, "The upload record for this roll is missing, so it can't be resumed.", "upload-failed", operation.ResolvedSourceDir);
                }
                continue;
            }

            store.Update(operation.IdempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
            {
                Status = ScanOperationStatuses.Queued,
                Message = "Recovered after agent restart; scan workflow resumed."
            });
            Schedule(operation.IdempotencyKey);
        }
        maintenance = RunMaintenanceAsync();
    }

    public UploadQueueService Uploads => uploadQueue;

    /// <summary>Jobs copying, converting or verifying on local disk. Updates wait for these to finish.</summary>
    public int LocalProcessingCount() =>
        store.ListActive().Count(operation => operation.Status == ScanOperationStatuses.Processing
            && operation.Phase is ScanOperationPhases.Hashing or ScanOperationPhases.Copying or ScanOperationPhases.Verifying
                or ScanOperationPhases.Finalizing or ScanOperationPhases.Archiving);

    public EnqueueScanOperationResult Enqueue(ProcessScanRequest request)
    {
        var result = store.Enqueue(request);
        if (result.Created)
        {
            Schedule(result.Operation.IdempotencyKey);
        }

        return result;
    }

    public ScanOperationState Get(string idempotencyKey) => store.Get(idempotencyKey);

    public IReadOnlyList<ScanOperationView> ListActive() =>
        store.ListActive()
            .Select(operation => operation.ToView())
            .ToArray();

    public IReadOnlyList<ScanJobSummary> ListRecent(int limit = 100) => store.ListRecent(limit);

    public ScanOperationView? Cancel(string idempotencyKey)
    {
        lock (executionGate)
        {
            var current = store.Get(idempotencyKey);
            if (current.Status == ScanOperationStatuses.Cancelled)
            {
                return current.ToView();
            }

            if (ScanOperationStatuses.IsTerminal(current.Status)
                || current.Phase is not (ScanOperationPhases.Queued or ScanOperationPhases.Watching or ScanOperationPhases.Settling))
            {
                return null;
            }

            var cancelled = store.Update(idempotencyKey, operation => operation with
            {
                Status = ScanOperationStatuses.Cancelled,
                Phase = ScanOperationPhases.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "Scan operation cancelled before file processing.",
                Error = "Scan operation cancelled before file processing.",
                ErrorCode = "cancelled"
            });
            if (operationCancellations.TryGetValue(idempotencyKey, out var source))
            {
                source.Cancel();
            }
            if (!string.IsNullOrWhiteSpace(current.Request.WatchId))
            {
                try
                {
                    watchService.Cancel(current.Request.WatchId);
                }
                catch (InvalidOperationException)
                {
                    // The independent watch may already have completed or been removed.
                }
            }
            logger.Info($"Scan operation {idempotencyKey} cancelled before file processing.");
            return cancelled.ToView();
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            Task.WaitAll(running.Values.Append(maintenance).ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The persisted operation will be recovered on the next start.
        }
        ingestStalls.Dispose();
        if (ownsUploadQueue) uploadQueue.Dispose();
        cancellation.Dispose();
    }

    private async Task RunMaintenanceAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                try
                {
                    store.Cleanup();
                }
                catch (Exception exception)
                {
                    logger.Error("Job history cleanup failed; it will be retried next hour.", exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private void Schedule(string idempotencyKey)
    {
        var operationCancellation = operationCancellations.GetOrAdd(
            idempotencyKey, _ => CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token));
        running.GetOrAdd(idempotencyKey, key => Task.Run(async () =>
        {
            try
            {
                await RunWorkflow(key, operationCancellation.Token);
            }
            finally
            {
                running.TryRemove(key, out _);
                operationCancellations.TryRemove(key, out _);
                operationCancellation.Dispose();
            }
        }));
    }

    private async Task RunWorkflow(string idempotencyKey, CancellationToken operationToken)
    {
        var operation = store.Get(idempotencyKey);
        if (ScanOperationStatuses.IsTerminal(operation.Status))
        {
            return;
        }

        var profileGate = profileGates.GetOrAdd(operation.Request.ProfileId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await profileGate.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            operationToken.ThrowIfCancellationRequested();
            await PrepareAndProcess(operation, operationToken);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (!ScanOperationStatuses.IsTerminal(store.Get(idempotencyKey).Status))
            {
                store.Update(idempotencyKey, current => current with
                {
                    Status = ScanOperationStatuses.Queued,
                    Message = "Agent stopped; this workflow will resume after restart."
                });
            }
        }
        finally
        {
            profileGate.Release();
        }
    }

    private async Task PrepareAndProcess(ScanOperationState originalOperation, CancellationToken operationToken)
    {
        var idempotencyKey = originalOperation.IdempotencyKey;
        var operation = store.Get(idempotencyKey);
        var effectiveRequest = operation.Request;

        try
        {
            if (ShouldWaitForReadiness(operation))
            {
                operationToken.ThrowIfCancellationRequested();
                store.Update(idempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
                {
                    Status = ScanOperationStatuses.Processing,
                    Phase = ScanOperationPhases.Watching,
                    StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                    Message = "Starting scanner watch."
                });
                var ready = await readinessCoordinator.WaitForReadyAsync(
                    operation.Request,
                    progress => store.Update(idempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
                    {
                        Status = ScanOperationStatuses.Processing,
                        Phase = progress.Phase,
                        Candidate = progress.Candidate,
                        Message = progress.Message
                    }),
                    operationToken);

                operationToken.ThrowIfCancellationRequested();
                operation = store.Update(idempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
                {
                    Status = ScanOperationStatuses.Queued,
                    Phase = ScanOperationPhases.Queued,
                    Candidate = ready,
                    ResolvedSourceDir = ready.Path,
                    Message = $"Scanner output ready: {ready.ImageCount} image(s)."
                });
                effectiveRequest = operation.Request with { SourceDir = ready.Path };
            }
            else if (!string.IsNullOrWhiteSpace(operation.ResolvedSourceDir))
            {
                effectiveRequest = operation.Request with { SourceDir = operation.ResolvedSourceDir };
            }

            await processingGate.WaitAsync(operationToken);
            try
            {
                lock (executionGate)
                {
                    operationToken.ThrowIfCancellationRequested();
                    store.Update(idempotencyKey, current => current with
                    {
                        Status = ScanOperationStatuses.Processing,
                        Phase = ScanOperationPhases.Hashing,
                        Message = "Scan processing started."
                    });
                }
                ProcessReadyOperation(operation, effectiveRequest);
            }
            finally
            {
                processingGate.Release();
            }
        }
        catch (ScanReadinessException exception)
        {
            var current = store.Get(idempotencyKey);
            FailOperation(current, exception.Message, exception.Code, exception.SourceDir ?? current.Candidate?.Path);
        }
        catch (ScanMetadataWriteException exception)
        {
            var current = store.Get(idempotencyKey);
            FailOperation(current, exception.Message, "metadata-write-failed", current.ResolvedSourceDir ?? current.Candidate?.Path);
            logger.Error($"Photo metadata failed for scan operation {idempotencyKey}.", exception);
        }
        catch (MultipleSourceCandidatesException exception)
        {
            FailOperation(operation, exception.Message, "multiple-source-candidates", exception.SourceDir);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var current = store.Get(idempotencyKey);
            FailOperation(current, exception.Message, null, current.ResolvedSourceDir ?? current.Candidate?.Path);
            logger.Error($"Scan operation {idempotencyKey} failed.", exception);
        }
    }

    private void ProcessReadyOperation(ScanOperationState operation, ProcessScanRequest effectiveRequest)
    {
        var idempotencyKey = operation.IdempotencyKey;
        var existingUpload = uploadQueue.Get(idempotencyKey);
        if (existingUpload is not null && existingUpload.Status != UploadStatuses.Handoff)
        {
            // Processed locally before a restart: the upload queue owns this roll now.
            if (!UploadStatuses.IsFinished(existingUpload.Status))
            {
                MarkUploading(idempotencyKey, existingUpload, "Processed locally; waiting to upload to the destination.");
                uploadQueue.Wake();
            }
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var lastPhase = store.Get(idempotencyKey).Phase;
        store.Update(idempotencyKey, current => current with
        {
            Status = ScanOperationStatuses.Processing,
            StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
            Message = "Scan processing started."
        });
        watchService.MarkProcessing(effectiveRequest.WatchId, idempotencyKey);
        logger.Info($"Scan operation {idempotencyKey} started.");

        IngestResult ingested;
        try
        {
            ingestStalls.Touch(idempotencyKey, "starting local processing");
            ingested = processor.Ingest(effectiveRequest, progress =>
            {
                if (!string.Equals(lastPhase, progress.Phase, StringComparison.Ordinal))
                {
                    logger.Info($"Scan operation {idempotencyKey} phase {lastPhase} completed after {stopwatch.Elapsed}.");
                    lastPhase = progress.Phase;
                    stopwatch.Restart();
                }

                ingestStalls.Touch(idempotencyKey, progress.Message ?? $"{progress.Phase} {progress.FilesCompleted}/{progress.FileCount}");
                store.Update(idempotencyKey, current => current with
                {
                    Status = ScanOperationStatuses.Processing,
                    Phase = progress.Phase,
                    FilesCompleted = progress.FilesCompleted,
                    FileCount = progress.FileCount,
                    BytesCompleted = progress.BytesCompleted,
                    TotalBytes = progress.TotalBytes,
                    Message = progress.Message ?? current.Message
                });
            }, uploadQueue.Register);
        }
        catch
        {
            uploadQueue.DiscardHandoff(idempotencyKey);
            throw;
        }
        finally
        {
            ingestStalls.Clear(idempotencyKey);
        }

        if (ingested.Completed is { } result)
        {
            ScanJobTransitions.Complete(store, watchService, logger, idempotencyKey, result, null);
            return;
        }

        var upload = ingested.Upload!;
        MarkUploading(idempotencyKey, upload, "Processed locally; waiting to upload to the destination.");
        uploadQueue.Activate(upload);
        logger.Info($"Scan operation {idempotencyKey} phase {lastPhase} completed after {stopwatch.Elapsed}; queued {upload.Files.Count} file(s) for upload.");
    }

    private void MarkUploading(string idempotencyKey, UploadRoll upload, string message) =>
        store.Update(idempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Uploading,
            FilesCompleted = upload.FilesUploaded,
            FileCount = upload.Files.Count,
            BytesCompleted = upload.BytesUploaded,
            TotalBytes = upload.TotalBytes,
            ResolvedSourceDir = upload.SourceDir,
            Message = message
        });

    private void OnIngestStalled(string idempotencyKey, string step, TimeSpan idle)
    {
        var minutes = Math.Max(1, (int)idle.TotalMinutes);
        logger.Warning($"Scan operation {idempotencyKey} made no progress for {minutes} minute(s) while: {step}");
        store.Update(idempotencyKey, current => ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
        {
            Message = $"No progress for {minutes} minute(s) ({step}). The scanner share may not be responding."
        });
    }

    private bool ShouldWaitForReadiness(ScanOperationState operation) =>
        operation.Request.WaitForReady
        && string.IsNullOrWhiteSpace(operation.ResolvedSourceDir)
        && operation.Phase is ScanOperationPhases.Queued or ScanOperationPhases.Watching or ScanOperationPhases.Settling;

    private void FailOperation(ScanOperationState operation, string error, string? errorCode, string? errorSourceDir) =>
        ScanJobTransitions.Fail(store, watchService, logger, operation.IdempotencyKey, error, errorCode, errorSourceDir);
}

/// <summary>Terminal job updates shared by local processing and the upload queue.</summary>
internal static class ScanJobTransitions
{
    public static void Complete(
        ScanOperationStore store,
        ScanWatchService watchService,
        LocalAgentLogger logger,
        string idempotencyKey,
        ProcessScanResult result,
        string? note)
    {
        ScanOperationState? before = null;
        ScanOperationState completed;
        try
        {
            completed = store.Update(idempotencyKey, current =>
            {
                before = current;
                return ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
                {
                    Status = ScanOperationStatuses.Completed,
                    Phase = ScanOperationPhases.Complete,
                    FilesCompleted = result.Manifest.Files.Count,
                    FileCount = result.Manifest.Files.Count,
                    BytesCompleted = result.Manifest.Files.Sum(file => file.Size),
                    TotalBytes = result.Manifest.Files.Sum(file => file.Size),
                    CompletedAt = DateTimeOffset.UtcNow,
                    Message = note is null ? $"Completed {result.ImageCount} image(s)." : $"Completed {result.ImageCount} image(s). {note}",
                    Error = null,
                    ErrorCode = null,
                    ErrorSourceDir = null,
                    ResolvedSourceDir = result.Manifest.SourceDir,
                    Result = result
                };
            });
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ScanOperationHistoryExpiredException)
        {
            return;
        }

        if (ScanOperationStatuses.IsTerminal(before!.Status)) return;
        watchService.MarkCompleted(completed.Request.WatchId, idempotencyKey, completed.Message);
        logger.Info($"Scan operation {idempotencyKey} completed. Images: {result.ImageCount}.");
    }

    public static void Fail(
        ScanOperationStore store,
        ScanWatchService watchService,
        LocalAgentLogger logger,
        string idempotencyKey,
        string error,
        string? errorCode,
        string? errorSourceDir)
    {
        ScanOperationState? before = null;
        ScanOperationState failed;
        try
        {
            failed = store.Update(idempotencyKey, current =>
            {
                before = current;
                return ScanOperationStatuses.IsTerminal(current.Status) ? current : current with
                {
                    Status = ScanOperationStatuses.Failed,
                    Phase = ScanOperationPhases.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Message = errorCode is "watch-timeout" or "settle-timeout"
                        ? "Scanner output did not become ready."
                        : errorCode == "upload-failed" ? "Upload to the destination failed." : "Scan processing failed.",
                    Error = error,
                    ErrorCode = errorCode,
                    ErrorSourceDir = errorSourceDir
                };
            });
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ScanOperationHistoryExpiredException)
        {
            return;
        }

        if (failed.Status != ScanOperationStatuses.Failed || ScanOperationStatuses.IsTerminal(before!.Status))
        {
            return;
        }
        watchService.MarkFailed(failed.Request.WatchId, idempotencyKey, failed.Error);
        logger.Warning($"Scan operation {idempotencyKey} failed{(errorCode is null ? "" : $" ({errorCode})")}: {error}");
    }
}
