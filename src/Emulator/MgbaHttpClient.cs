using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using OllamaNetGB.Config;
using OllamaNetGB.Models;

namespace OllamaNetGB.Emulator;

/// <summary>
/// Controls mGBA through the cross-platform mGBA-http companion process.
/// </summary>
public sealed class MgbaHttpClient : IMgbaClient, IDisposable
{
    private const double GbaFramesPerSecond = 59.7275;

    private readonly ISettingsService _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _screenshotPath;
    private Process? _mgbaProcess;
    private Process? _httpProcess;

    public MgbaHttpClient(ISettingsService settings)
    {
        _settings = settings;
        var captureDirectory = Path.Combine(Path.GetTempPath(), "OllamaNetGB");
        Directory.CreateDirectory(captureDirectory);
        _screenshotPath = Path.Combine(captureDirectory, "current-frame.png");
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (await IsAliveAsync(cancellationToken))
            return;

        var settings = _settings.Current;

        if (!await IsHttpServerAvailableAsync(cancellationToken))
        {
            if (!File.Exists(settings.MgbaHttpBinaryPath))
                throw new InvalidOperationException("mGBA-http is not running and its executable path is not configured.");

            _httpProcess = StartProcess(settings.MgbaHttpBinaryPath);
        }

        if (!File.Exists(settings.MgbaBinaryPath))
            throw new InvalidOperationException("The mGBA executable path is missing or invalid.");
        if (!File.Exists(settings.MgbaSocketLuaPath))
            throw new InvalidOperationException("The mGBASocketServer.lua path is missing or invalid.");
        if (!File.Exists(settings.RomPath))
            throw new InvalidOperationException("Choose a ROM in Settings before starting the player.");

        // mGBA 0.10.x does not expose script loading as a command-line option.
        // The script must be loaded from Tools > Scripting > File > Load script.
        // Keep an existing owned mGBA process alive so retrying does not open
        // duplicate emulator windows while the user is loading the script.
        if (!IsProcessRunning(_mgbaProcess))
            _mgbaProcess = StartProcess(settings.MgbaBinaryPath, settings.RomPath);

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAliveAsync(cancellationToken))
                return;

            await Task.Delay(300, cancellationToken);
        }

        throw new TimeoutException(
            "mGBA did not connect. In mGBA, open Tools > Scripting, then use File > Load script and select mGBASocketServer.lua. Load it only once.");
    }

    public async Task<byte[]?> GetScreenshotAsync(CancellationToken cancellationToken)
    {
        var url = BuildUrl("/core/screenshot?path=" + Uri.EscapeDataString(_screenshotPath));
        using var response = await _http.PostAsync(url, content: null, cancellationToken);
        await EnsureSuccessAsync(response, "capture an mGBA screenshot", cancellationToken);

        if (!File.Exists(_screenshotPath))
            return null;

        return await File.ReadAllBytesAsync(_screenshotPath, cancellationToken);
    }

    public async Task TapAsync(GbaButton button, int holdMs, CancellationToken cancellationToken)
    {
        holdMs = Math.Clamp(holdMs, 16, 2_000);
        string path;

        if (holdMs <= 34)
        {
            path = $"/mgba-http/button/tap?button={button}";
        }
        else
        {
            var frames = Math.Max(1, (int)Math.Round(holdMs * GbaFramesPerSecond / 1_000));
            path = $"/mgba-http/button/hold?button={button}&duration={frames.ToString(CultureInfo.InvariantCulture)}";
        }

        using var response = await _http.PostAsync(BuildUrl(path), content: null, cancellationToken);
        await EnsureSuccessAsync(response, $"press {button}", cancellationToken);
    }

    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(BuildUrl("/core/getgametitle"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> IsHttpServerAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(BuildUrl("/openapi/v1.json"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private string BuildUrl(string path)
    {
        var baseUrl = _settings.Current.MgbaHttpBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("The mGBA-http URL in Settings is invalid.");

        return baseUrl + path;
    }

    private static Process StartProcess(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        if (details.Length > 300)
            details = details[..300];

        throw new HttpRequestException(
            $"Could not {operation}: {(int)response.StatusCode} {response.StatusCode}. {details}".Trim());
    }

    public void Dispose()
    {
        _http.Dispose();
        StopOwnedProcess(_mgbaProcess);
        StopOwnedProcess(_httpProcess);
    }

    private static void StopOwnedProcess(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited while the app was shutting down.
        }
        finally
        {
            process.Dispose();
        }
    }
}
