namespace TwinCheck.Agent.Core;

public sealed record ProcessScanRequest
{
    public required string IdempotencyKey { get; init; }
    public required string ProfileId { get; init; }
    public string? SourceDir { get; init; }
    public string? DestinationDir { get; init; }
    public required string OrderNumber { get; init; }
    public required string RollNumber { get; init; }
    public string? Naming { get; init; }
    public string ScanKind { get; init; } = ScanKinds.Original;
    public int? RescanNumber { get; init; }
    public ScanOptions? Options { get; init; }
    public bool DryRun { get; init; }
}

public sealed record ProcessScanResult(
    bool Ok,
    int ImageCount,
    OperationManifest Manifest,
    IReadOnlyList<ScanFileManifest> Conflicts,
    IReadOnlyList<ScanFileManifest> Reviewed);

public sealed record OperationManifest
{
    public required string IdempotencyKey { get; init; }
    public required string ProfileId { get; init; }
    public required string OrderNumber { get; init; }
    public required string RollNumber { get; init; }
    public required string SourceDir { get; init; }
    public required string DestinationDir { get; init; }
    public required string FinalDir { get; init; }
    public string ScanKind { get; init; } = ScanKinds.Original;
    public int? RescanNumber { get; init; }
    public bool DryRun { get; init; }
    public bool Ok { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }
    public IReadOnlyList<ScanFileManifest> Files { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record RollbackScanRequest
{
    public required string ProfileId { get; init; }
    public required string ManifestPath { get; init; }
}

public sealed record RollbackScanResult(
    bool Ok,
    string ManifestPath,
    string SourceArchiveDir,
    string FinalDir,
    int RestoredFileCount,
    IReadOnlyList<string> Warnings);

public sealed record ScanFileManifest
{
    public required string SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }
    public required ScanFileKind Kind { get; init; }
    public required ScanFileOutcome Outcome { get; init; }
    public string? Message { get; init; }
}

public enum ScanFileKind
{
    Image,
    Review
}

public enum ScanFileOutcome
{
    Planned,
    Copied,
    AlreadyDone,
    ConflictRenamed,
    MovedToReview,
    Failed
}
