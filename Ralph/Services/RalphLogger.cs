namespace Ralph.Services;

public class RalphLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _lockObj = new();

    public string LogFile { get; }

    public static RalphLogger Null { get; } = new NullLogger();

    public RalphLogger(string logDir = RalphPaths.LogDir)
    {
        Directory.CreateDirectory(logDir);
        LogFile = Path.Combine(logDir, $"ralph-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        // Windows에서 로그를 tail하거나 테스트가 동시에 read 가능하도록 share 명시.
        var stream = new FileStream(
            LogFile, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writer.WriteLine($"Ralph session started at {DateTime.Now}");
    }

    protected RalphLogger()
    {
        LogFile = "";
        _writer = null;
    }

    public virtual void Log(string level, string message)
    {
        if (_writer is null) return;
        lock (_lockObj)
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
        }
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message) => Log("ERROR", message);

    public void TaskStart(string taskId, string title)
        => Info($"=== Task started: {taskId} - {title} ===");

    public void TaskEnd(string taskId, string status)
        => Info($"=== Task ended: {taskId} - status: {status} ===");

    public virtual void Dispose()
    {
        lock (_lockObj)
        {
            _writer?.Dispose();
        }
    }

    private sealed class NullLogger : RalphLogger
    {
        public override void Log(string level, string message) { }
        public override void Dispose() { }
    }
}
