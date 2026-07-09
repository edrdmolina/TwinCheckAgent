namespace TwinCheck.Agent.Core;

public sealed record AgentConfig
{
    public string AgentName { get; init; } = Environment.MachineName;
    public string Version { get; init; } = "0.1.0";
    public string ApiKey { get; init; } = "change-me";
    public string[] AllowedSourceRoots { get; init; } = [];
    public string[] AllowedDestinationRoots { get; init; } = [];
    public string? ActiveProfileId { get; init; }
    public ScannerProfile[] Profiles { get; init; } = [];
}

public sealed record ScannerProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SourceDir { get; init; }
    public required string DestinationDir { get; init; }
    public string NamingPattern { get; init; } = "{orderNumber}-{rollNumber}-{imgNumber}";
    public ScanOptions Options { get; init; } = new();
}

public sealed record ScanOptions
{
    public bool BmpToTiff { get; init; }
    public ExifOptions? Exif { get; init; }
}

public sealed record ExifOptions
{
    public string? Artist { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
}
