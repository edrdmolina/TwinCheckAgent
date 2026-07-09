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
