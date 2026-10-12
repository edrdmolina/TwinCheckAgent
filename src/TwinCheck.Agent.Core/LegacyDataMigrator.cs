namespace TwinCheck.Agent.Core;

public enum LegacyMigrationOutcome
{
    NothingToMigrate,
    TargetAlreadyConfigured,
    Migrated,
}

public sealed record LegacyMigrationResult(LegacyMigrationOutcome Outcome, int FilesCopied);

/// <summary>
/// Copies data from the old machine-wide Windows service install
/// (%ProgramData%\TwinCheck\ScanAgent) into the per-user data folder.
/// The source is never modified, and existing target files are never overwritten.
/// </summary>
public static class LegacyDataMigrator
{
    public const string ConfigFileName = "agent-config.json";

    // The old installer's machine certificate files; the per-user app makes its own.
    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost.pfx",
        "localhost.cer",
        "localhost-cert-thumbprint.txt",
    };

    public static LegacyMigrationResult Migrate(string sourceDir, string targetDir)
    {
        var sourceConfig = Path.Combine(sourceDir, ConfigFileName);
        if (!File.Exists(sourceConfig))
        {
            return new LegacyMigrationResult(LegacyMigrationOutcome.NothingToMigrate, 0);
        }

        var targetConfig = Path.Combine(targetDir, ConfigFileName);
        if (File.Exists(targetConfig))
        {
            return new LegacyMigrationResult(LegacyMigrationOutcome.TargetAlreadyConfigured, 0);
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (SkippedFiles.Contains(relative) || string.Equals(relative, ConfigFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CopyIfMissing(file, Path.Combine(targetDir, relative))) copied++;
        }

        // Copy the config last: it marks the migration as done, so an interrupted copy is retried.
        if (CopyIfMissing(sourceConfig, targetConfig)) copied++;
        return new LegacyMigrationResult(LegacyMigrationOutcome.Migrated, copied);
    }

    private static bool CopyIfMissing(string source, string target)
    {
        if (File.Exists(target)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
        return true;
    }
}
