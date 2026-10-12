using System.Text.Json;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Tests;

public sealed class LegacyDataMigratorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twincheck-migration-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "programdata");
    private string Target => Path.Combine(root, "appdata");

    [Fact]
    public void CopiesConfigStateAndLogsButNotMachineCertificates()
    {
        Write(Source, "agent-config.json", """{"apiKey":"tcn-legacy"}""");
        Write(Source, "jobs.sqlite3", "db");
        Write(Source, Path.Combine("operations", "operation-a.json"), "{}");
        Write(Source, Path.Combine("logs", "agent-20261001.log"), "log");
        Write(Source, "localhost.pfx", "pfx");
        Write(Source, "localhost.cer", "cer");
        Write(Source, "localhost-cert-thumbprint.txt", "ABC");

        var result = LegacyDataMigrator.Migrate(Source, Target);

        Assert.Equal(LegacyMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(4, result.FilesCopied);
        Assert.Equal("""{"apiKey":"tcn-legacy"}""", File.ReadAllText(Path.Combine(Target, "agent-config.json")));
        Assert.True(File.Exists(Path.Combine(Target, "jobs.sqlite3")));
        Assert.True(File.Exists(Path.Combine(Target, "operations", "operation-a.json")));
        Assert.True(File.Exists(Path.Combine(Target, "logs", "agent-20261001.log")));
        Assert.False(File.Exists(Path.Combine(Target, "localhost.pfx")));
        Assert.False(File.Exists(Path.Combine(Target, "localhost.cer")));
        Assert.False(File.Exists(Path.Combine(Target, "localhost-cert-thumbprint.txt")));
        Assert.True(File.Exists(Path.Combine(Source, "agent-config.json")), "The source must be left in place.");
    }

    [Fact]
    public void NeverOverwritesAnExistingPerUserConfig()
    {
        Write(Source, "agent-config.json", "legacy");
        Write(Source, "jobs.sqlite3", "legacy-db");
        Write(Target, "agent-config.json", "current");

        var result = LegacyDataMigrator.Migrate(Source, Target);

        Assert.Equal(LegacyMigrationOutcome.TargetAlreadyConfigured, result.Outcome);
        Assert.Equal("current", File.ReadAllText(Path.Combine(Target, "agent-config.json")));
        Assert.False(File.Exists(Path.Combine(Target, "jobs.sqlite3")));
    }

    [Fact]
    public void RetryAfterInterruptedCopyKeepsAlreadyCopiedFiles()
    {
        Write(Source, "agent-config.json", "legacy");
        Write(Source, "jobs.sqlite3", "legacy-db");
        Write(Target, "jobs.sqlite3", "partial-copy");

        var result = LegacyDataMigrator.Migrate(Source, Target);

        Assert.Equal(LegacyMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("partial-copy", File.ReadAllText(Path.Combine(Target, "jobs.sqlite3")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(Target, "agent-config.json")));
    }

    [Fact]
    public void NothingToMigrateWithoutLegacyConfig()
    {
        Write(Source, Path.Combine("logs", "agent.log"), "log");

        var result = LegacyDataMigrator.Migrate(Source, Target);

        Assert.Equal(LegacyMigrationOutcome.NothingToMigrate, result.Outcome);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void CreateIfMissingWritesUniqueKeyOnce()
    {
        var path = Path.Combine(root, "fresh", "agent-config.json");

        Assert.True(LocalAgentConfigStore.CreateIfMissing(path));
        var first = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("apiKey").GetString();
        Assert.False(LocalAgentConfigStore.CreateIfMissing(path));
        var second = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("apiKey").GetString();

        Assert.StartsWith("tcn-", first);
        Assert.Equal(52, first!.Length);
        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void Write(string directory, string relative, string contents)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
