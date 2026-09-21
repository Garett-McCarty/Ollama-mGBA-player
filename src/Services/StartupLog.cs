using System.Diagnostics;
using System.Text;

namespace OllamaNetGB.Services;

internal static class StartupLog
{
    public static void Initialize()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaNetGB", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            var file = TextWriter.Synchronized(new StreamWriter(path, append: true) { AutoFlush = true });
            // Keep dotnet run output visible while also retaining diagnostics on disk.
            Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out, file)));
            Console.SetError(TextWriter.Synchronized(new TeeWriter(Console.Error, file)));
            Trace.Listeners.Add(new TextWriterTraceListener(file));
            Trace.AutoFlush = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine(e.ExceptionObject);
            Console.WriteLine($"Started {DateTimeOffset.Now:O}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            Console.WriteLine($"Diagnostic log: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must not prevent the desktop window from opening.
        }
    }

    private sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;
        public override void Write(char value)
        {
            console.Write(value);
            file.Write(value);
        }
        public override void Write(string? value)
        {
            console.Write(value);
            file.Write(value);
        }
        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            file.WriteLine(value);
        }
        public override void Flush()
        {
            console.Flush();
            file.Flush();
        }
    }
}
