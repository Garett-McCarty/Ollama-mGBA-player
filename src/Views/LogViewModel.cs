using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaNetGB.Services;

namespace OllamaNetGB.Views;

public partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private long _lastId = -1;
    public ObservableCollection<LogEntry> Entries { get; } = new();
    [ObservableProperty] private LogEntry? _selectedEntry;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _storageStatus = "";
    [ObservableProperty] private string _copyStatus = "";
    public string SelectedText => SelectedEntry?.FullText ?? "Select an entry to inspect and copy its details.";
    partial void OnSelectedEntryChanged(LogEntry? value) => OnPropertyChanged(nameof(SelectedText));
    partial void OnErrorsOnlyChanged(bool value) { _lastId = -1; Refresh(); }
    partial void OnIsPausedChanged(bool value) { if (!value) Refresh(); }

    public LogViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (!IsPaused) Refresh(); };
        _timer.Start();
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var log = AppLog.Shared;
        StorageStatus = string.IsNullOrEmpty(log.PersistenceError)
            ? $"Saved to: {log.FilePath}"
            : $"File logging unavailable: {log.PersistenceError}. Entries remain available here.";
        var entries = log.Snapshot();
        var last = entries.LastOrDefault()?.Id ?? 0;
        if (_lastId == last) return;
        _lastId = last;
        // Keep a selected entry even if it ages out of the live history.
        var selected = SelectedEntry;
        var visible = entries.Where(e => !ErrorsOnly || e.Level == "Error").Reverse().ToList();
        if (selected is not null && !visible.Any(e => e.Id == selected.Id))
            visible.Add(selected);
        Entries.Clear();
        foreach (var entry in visible) Entries.Add(entry);
        SelectedEntry = selected;
    }

    public void Dispose() => _timer.Stop();
}
