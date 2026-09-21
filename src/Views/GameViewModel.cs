using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using OllamaNetGB.Metrics;

namespace OllamaNetGB.Views;

public partial class GameViewModel : ObservableObject
{
    private readonly IMgbaClient _mgba;
    private readonly AgentCoordinator _coordinator;
    private readonly ISettingsService _settings;
    private readonly IRunMetricsStore _metrics;

    private CancellationTokenSource? _cts;

    public GameViewModel(IMgbaClient mgba, AgentCoordinator coordinator, ISettingsService settings,
        IRunMetricsStore metrics)
    {
        _mgba = mgba;
        _coordinator = coordinator;
        _settings = settings;
        _metrics = metrics;
        GoalInput = settings.Current.SessionGoal;
        MetricsStatus = metrics.Status;
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
    [ObservableProperty] private string _perceptionState = "Waiting for first frame";
    [ObservableProperty] private string _memoryStatus = "Memory not queried yet";
    [ObservableProperty] private string _goalInput = "";
    [ObservableProperty] private int _stuckCount;
    [ObservableProperty] private bool _isClearMemoryArmed;
    [ObservableProperty] private string _clearMemoryText = "Clear current game";
    [ObservableProperty] private string _metricsStatus = "Metrics ready";
    [ObservableProperty] private string _lastInferenceText = "No inference recorded yet";
    [ObservableProperty] private string _compatibilityProfile = "—";

    public string MetricsDatabasePath => _metrics.DatabasePath;

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
            await _metrics.StartRunAsync(_cts.Token);
            MetricsStatus = _metrics.Status;
            StatusText = "Running";
            await RunLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {ex}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            await _metrics.EndRunAsync(CancellationToken.None);
            MetricsStatus = _metrics.Status;
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
    private async Task DeleteMemoryAsync(MemoryDisplayItem? memory)
    {
        if (memory is null)
            return;

        if (await _coordinator.DeleteMemoryAsync(memory, CancellationToken.None))
            memory = ReplaceMemory(memory, MemoryState.Rejected);

        MemoryStatus = _coordinator.MemoryStatus;
        ResetClearMemoryConfirmation();
    }

    [RelayCommand]
    private Task ConfirmMemoryAsync(MemoryDisplayItem? memory) => ChangeMemoryStateAsync(memory, MemoryState.Confirmed);

    [RelayCommand]
    private Task TrustMemoryAsync(MemoryDisplayItem? memory) => ChangeMemoryStateAsync(memory, MemoryState.Trusted);

    [RelayCommand]
    private Task PinMemoryAsync(MemoryDisplayItem? memory) => ChangeMemoryStateAsync(memory, MemoryState.Pinned);

    [RelayCommand]
    private Task SaveMemoryWeightAsync(MemoryDisplayItem? memory) =>
        memory is null ? Task.CompletedTask : ChangeMemoryStateAsync(memory, memory.State);

    private async Task ChangeMemoryStateAsync(MemoryDisplayItem? memory, MemoryState state)
    {
        if (memory is null) return;
        if (await _coordinator.UpdateMemoryAsync(memory, state, memory.Weight, CancellationToken.None))
            ReplaceMemory(memory, state);
        MemoryStatus = _coordinator.MemoryStatus;
        ResetClearMemoryConfirmation();
    }

    private MemoryDisplayItem ReplaceMemory(MemoryDisplayItem old, MemoryState state)
    {
        var replacement = new MemoryDisplayItem(old.Kind, old.Summary, old.Confidence,
            old.Weight, state, old.ConfirmationCount);
        var index = Memories.IndexOf(old);
        if (index >= 0) Memories[index] = replacement;
        return replacement;
    }

    [RelayCommand]
    private async Task ClearMemoriesAsync()
    {
        if (!IsClearMemoryArmed)
        {
            IsClearMemoryArmed = true;
            ClearMemoryText = "Confirm clear all";
            MemoryStatus = "Press Confirm clear all to permanently forget this game's memories.";
            return;
        }

        if (await _coordinator.ClearMemoriesAsync(CancellationToken.None))
            Memories.Clear();

        MemoryStatus = _coordinator.MemoryStatus;
        ResetClearMemoryConfirmation();
    }

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
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {ex}");
            StatusText = $"Input error: {ex.Message}";
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _settings.Current;
            var interval = Math.Clamp(settings.FrameIntervalMs, 100, 10_000);
            var batch = await CapturePerceptionFramesAsync(ct);
            if (batch.Frames.Count == 0)
            {
                StatusText = "No frame from emulator";
                await Task.Delay(interval, ct);
                continue;
            }

            PerceptionState = batch.Stable
                ? $"{batch.Frames.Count} frame(s) · settled"
                : $"{batch.Frames.Count} frame(s) · animated/changing";
            StatusText = "Perceiving and planning…";
            IsThinking = true;
            CoordinatorTurnResult result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = await _coordinator.ProcessTurnAsync(batch.Frames, batch.Stable, Log, ct);
                stopwatch.Stop();
                await _metrics.RecordTurnAsync(result, ct);
                MetricsStatus = _metrics.Status;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {ex}");
                stopwatch.Stop();
                await _metrics.RecordFailureAsync(ex, stopwatch.Elapsed.TotalMilliseconds, CancellationToken.None);
                MetricsStatus = _metrics.Status;
                StatusText = $"Ollama error: {ex.Message}";
                await Task.Delay(interval, ct);
                continue;
            }
            finally
            {
                IsThinking = false;
            }

