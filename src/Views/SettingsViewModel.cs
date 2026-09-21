using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neo4j.Driver;
using OllamaNetGB.Config;
using OllamaNetGB.Services;

namespace OllamaNetGB.Views;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IFilePickerService _filePicker;

    public SettingsViewModel(ISettingsService settings, IFilePickerService filePicker)
    {
        _settings = settings;
        _filePicker = filePicker;

        var current = settings.Current;
        MgbaBinaryPath = current.MgbaBinaryPath;
        MgbaHttpBinaryPath = current.MgbaHttpBinaryPath;
        MgbaSocketLuaPath  = current.MgbaSocketLuaPath;
        MgbaHttpBaseUrl    = current.MgbaHttpBaseUrl;
        OllamaBaseUri      = current.OllamaBaseUri;
        OllamaModel        = current.OllamaModel;
        SessionGoal        = current.SessionGoal;
        GamePrompt         = current.GamePrompt;
        FrameIntervalMs    = current.FrameIntervalMs;
        DecisionHistoryCount = current.DecisionHistoryCount;
        Neo4jUri           = current.Neo4jUri;
        Neo4jUser          = current.Neo4jUser;
        Neo4jPassword      = current.Neo4jPassword;
        GameId             = current.GameId;
        RomPath            = current.RomPath;
    }

    [ObservableProperty] private string _mgbaBinaryPath = "";

    [ObservableProperty]
    private string _mgbaBinaryStatus = "";

    [ObservableProperty]
    private string _mgbaHttpBinaryPath = "";

    [ObservableProperty]
    private string _mgbaHttpBinaryStatus = "";

    [ObservableProperty]
    private string _mgbaSocketLuaPath = "";

    [ObservableProperty]
    private string _mgbaSocketLuaStatus = "";

    [ObservableProperty]
    private string _mgbaHttpBaseUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _mgbaHttpStatus = "";

    [ObservableProperty]
    private string _ollamaBaseUri = "";

    [ObservableProperty]
    private string _ollamaModel = "";

    [ObservableProperty]
    private string _sessionGoal = "";

    [ObservableProperty]
    private string _gamePrompt = "";

    [ObservableProperty]
    private string _ollamaStatus = "";

    [ObservableProperty]
    private decimal? _frameIntervalMs = 1500;

    [ObservableProperty]
    private decimal? _decisionHistoryCount = 12;

    [ObservableProperty]
    private string _neo4jUri = "";

    [ObservableProperty]
    private string _neo4jUser = "";

    [ObservableProperty]
    private string _neo4jPassword = "";

    [ObservableProperty]
    private string _neo4jStatus = "";

    [ObservableProperty]
    private string _gameId = "";

    [ObservableProperty]
    private string _romPath = "";

    [ObservableProperty]
    private string _saveStatus = "";

    public event EventHandler? SaveCompleted;

    [RelayCommand]
    private async Task BrowseMgbaBinaryAsync()
    {
        await BrowseAsync(
            "Select mGBA executable",
            path => MgbaBinaryPath = path,
            status => MgbaBinaryStatus = status,
            "appimage", "exe");
    }

    [RelayCommand]
    private async Task BrowseMgbaHttpBinaryAsync()
    {
        await BrowseAsync(
            "Select mGBA-http executable",
            path => MgbaHttpBinaryPath = path,
            status => MgbaHttpBinaryStatus = status,
            "exe");
    }

    [RelayCommand]
    private async Task BrowseLuaScriptAsync()
    {
        await BrowseAsync(
            "Select mGBASocketServer.lua",
            path => MgbaSocketLuaPath = path,
            status => MgbaSocketLuaStatus = status,
            "lua");
    }

    [RelayCommand]
    private async Task BrowseRomAsync()
    {
        await BrowseAsync(
            "Select GBA ROM",
            path => RomPath = path,
            status => SaveStatus = status,
            "gba", "rom");
    }

    private async Task BrowseAsync(
        string title,
        Action<string> setPath,
        Action<string> setStatus,
        params string[] extensions)
    {
        try
        {
            setStatus("Opening file picker...");
            var path = await _filePicker.PickFileAsync(title, extensions);
            if (!string.IsNullOrWhiteSpace(path))
            {
                setPath(path);
                setStatus("ok Selected");
            }
            else
            {
                setStatus("Selection cancelled");
            }
        }
        catch (Exception exception)
        {
            setStatus($"ex File picker: {exception.Message}");
        }
    }

    [RelayCommand]
    private void TestMgbaBinary() => MgbaBinaryStatus = ValidateExecutable(MgbaBinaryPath);

    [RelayCommand]
    private void TestMgbaHttpBinary() => MgbaHttpBinaryStatus = ValidateExecutable(MgbaHttpBinaryPath);

    [RelayCommand]
    private void TestLuaScript() => MgbaSocketLuaStatus = File.Exists(MgbaSocketLuaPath) ? "ok Found" : "err Not found";


    [RelayCommand]
    private async Task TestMgbaHttpAsync()
    {
        MgbaHttpStatus = await ProbeUrlAsync($"{MgbaHttpBaseUrl.TrimEnd('/')}/openapi/v1.json");
    }

    [RelayCommand]
    private async Task TestOllamaAsync()
    {
        OllamaStatus = await ProbeUrlAsync($"{OllamaBaseUri.TrimEnd('/')}/api/tags");
    }

    [RelayCommand]
    private async Task TestNeo4jAsync()
    {
        try
        {
            await using var driver = GraphDatabase.Driver(Neo4jUri, AuthTokens.Basic(Neo4jUser, Neo4jPassword));
            await driver.VerifyConnectivityAsync();
            Neo4jStatus = "ok Connected";
        }
        catch (Exception exception)
        {
            Neo4jStatus = $"ex {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            TryMakeExecutable(MgbaBinaryPath);
            TryMakeExecutable(MgbaHttpBinaryPath);
        }

        var current = _settings.Current;
        current.MgbaBinaryPath     = MgbaBinaryPath;
        current.MgbaHttpBinaryPath = MgbaHttpBinaryPath;
        current.MgbaSocketLuaPath  = MgbaSocketLuaPath;
        current.MgbaHttpBaseUrl    = MgbaHttpBaseUrl;
        current.OllamaBaseUri      = OllamaBaseUri;
        current.OllamaModel        = OllamaModel;
        current.SessionGoal        = SessionGoal;
        current.GamePrompt         = GamePrompt;
        current.FrameIntervalMs    = (int)(FrameIntervalMs ?? 1500);
        current.DecisionHistoryCount = (int)(DecisionHistoryCount ?? 12);
        current.Neo4jUri           = Neo4jUri;
        current.Neo4jUser          = Neo4jUser;
        current.Neo4jPassword      = Neo4jPassword;
        current.GameId             = GameId;
        current.RomPath            = RomPath;

        await _settings.SaveAsync();
        SaveStatus = $"Saved to {_settings.ConfigFilePath}";
        SaveCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static string ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "err Empty";
        if (!File.Exists(path))
            return "err File not found";

        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            var anyExec = mode.HasFlag(UnixFileMode.UserExecute) || mode.HasFlag(UnixFileMode.GroupExecute) || mode.HasFlag(UnixFileMode.OtherExecute);
            if (!anyExec)
                return "err Not executable (will chmod +x on save)";
        }

        return "ok OK";
    }

    private static void TryMakeExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            } catch { /* best effort */ }
        }

    }

    private static async Task<string> ProbeUrlAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? $"ok {(int)response.StatusCode}" : $"err {(int)response.StatusCode}";
        } catch(Exception exception)
        {
            return $"ex {exception.Message}";
        }
    }
}
