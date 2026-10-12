using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Tests;

public sealed class AgentVersionTests
{
    [Theory]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2.3-beta.1+abc123", "1.2.3-beta.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(null, "0.0.0")]
    [InlineData("  ", "0.0.0")]
    public void NormalizeDropsSourceRevision(string? informational, string expected) =>
        Assert.Equal(expected, AgentVersion.Normalize(informational));
}
