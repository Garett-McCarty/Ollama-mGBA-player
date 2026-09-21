using Avalonia;
using System;

namespace OllamaNetGB;

/// <summary>
/// Ollama mGBA Player Utility App.
/// 
/// Uses Ollama, Neo4j, mGBA, and mGBA-http to have an agent play a gameboy advance game! built by ChatGPT (Nova) <3
/// </summary>
class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		Console.WriteLine("[startup] Configuring Avalonia");
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		Console.WriteLine("[startup] Avalonia lifetime ended");
	}

	// Avalonia configuration, don't remove; also used by visual designer.
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace();
}
