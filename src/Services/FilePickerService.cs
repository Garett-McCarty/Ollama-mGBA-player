
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OllamaNetGB.Views;

namespace OllamaNetGB.Services;

public sealed class FilePickerService : IFilePickerService
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        if (_topLevel is null)
            throw new InvalidOperationException("The file picker is not attached to an open window.");

        // Avalonia's Linux storage provider normally talks to the desktop portal.
        // In X11-forwarded sessions the portal can accept the request without ever
        // mapping its dialog on the forwarded display. A native chooser inherits
        // DISPLAY and therefore behaves correctly in both local and SSH sessions.
        if (OperatingSystem.IsLinux())
        {
            var nativeResult = await TryLinuxFilePickerAsync(title, extensions);
            if (nativeResult.WasStarted)
                return nativeResult.Path;

            if (_topLevel is Window owner)
            {
                var dialog = new FilePickerWindow(title, extensions);
                return await dialog.ShowDialog<string?>(owner);
            }
        }

        var patterns = extensions
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .Select(ext => ext.TrimStart('.'))
            .Select(ext => new FilePickerFileType(ext.ToUpperInvariant())
            {
                Patterns = new[] { $"*.{ext}" }
            })
            .ToList();
        if (patterns.Count == 0)
        {
            patterns.Add(new FilePickerFileType("All Files") { Patterns = new[] { "*" } });
        }
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns,
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (_topLevel is null)
            return null;

        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private static async Task<(bool WasStarted, string? Path)> TryLinuxFilePickerAsync(
        string title,
        IReadOnlyCollection<string> extensions)
    {
        var zenity = new ProcessStartInfo("zenity")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        zenity.ArgumentList.Add("--file-selection");
        zenity.ArgumentList.Add($"--title={title}");

        var patterns = extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => $"*.{extension.TrimStart('.')}")
            .ToArray();
        if (patterns.Length > 0)
            zenity.ArgumentList.Add($"--file-filter=Supported files | {string.Join(' ', patterns)}");
        zenity.ArgumentList.Add("--file-filter=All files | *");

        try
        {
            using var process = Process.Start(zenity);
            if (process is null)
                return (false, null);

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, output.Trim())
                : (true, null); // Exit code 1 is the normal Cancel result.
        }
        catch (Win32Exception)
        {
            // zenity is optional. Fall through to Avalonia's platform picker.
            return (false, null);
        }
    }
}
