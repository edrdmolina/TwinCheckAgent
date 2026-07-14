using System.Text.Json;

namespace TwinCheck.Agent.Core;

public sealed class OperationStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private OperationSummary? _lastOperation;

    public OperationSummary? GetLastOperation() => _lastOperation;

    public IReadOnlyList<ManifestSummary> ListManifests(AgentConfig config)
    {
        var manifests = new List<ManifestSummary>();
        foreach (var root in config.AllowedDestinationRoots.Where(Directory.Exists))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest-*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var manifest = ReadManifest(manifestPath);
                    manifests.Add(new ManifestSummary(
                        manifestPath,
                        manifest.IdempotencyKey,
                        manifest.ProfileId,
                        manifest.OrderNumber,
                        manifest.RollNumber,
                        manifest.ScanKind,
                        manifest.RescanNumber,
                        manifest.FinalDir,
                        manifest.CompletedAt,
                        manifest.RolledBackAt,
                        manifest.Files.Count(file => file.Kind == ScanFileKind.Image),
                        manifest.Ok));
                }
                catch
                {
                    // Skip unreadable manifests; health/log endpoints should not fail because of one corrupt file.
                }
            }
        }

        return manifests
            .OrderByDescending(summary => summary.CompletedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public OperationManifest? TryReadManifest(string finalDir, string idempotencyKey)
    {
        var manifestPath = GetManifestPath(finalDir, idempotencyKey);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(manifestPath), _jsonOptions);
        if (manifest?.CompletedAt is not null)
        {
            _lastOperation = ToSummary(manifest);
        }

        return manifest;
    }

    public void WriteManifest(OperationManifest manifest)
    {
        Directory.CreateDirectory(manifest.FinalDir);
        File.WriteAllText(GetManifestPath(manifest.FinalDir, manifest.IdempotencyKey), JsonSerializer.Serialize(manifest, _jsonOptions));
        if (manifest.CompletedAt is not null)
        {
            _lastOperation = ToSummary(manifest);
        }
    }

    public OperationManifest ReadManifest(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(manifestPath), _jsonOptions)
            ?? throw new InvalidOperationException($"Manifest could not be read: {manifestPath}");
        return manifest;
    }

    public void WriteManifestAt(string manifestPath, OperationManifest manifest)
    {
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));
        if (manifest.CompletedAt is not null)
        {
            _lastOperation = ToSummary(manifest);
        }
    }

    private static string GetManifestPath(string finalDir, string idempotencyKey)
    {
        var safeKey = string.Concat(idempotencyKey.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        return Path.Combine(finalDir, $"manifest-{safeKey}.json");
    }

    private static OperationSummary ToSummary(OperationManifest manifest) =>
        new(
            manifest.IdempotencyKey,
            manifest.CompletedAt!.Value,
            manifest.OrderNumber,
            manifest.RollNumber,
            manifest.Files.Count(file => file.Kind == ScanFileKind.Image),
                manifest.Ok);
}

public sealed record ManifestSummary(
    string ManifestPath,
    string IdempotencyKey,
    string ProfileId,
    string OrderNumber,
    string RollNumber,
    string ScanKind,
    int? RescanNumber,
    string FinalDir,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? RolledBackAt,
    int ImageCount,
    bool Ok);
