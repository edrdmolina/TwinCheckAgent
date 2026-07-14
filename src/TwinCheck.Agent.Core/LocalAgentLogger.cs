namespace TwinCheck.Agent.Core;

public sealed class LocalAgentLogger
{
    public const string LogDirectoryEnvironmentVariable = "TWINCHECK_AGENT_LOG_DIR";

    private readonly object gate = new();
    private readonly string logDirectory;

    public LocalAgentLogger()
        : this(DefaultLogDirectory)
    {
    }

    public LocalAgentLogger(string logDirectory)
    {
        this.logDirectory = logDirectory;
    }

    public static string DefaultLogDirectory
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TwinCheck",
                "ScanAgent",
                "logs");
        }
    }

    public string LogDirectory => logDirectory;

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}");

    public IReadOnlyList<string> ReadRecentLines(int lineCount)
    {
        var count = Math.Clamp(lineCount, 1, 2000);
        var path = GetLogPath(DateTime.Today);
        if (!File.Exists(path))
        {
            return [];
        }

        lock (gate)
        {
            return File.ReadLines(path).TakeLast(count).ToArray();
        }
    }

    private void Write(string level, string message)
    {
        Directory.CreateDirectory(logDirectory);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (gate)
        {
            File.AppendAllText(GetLogPath(DateTime.Today), line + Environment.NewLine);
        }
    }

    private string GetLogPath(DateTime date) =>
        Path.Combine(logDirectory, $"agent-{date:yyyy-MM-dd}.log");
}
