using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OllamaNetGB.Views;

public partial class FilePickerWindow : Window
{
    private string _currentDirectory;

    public FilePickerWindow() : this("Select a file", Array.Empty<string>())
    {
    }

    public FilePickerWindow(string title, IReadOnlyCollection<string> extensions)
    {
        InitializeComponent();
        Title = title;

        // Always display every file. Linux executables commonly have no suffix,
        // so filtering only by .exe or .AppImage would hide valid selections.
        _ = extensions;
        _currentDirectory = GetStartingDirectory();
        LoadDirectory(_currentDirectory);
    }

    private void LoadDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var entries = directory
                .EnumerateDirectories()
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FilePickerEntry(item.FullName, $"📁  {item.Name}", true))
                .Concat(directory
                    .EnumerateFiles()
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new FilePickerEntry(item.FullName, item.Name, false)))
                .ToList();

            _currentDirectory = directory.FullName;
            PathBox.Text = _currentDirectory;
            EntriesList.ItemsSource = entries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PathBox.Text = $"{path} — {exception.Message}";
        }
    }

    private void OpenSelected()
    {
        if (EntriesList.SelectedItem is not FilePickerEntry entry)
            return;

        if (entry.IsDirectory)
            LoadDirectory(entry.FullPath);
        else
            Close(entry.FullPath);
    }

    private void Up_Click(object? sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentDirectory);
        if (parent is not null)
            LoadDirectory(parent.FullName);
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(PathBox.Text))
            return;

        var path = PathBox.Text.Trim();
        if (Directory.Exists(path))
            LoadDirectory(path);
        else if (File.Exists(path))
            Close(Path.GetFullPath(path));
    }

    private void EntriesList_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void Select_Click(object? sender, RoutedEventArgs e) => OpenSelected();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close((string?)null);

    private static string GetStartingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(home) ? home : Environment.CurrentDirectory;
    }
}

public sealed record FilePickerEntry(string FullPath, string DisplayName, bool IsDirectory);
