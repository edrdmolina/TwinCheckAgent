namespace TwinCheck.Agent.Core;

internal sealed record UploadQueueOptions
{
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StallCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Uploads locally processed rolls to their destinations: one host-wide worker, oldest roll first, one file
/// at a time. The queue is stored in the job database, so a restart resumes every roll where it stopped.
/// </summary>
public sealed class UploadQueueService : IDisposable
{
    private const long JobProgressBytes = 64L * 1024 * 1024;

    private readonly ScanOperationStore store;
    private readonly ScanProcessor processor;
    private readonly ScanWatchService watchService;
    private readonly LocalAgentLogger logger;
    private readonly UploadQueueOptions options;
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly StallMonitor stallMonitor;
    private readonly object currentGate = new();
    private readonly Task worker;
    private CurrentUpload? current;

    public UploadQueueService(ScanOperationStore store, ScanProcessor processor, ScanWatchService watchService, LocalAgentLogger logger)
        : this(store, processor, watchService, logger, null)
    {
    }

    internal UploadQueueService(
        ScanOperationStore store,
        ScanProcessor processor,
        ScanWatchService watchService,
        LocalAgentLogger logger,
        UploadQueueOptions? options)
    {
        this.store = store;
        this.processor = processor;
        this.watchService = watchService;
        this.logger = logger;
        this.options = options ?? new UploadQueueOptions();
        stallMonitor = new StallMonitor(this.options.StallAfter, this.options.StallCheckInterval, OnStalled, OnResumed);
        RecoverHandoffs();
        worker = Task.Run(RunAsync);
    }

    /// <summary>Test hook: runs on the worker thread before each file upload.</summary>
    internal Action<UploadRoll, UploadFile>? BeforeFileUpload { get; set; }

    public bool Paused => store.UploadsPaused;

    public int PendingCount =>
        store.ListUploads(finishedLimit: 0).Count(roll => roll.Status != UploadStatuses.Handoff);

    public IReadOnlyList<UploadRollView> List(int finishedLimit = 100)
    {
        var paused = store.UploadsPaused;
        CurrentUpload? snapshot;
        lock (currentGate) snapshot = current;
        return store.ListUploads(finishedLimit)
            .Where(roll => roll.Status != UploadStatuses.Handoff)
            .Select(roll =>
            {
                var active = snapshot?.Key == roll.IdempotencyKey ? snapshot : null;
                return new UploadRollView(
                    roll.IdempotencyKey,
                    roll.ProfileId,
                    roll.OrderNumber,
                    roll.RollNumber,
                    roll.Status,
                    stallMonitor.IsStalled(roll.IdempotencyKey),
                    paused && !UploadStatuses.IsFinished(roll.Status),
                    roll.FilesUploaded,
                    roll.Files.Count,
                    roll.BytesUploaded + (active?.FileBytes ?? 0),
                    roll.TotalBytes,
                    active?.FileName,
                    roll.Attempts,
                    roll.LastError,
                    roll.NextAttemptAt,
                    roll.QueuedAt,
                    roll.StartedAt,
                    roll.CompletedAt,
                    roll.StagingDir,
                    roll.FinalDir,
                    roll.SourceArchiveDir,
                    roll.CleanupWarning);
            })
            .ToArray();
    }

    /// <summary>Stops after the file being uploaded is finished and verified.</summary>
    public void Pause()
    {
        store.UploadsPaused = true;
        logger.Info("Uploads paused.");
    }

    public void Resume()
    {
        store.UploadsPaused = false;
        logger.Info("Uploads resumed.");
        wake.Release();
    }

    /// <summary>Skips the wait before the next attempt of a roll that is retrying.</summary>
    public bool RetryNow(string idempotencyKey)
    {
        var roll = store.GetUpload(idempotencyKey);
        if (roll?.Status != UploadStatuses.Retrying) return false;
        store.UpdateUploadRoll(roll with { NextAttemptAt = null });
        logger.Info($"Retry requested for upload {idempotencyKey}.");
        wake.Release();
        return true;
    }

