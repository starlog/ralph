namespace Ralph.Services;

public sealed class RalphLogger : IDisposable
{
    private readonly StreamWriter _writer;

    public string LogFile { get; }

    public RalphLogger(string logDir = ".ralph-logs")
    {
        Directory.CreateDirectory(logDir);
        LogFile = Path.Combine(logDir, $"ralph-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(LogFile, append: true) { AutoFlush = true };
        _writer.WriteLine($"Ralph session started at {DateTime.Now}");
    }

    public void Log(string level, string message)
    {
        _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message) => Log("ERROR", message);

    public void TaskStart(string taskId, string title)
        => Info($"=== Task started: {taskId} - {title} ===");

    public void TaskEnd(string taskId, string status)
        => Info($"=== Task ended: {taskId} - status: {status} ===");

    public void Dispose() => _writer.Dispose();
}
