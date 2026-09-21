
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OllamaNetGB.AI;
using OllamaNetGB.Agent;
using OllamaNetGB.Config;
using OllamaNetGB.Emulator;
using OllamaNetGB.Memory;
using OllamaNetGB.Metrics;
using OllamaNetGB.Profiles;
using OllamaNetGB.Services;
using OllamaNetGB.Views;

namespace OllamaNetGB;

public partial class App : Application
{
    public override void Initialize()
    {
        Console.WriteLine("[startup] Loading App.axaml");
        AvaloniaXamlLoader.Load(this);
        Console.WriteLine("[startup] App.axaml loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.WriteLine("[startup] Framework initialization completing");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Console.WriteLine("[startup] Loading settings");
            var settings = new SettingsService();
            settings.Load();
            Console.WriteLine("[startup] Creating services and view models");

            var filePicker = new FilePickerService();
            var mgba = new MgbaHttpClient(settings);
            var ollama = new OllamaAgent(settings);
            var memory = new Neo4jAgentMemory(settings);
            var profiles = new JsonGameProfileProvider();
            var coordinator = new AgentCoordinator(ollama, memory, profiles, settings);
            var metrics = new SqliteRunMetricsStore(settings);
            var settingsViewModel = new SettingsViewModel(settings, filePicker);
            var gameViewModel = new GameViewModel(mgba, coordinator, settings, metrics);
            var mainViewModel = new MainWindowViewModel(settingsViewModel, gameViewModel);

            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            // Attach only after Avalonia has created the native window and its
            // storage provider. Attaching before Opened is unreliable on Linux.
            mainWindow.Opened += (_, _) => filePicker.Attach(mainWindow);
            desktop.MainWindow = mainWindow;
            Console.WriteLine("[startup] MainWindow assigned");

            desktop.Exit += (_, _) =>
            {
                mainViewModel.Logs.Dispose();
                mgba.Dispose();
                ollama.Dispose();
                memory.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