    internal UploadRoll? Get(string idempotencyKey) => store.GetUpload(idempotencyKey);

    /// <summary>Records a roll before its folder is renamed; it isn't uploaded until <see cref="Activate"/>.</summary>
    internal void Register(UploadRoll roll) => store.SaveUpload(roll with { Status = UploadStatuses.Handoff });

    internal void Activate(UploadRoll roll)
    {
        store.UpdateUploadRoll(roll with { Status = UploadStatuses.Waiting });
        wake.Release();
    }

    /// <summary>Forgets a roll whose hand-off failed before the folder was renamed.</summary>
    internal void DiscardHandoff(string idempotencyKey)
    {
        if (store.GetUpload(idempotencyKey)?.Status == UploadStatuses.Handoff)
        {
            store.DeleteUpload(idempotencyKey);
        }
    }

    internal void Wake() => wake.Release();

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            // A worker stuck in a hung network write can't be stopped; don't wait for it forever.
            worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancelled during shutdown.
        }
        stallMonitor.Dispose();
    }

    private void RecoverHandoffs()
    {
        foreach (var roll in store.ListUploads(finishedLimit: 0).Where(roll => roll.Status == UploadStatuses.Handoff))
        {
            if (Directory.Exists(roll.SourceArchiveDir))
            {
                // The rename happened before the agent stopped.
                store.UpdateUploadRoll(roll with { Status = UploadStatuses.Waiting });
                logger.Info($"Upload {roll.IdempotencyKey} recovered after restart.");
            }
            else
            {
                // The rename never happened; local processing runs again from the untouched roll folder.
                store.DeleteUpload(roll.IdempotencyKey);
            }
        }
    }

    private async Task RunAsync()
    {
        var token = cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = UploadNextDue();
            }
            catch (Exception exception)
            {
                logger.Error("Upload queue error; it will try again shortly.", exception);
                wait = options.RetryBase;
            }

            if (wait == TimeSpan.Zero) continue;
            try
            {
                await wake.WaitAsync(wait, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Uploads the oldest roll that is due. Returns how long to wait before looking again.</summary>
    private TimeSpan UploadNextDue()
    {
        if (store.UploadsPaused) return Timeout.InfiniteTimeSpan;

        var now = DateTimeOffset.UtcNow;
        var pending = store.ListUploads(finishedLimit: 0)
            .Where(roll => roll.Status is UploadStatuses.Waiting or UploadStatuses.Uploading or UploadStatuses.Retrying)
            .ToArray();
        var due = pending.FirstOrDefault(roll => roll.NextAttemptAt is null || roll.NextAttemptAt <= now);
        if (due is null)
        {
            var next = pending.Select(roll => roll.NextAttemptAt).Min();
            return next is { } at ? Max(at - now, TimeSpan.FromMilliseconds(10)) : Timeout.InfiniteTimeSpan;
        }

        Upload(due);
        return TimeSpan.Zero;
    }

    private void Upload(UploadRoll roll)
    {
        var key = roll.IdempotencyKey;
        roll = roll with { Status = UploadStatuses.Uploading, StartedAt = roll.StartedAt ?? DateTimeOffset.UtcNow };
        store.UpdateUploadRoll(roll);
        var files = roll.Files.ToList();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                if (files[index].State == UploadFileStates.Uploaded) continue;
                if (store.UploadsPaused)
                {
                    store.UpdateUploadRoll(roll with { Status = UploadStatuses.Waiting });
                    UpdateJob(key, files, "Uploads are paused.");
                    return;
                }

                var file = files[index];
                var frame = $"{index + 1}/{files.Count}";
                lock (currentGate) current = new CurrentUpload(key, file.FileName, 0);
                stallMonitor.Touch(key, $"uploading {file.FileName} ({frame})");
                UpdateJob(key, files, $"Uploading frame {frame} to the destination.");
                BeforeFileUpload?.Invoke(roll, file);
                long reported = 0;
                files[index] = processor.UploadFile(file, delta =>
                {
                    long bytes;
                    lock (currentGate)
                    {
                        current = current! with { FileBytes = current.FileBytes + delta };
                        bytes = current.FileBytes;
                    }
                    stallMonitor.Touch(key, $"uploading {file.FileName} ({frame})");
                    if (bytes - reported >= JobProgressBytes)
                    {
                        reported = bytes;
                        UpdateJob(key, files, $"Uploading frame {frame} to the destination.", bytes);
                    }
                }, planned => store.UpdateUploadFile(key, planned));
                store.UpdateUploadFile(key, files[index]);
                lock (currentGate) current = null;
                roll = roll with { Files = files.ToArray() };
            }

            stallMonitor.Touch(key, "writing the manifest and cleaning up");
            var (result, finished) = processor.FinishUpload(roll, written => store.UpdateUploadRoll(written));
            ScanJobTransitions.Complete(store, watchService, logger, key, result, finished.CleanupWarning);
            store.UpdateUploadRoll(finished with
            {
                Status = UploadStatuses.Done,
                CompletedAt = finished.CompletedAt ?? DateTimeOffset.UtcNow,
                LastError = null,
                NextAttemptAt = null
            });
            logger.Info($"Upload {key} finished: {files.Count} file(s) verified at {roll.FinalDir}.");
        }
        catch (UploadStagedFileException exception)
        {
            store.UpdateUploadRoll(roll with
            {
                Status = UploadStatuses.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                LastError = exception.Message,
                NextAttemptAt = null
            });
            ScanJobTransitions.Fail(store, watchService, logger, key, exception.Message, "upload-failed", roll.SourceArchiveDir);
        }
        catch (Exception exception)
        {
            var attempts = roll.Attempts + 1;
            var delay = Min(options.RetryBase * Math.Pow(2, Math.Min(attempts - 1, 16)), options.RetryMax);
            var nextAttempt = DateTimeOffset.UtcNow + delay;
            store.UpdateUploadRoll(roll with
            {
                Status = UploadStatuses.Retrying,
                Attempts = attempts,
                LastError = exception.Message,
                NextAttemptAt = nextAttempt
            });
            UpdateJob(key, files, $"Upload failed; retrying at {nextAttempt.ToLocalTime():t}. {exception.Message}");
            logger.Warning($"Upload {key} attempt {attempts} failed; retrying in {delay.TotalSeconds:F0}s: {exception.Message}");
        }
        finally
        {
            lock (currentGate) current = null;
            stallMonitor.Clear(key);
        }
    }

    private void UpdateJob(string key, IReadOnlyList<UploadFile> files, string message, long currentFileBytes = 0)
    {
        var uploaded = files.Where(file => file.State == UploadFileStates.Uploaded).ToArray();
        TryUpdateJob(key, job => job with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Uploading,
            FilesCompleted = uploaded.Length,
            FileCount = files.Count,
            BytesCompleted = uploaded.Sum(file => file.Size) + currentFileBytes,
            TotalBytes = files.Sum(file => file.Size),
            Message = message
        });
    }

    private void TryUpdateJob(string key, Func<ScanOperationState, ScanOperationState> update)
    {
        try
        {
            store.Update(key, job => ScanOperationStatuses.IsTerminal(job.Status) ? job : update(job));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ScanOperationHistoryExpiredException)
        {
            // Rolls uploaded for the direct process endpoint have no queued job.
        }
    }

    private void OnStalled(string key, string step, TimeSpan idle)
    {
        var minutes = Math.Max(1, (int)idle.TotalMinutes);
        logger.Warning($"Upload {key} made no progress for {minutes} minute(s) while {step}. The NAS may not be responding.");
        TryUpdateJob(key, job => job with { Message = $"Stalled — NAS not responding: no progress for {minutes} minute(s) while {step}." });
    }

    private void OnResumed(string key) => logger.Info($"Upload {key} is making progress again.");

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private sealed record CurrentUpload(string Key, string FileName, long FileBytes);
}
