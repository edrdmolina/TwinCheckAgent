using System.Text.Json;

namespace TwinCheck.Agent.Core;

public static class LocalAgentConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwinCheck",
            "ScanAgent",
            "agent-config.json");

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
    }
}
