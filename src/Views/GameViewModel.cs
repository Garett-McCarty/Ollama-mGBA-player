using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaNetGB.Agent;
using OllamaNetGB.Config;
using OllamaNetGB.Emulator;
using OllamaNetGB.Models;

namespace OllamaNetGB.Views;

public partial class GameViewModel : ObservableObject
{
    private readonly IMgbaClient _mgba;
    private readonly AgentCoordinator _coordinator;
    private readonly ISettingsService _settings;

    private CancellationTokenSource? _cts;

    public GameViewModel(IMgbaClient mgba, AgentCoordinator coordinator, ISettingsService settings)
    {
        _mgba = mgba;
        _coordinator = coordinator;
        _settings = settings;
        GoalInput = settings.Current.SessionGoal;
    }

    [ObservableProperty] private Bitmap? _currentFrame;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private string _statusText = "Idle";

    [ObservableProperty] private string _currentButton = "—";
    [ObservableProperty] private string _currentReasoning = "";
    [ObservableProperty] private string _activeGoal = "Waiting for the first observation";
    [ObservableProperty] private string _activeTask = "—";
    [ObservableProperty] private string _lastObservation = "—";
    [ObservableProperty] private string _memoryStatus = "Memory not queried yet";
    [ObservableProperty] private string _goalInput = "";
    [ObservableProperty] private int _stuckCount;

    public ObservableCollection<DecisionLogEntry> Log { get; } = new();
    public ObservableCollection<AgentTaskItem> Tasks { get; } = new();
    public ObservableCollection<MemoryDisplayItem> Memories { get; } = new();
    public bool HasFrame => CurrentFrame is not null;
    public bool HasNoFrame => CurrentFrame is null;

    partial void OnCurrentFrameChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasFrame));
        OnPropertyChanged(nameof(HasNoFrame));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = "Starting mGBA — load the Lua script from Tools > Scripting if needed…";

        try
        {
            await _mgba.EnsureReadyAsync(_cts.Token);
            StatusText = "Running";
            await RunLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            IsThinking = false;
            if (_cts is not null) { _cts.Dispose(); _cts = null; }
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private async Task ApplyGoalAsync()
    {
        if (string.IsNullOrWhiteSpace(GoalInput))
            return;

        await _coordinator.SetGoalAsync(GoalInput, CancellationToken.None);
        ActiveGoal = _coordinator.ActiveGoal;
        ActiveTask = "—";
        Tasks.Clear();
        MemoryStatus = _coordinator.MemoryStatus;
        StatusText = "New objective applied";
    }

    [RelayCommand]
    private async Task ManualTapAsync(GbaButton button)
    {
        try
        {
            StatusText = "Connecting — load the Lua script from Tools > Scripting if needed…";
            await _mgba.EnsureReadyAsync(CancellationToken.None);
            StatusText = $"Manual input: {button}";
            await _mgba.TapAsync(button, 80, CancellationToken.None);
            await Task.Delay(100);

            var frame = await _mgba.GetScreenshotAsync(CancellationToken.None);
            if (frame is not null)
                UpdateFrame(frame);

            if (!IsRunning)
                StatusText = "Ready";
        }
        catch (Exception ex)
        {
            StatusText = $"Input error: {ex.Message}";
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var interval = Math.Clamp(_settings.Current.FrameIntervalMs, 100, 10_000);

        while (!ct.IsCancellationRequested)
        {
            var frame = await _mgba.GetScreenshotAsync(ct);
            if (frame is null)
            {
                StatusText = "No frame from emulator";
                await Task.Delay(interval, ct);
                continue;
            }

            UpdateFrame(frame);

            IsThinking = true;
            CoordinatorTurnResult result;
            try
            {
                result = await _coordinator.ProcessTurnAsync(frame, Log, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                StatusText = $"Ollama error: {ex.Message}";
                await Task.Delay(interval, ct);
                continue;
            }
            finally
            {
                IsThinking = false;
            }

            var decision = result.Turn.Action;
            ApplyCoordinatorState(result);
            await _mgba.TapAsync(decision.Button, decision.HoldMs, ct);
            AppendLog(decision);

            CurrentButton = decision.Button.ToString().ToUpperInvariant();
            CurrentReasoning = decision.Reasoning;

            await Task.Delay(interval, ct);
        }
    }

    private void ApplyCoordinatorState(CoordinatorTurnResult result)
    {
        ActiveGoal = result.ActiveGoal;
        ActiveTask = string.IsNullOrWhiteSpace(result.ActiveTask) ? "—" : result.ActiveTask;
        LastObservation = result.Turn.Observation.Summary;
        StuckCount = result.StuckCount;
        MemoryStatus = _coordinator.MemoryStatus;

        Tasks.Clear();
        foreach (var task in result.Tasks)
            Tasks.Add(task);

        Memories.Clear();
        foreach (var memory in result.Memories)
            Memories.Add(memory);
    }

    private void UpdateFrame(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var old = CurrentFrame;
        CurrentFrame = new Bitmap(ms);
        old?.Dispose();
    }

    private void AppendLog(AgentDecision decision)
    {
        var entry = new DecisionLogEntry(
            DateTime.Now, decision.Button, decision.HoldMs, decision.Reasoning);

        Log.Insert(0, entry); // newest first
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }
}
