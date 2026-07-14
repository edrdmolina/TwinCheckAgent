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
    public string ScannerMode { get; init; } = ScannerModes.FrontierFolder;
    public string NamingPattern { get; init; } = "{orderNumber}-{rollNumber}-{imgNumber}";
    public bool WeeklyDestination { get; init; } = true;
    public int SettleStableSeconds { get; init; } = 5;
    public int SettleTimeoutSeconds { get; init; } = 120;
    public int SettlePollSeconds { get; init; } = 1;
    public ScanOptions Options { get; init; } = new();
}

public static class ScannerModes
{
    public const string FrontierFolder = "frontier-folder";
    public const string NoritsuDailyWatch = "noritsu-daily-watch";

    public static bool IsValid(string? value) =>
        string.Equals(value, FrontierFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, NoritsuDailyWatch, StringComparison.OrdinalIgnoreCase);
}

public static class ScanKinds
{
    public const string Original = "original";
    public const string Rescan = "rescan";

    public static bool IsValid(string? value) =>
        string.Equals(value, Original, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Rescan, StringComparison.OrdinalIgnoreCase);
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
