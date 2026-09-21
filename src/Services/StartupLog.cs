using System.Diagnostics;
using System.Text;

namespace OllamaNetGB.Services;

internal static class StartupLog
{
    public static void Initialize()
    {
        Console.SetOut(TextWriter.Synchronized(new LogWriter(Console.Out, "Info")));
        Console.SetError(TextWriter.Synchronized(new LogWriter(Console.Error, "Error")));
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Shared.Write("Error", "Unhandled", "Unhandled exception", e.ExceptionObject.ToString() ?? "");
        Console.WriteLine($"Started {DateTimeOffset.Now:O}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    private sealed class LogWriter(TextWriter original, string level) : TextWriter
    {
        private readonly StringBuilder _pending = new();
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value)
        {
            original.Write(value);
            if (value == '\n') Emit();
            else if (value != '\r') _pending.Append(value);
            if (_pending.Length >= 65536) Emit();
        }
        public override void Write(string? value)
        {
            if (value is not null) foreach (var c in value) Write(c);
        }
        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            Emit();
            AppLog.Shared.Write(level, "Application", value?.Split('\n')[0] ?? "", value ?? "");
        }
        public override void Flush() { original.Flush(); Emit(); }
        private void Emit()
        {
            if (_pending.Length == 0) return;
            var text = _pending.ToString();
            _pending.Clear();
            AppLog.Shared.Write(level, "Application", text);
        }
    }
}