            ApplyCoordinatorState(result);
            var actions = result.Turn.Actions
                .Take(Math.Clamp(_settings.Current.MaxActionsPerTurn, 1, 4))
                .ToArray();

            for (var index = 0; index < actions.Length; index++)
            {
                var decision = actions[index];
                CurrentButton = decision.Button.ToString().ToUpperInvariant();
                CurrentReasoning = decision.Reasoning;
                AppendLog(decision);

                if (decision.Button == GbaButton.Wait)
                {
                    StatusText = $"Waiting {decision.HoldMs} ms for more visual context…";
                    await Task.Delay(decision.HoldMs, ct);
                    break;
                }

                StatusText = actions.Length > 1
                    ? $"Running combo {index + 1}/{actions.Length}"
                    : "Running";
                await _mgba.TapAsync(decision.Button, decision.HoldMs, ct);
                if (index + 1 < actions.Length)
                    await Task.Delay(100, ct);
            }

            StatusText = "Running";
            await Task.Delay(interval, ct);
        }
    }

    private async Task<FrameBatch> CapturePerceptionFramesAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var captureDelayMs = Math.Clamp(settings.StableFrameCaptureDelayMs, 50, 1000);
        var requiredMatches = Math.Clamp(settings.StableFrameMatchCount, 1, 10);
        var timeoutMs = Math.Clamp(settings.StableFrameTimeoutMs, 250, 10_000);
        var desiredFrames = Math.Clamp(settings.PerceptionFrameCount, 1, 4);

        var frames = new List<byte[]>(desiredFrames);
        byte[]? previous = null;
        var matchingFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        StatusText = "Sampling visual context…";

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var current = await _mgba.GetScreenshotAsync(ct);
            if (current is null)
            {
                await Task.Delay(captureDelayMs, ct);
                continue;
            }

            UpdateFrame(current);

            var sameAsPrevious = previous is not null && current.AsSpan().SequenceEqual(previous);
            if (!sameAsPrevious)
            {
                frames.Add(current);
                while (frames.Count > desiredFrames)
                    frames.RemoveAt(0);
                matchingFrames = 0;
            }
            else
            {
                matchingFrames++;
            }

            previous = current;
            if (matchingFrames >= requiredMatches)
            {
                if (frames.Count == 0)
                    frames.Add(current);
                else if (!frames[^1].AsSpan().SequenceEqual(current))
                    frames.Add(current);

                while (frames.Count > desiredFrames)
                    frames.RemoveAt(0);

                return new FrameBatch(frames.ToArray(), true);
            }

            await Task.Delay(captureDelayMs, ct);
        }

        if (previous is not null && (frames.Count == 0 || !frames[^1].AsSpan().SequenceEqual(previous)))
            frames.Add(previous);
        while (frames.Count > desiredFrames)
            frames.RemoveAt(0);

        return new FrameBatch(frames.ToArray(), false);
    }

    private void ApplyCoordinatorState(CoordinatorTurnResult result)
    {
        ActiveGoal = result.ActiveGoal;
        ActiveTask = string.IsNullOrWhiteSpace(result.ActiveTask) ? "—" : result.ActiveTask;
        LastObservation = result.Turn.Observation.Summary;
        PerceptionState = $"{(result.Perception.ScreenStable ? "settled" : "animated/changing")} · " +
                          $"dialogue {(result.Perception.DialogueComplete ? "complete" : "incomplete")} · " +
                          result.Perception.ChangeSummary;
        StuckCount = result.StuckCount;
        MemoryStatus = _coordinator.MemoryStatus;
        CompatibilityProfile = result.Turn.Metrics.CompatibilityProfile;
        var vision = result.Perception.Metrics;
        var planner = result.Turn.Metrics;
        var plannerOnlyMs = Math.Max(0, planner.TotalDurationMs - vision.TotalDurationMs);
        LastInferenceText = $"vision {vision.TotalDurationMs / 1000:0.0}s · " +
                            $"plan {plannerOnlyMs / 1000:0.0}s · total {planner.TotalDurationMs / 1000:0.0}s · " +
                            $"{planner.TokensPerSecond:0.0} tok/s";

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

    private void ResetClearMemoryConfirmation()
    {
        IsClearMemoryArmed = false;
        ClearMemoryText = "Clear current game";
    }

    private sealed record FrameBatch(IReadOnlyList<byte[]> Frames, bool Stable);
}
