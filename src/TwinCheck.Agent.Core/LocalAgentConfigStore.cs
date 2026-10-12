using System.Text.Json;

namespace TwinCheck.Agent.Core;

public static class LocalAgentConfigStore
{
    public const string ConfigPathEnvironmentVariable = "TWINCHECK_AGENT_CONFIG_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string ConfigPath
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TwinCheck",
                "ScanAgent",
                "agent-config.json");
        }
    }

    public static AgentConfig LoadOrDefault(AgentConfig fallback)
    {
        if (!File.Exists(ConfigPath))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static void Save(AgentConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        new LocalAgentLogger().Info($"Saved local agent config to {ConfigPath}");
    }

    /// <summary>
    /// Writes an empty config with a unique API key when none exists, so a fresh install
    /// never runs with a shared default key. Returns true when a config was created.
    /// </summary>
    public static bool CreateIfMissing(string? path = null)
    {
        path ??= ConfigPath;
        if (File.Exists(path)) return false;

        var config = new AgentConfig { AgentName = "TwinCheck Scan Agent", ApiKey = GenerateApiKey() };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return true;
    }

    public static string GenerateApiKey() =>
        $"tcn-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
}
