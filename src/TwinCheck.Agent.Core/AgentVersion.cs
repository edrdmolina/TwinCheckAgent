using System.Reflection;

namespace TwinCheck.Agent.Core;

public static class AgentVersion
{
    /// <summary>
    /// The running build's version, set by -p:Version at publish time.
    /// Health and diagnostics report this instead of the version stored in agent-config.json.
    /// </summary>
    public static string Current { get; } = Normalize(
        typeof(AgentVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Normalize(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "0.0.0";
        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
    }
}
