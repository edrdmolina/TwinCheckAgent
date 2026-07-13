namespace TwinCheck.Agent.Core;

public sealed class AgentConfigProvider(AgentConfig fallback)
{
    public AgentConfig Current => LocalAgentConfigStore.LoadOrDefault(fallback);
}
