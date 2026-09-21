using System.Text.Json;

namespace OllamaNetGB.Services;

public sealed record LogEntry(long Id, DateTimeOffset Time, string Level, string Source, string Message, string Details)
{
    public string Summary => $"{Time:HH:mm:ss}  {Level}  {Source} — {Message}";
    public string FullText => $"{Time:O} [{Level}] {Source}: {Message}\n{Details}";
}

/// <summary>Thread-safe, bounded diagnostic history and rotating local JSON-lines log.</summary>
public sealed class AppLog
{
    public static AppLog Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private long _sequence;
    private int _part;
    private string? _directory;
    private readonly string _session = $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
    public string FilePath { get; private set; } = "";
    public string PersistenceError { get; private set; } = "";

    private AppLog()
    {
        try
        {
            _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaNetGB", "logs");
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, _session + ".jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _directory = null;
            PersistenceError = ex.Message;
        }
    }

    public LogEntry[] Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Write(string level, string source, string message, string details = "")
    {
        lock (_gate)
        {
            var entry = new LogEntry(++_sequence, DateTimeOffset.Now, level, source, message, details);
            var visible = details.Length > 65536
                ? entry with { Details = details[..65536] + "\n[View truncated; full details are in the log file.]" }
                : entry;
            _entries.Enqueue(visible);
            while (_entries.Count > 200) _entries.Dequeue();
            if (_directory is null) return;
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= 10 * 1024 * 1024)
                    FilePath = Path.Combine(_directory, $"{_session}-{++_part}.jsonl");
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
                PersistenceError = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PersistenceError = ex.Message;
            }
        }
    }
}
