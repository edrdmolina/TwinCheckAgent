namespace TwinCheck.Agent.Core;

public static class UploadStatuses
{
    /// <summary>Recorded just before the roll folder is renamed; not yet eligible for upload.</summary>
    public const string Handoff = "handoff";
    public const string Waiting = "waiting";
    public const string Uploading = "uploading";
    public const string Retrying = "retrying";
    public const string Done = "done";
    public const string Failed = "failed";

    public static bool IsFinished(string status) => status is Done or Failed;
}

public static class UploadFileStates
{
    public const string Pending = "pending";
    /// <summary>The final name is chosen and saved; the file may already be at it after a crash.</summary>
    public const string Publishing = "publishing";
    public const string Uploaded = "uploaded";
}

/// <summary>One processed file waiting in local staging for its upload to the destination.</summary>
public sealed record UploadFile
{
    public required int Sequence { get; init; }
    public required ScanFileKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public required string FileName { get; init; }
    public required string StagedPath { get; init; }
    /// <summary>Destination before collision handling; the final name is chosen at upload time.</summary>
    public required string PlannedPath { get; init; }
    public long SourceSize { get; init; }
    public string? SourceSha256 { get; init; }
    public long Size { get; init; }
    public required string Sha256 { get; init; }
    public string? Conversion { get; init; }
    public string State { get; init; } = UploadFileStates.Pending;
    public string? DestinationPath { get; init; }
    public ScanFileOutcome? Outcome { get; init; }
    public string? Message { get; init; }

    public ScanFileManifest ToManifest() => new()
    {
        SourcePath = SourcePath,
        DestinationPath = DestinationPath,
        FileName = FileName,
        SourceSize = SourceSize,
        SourceSha256 = SourceSha256,
        Size = Size,
        Sha256 = Sha256,
        Conversion = Conversion,
        Kind = Kind,
        Outcome = Outcome ?? ScanFileOutcome.Planned,
        Message = Message
    };
}

/// <summary>A roll processed on local disk whose files are uploaded to the destination one at a time.</summary>
public sealed record UploadRoll
{
    public required string IdempotencyKey { get; init; }
    public required string ProfileId { get; init; }
    public required string OrderNumber { get; init; }
    public required string RollNumber { get; init; }
    public required string SourceDir { get; init; }
    /// <summary>The roll folder renamed in place to <c>_processed-{order}-{roll}</c>; deleted after a verified upload.</summary>
    public required string SourceArchiveDir { get; init; }
    public required string StagingDir { get; init; }
    public required string FinalDir { get; init; }
    public required string DestinationRoot { get; init; }
    /// <summary>The roll's manifest without files; files and completion time are filled in when the upload finishes.</summary>
    public required OperationManifest Manifest { get; init; }
    public IReadOnlyList<UploadFile> Files { get; init; } = [];
    public string Status { get; init; } = UploadStatuses.Handoff;
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool ManifestWritten { get; init; }
    public string? CleanupWarning { get; init; }

    public int FilesUploaded => Files.Count(file => file.State == UploadFileStates.Uploaded);
    public long TotalBytes => Files.Sum(file => file.Size);
    public long BytesUploaded => Files.Where(file => file.State == UploadFileStates.Uploaded).Sum(file => file.Size);
}

public sealed record UploadRollView(
    string IdempotencyKey,
    string ProfileId,
    string OrderNumber,
    string RollNumber,
    string Status,
    bool Stalled,
    bool Paused,
    int FilesUploaded,
    int FileCount,
    long BytesUploaded,
    long TotalBytes,
    string? CurrentFile,
    int Attempts,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string StagingDir,
    string FinalDir,
    string SourceArchiveDir,
    string? CleanupWarning);

/// <summary>A local staged file is missing or damaged, so retrying the upload can't succeed.</summary>
public sealed class UploadStagedFileException(string message) : IOException(message);
