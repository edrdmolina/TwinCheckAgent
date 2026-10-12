namespace TwinCheck.Agent.Core;

public sealed record PhotoMetadata(string? Artist, string? Make, string? Model, string? FilmStock)
{
    public const string Capability = "scan-metadata-v1";
    public const string NamespaceUri = "urn:twincheck:scan-metadata:1.0";
    public const string NamespacePrefix = "twincheck";
    public bool HasValues => Artist is not null || Make is not null || Model is not null || FilmStock is not null;

    public static PhotoMetadata Resolve(ScanMetadata? job, ExifOptions? profile) => new(
        Clean(job?.CustomerName) ?? Clean(profile?.Artist),
        Clean(profile?.Make), Clean(profile?.Model), Clean(job?.FilmStock));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IScanMetadataWriter
{
    bool IsAvailable { get; }
    void Write(string stagedPath, PhotoMetadata metadata);
}

public sealed class ScanMetadataWriteException(string message, Exception? innerException = null)
    : IOException(message, innerException);

internal sealed class UnavailableScanMetadataWriter : IScanMetadataWriter
{
    public static readonly UnavailableScanMetadataWriter Instance = new();
    public bool IsAvailable => false;
    public void Write(string stagedPath, PhotoMetadata metadata) =>
        throw new ScanMetadataWriteException("Photo metadata was requested, but no metadata writer is configured.");
}
