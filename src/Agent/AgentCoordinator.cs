using OllamaNetGB.AI;
using OllamaNetGB.Config;
using OllamaNetGB.Memory;
using OllamaNetGB.Models;
using OllamaNetGB.Profiles;

namespace OllamaNetGB.Agent;

/// <summary>
/// Owns validated agent state. Ollama proposes cognitive updates, but only this
/// coordinator changes goals, tasks, progress state, and durable memory.
/// </summary>
public sealed class AgentCoordinator
{
    private const string DefaultRootGoal =
        "Explore the game, learn its controls, avoid repeated failures, and make measurable progress.";

    private readonly IOllamaAgent _agent;
    private readonly IAgentMemory _memory;
    private readonly IGameProfileProvider _profiles;
    private readonly ISettingsService _settings;
    private readonly FrameProgressDetector _progress = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly List<AgentTaskItem> _tasks = [];

    private GameProfile? _profile;
    private string _loadedGameId = "";
    private string _activeGoal = "";
    private string _activeTask = "";
    private PerceptionSnapshot? _previousPerception;

    public AgentCoordinator(
        IOllamaAgent agent,
        IAgentMemory memory,
        IGameProfileProvider profiles,
        ISettingsService settings)
    {
        _agent = agent;
        _memory = memory;
        _profiles = profiles;
        _settings = settings;
    }

    public string ActiveGoal => _activeGoal;
    public string ActiveTask => _activeTask;
    public string MemoryStatus => _memory.Status;

    public async Task<CoordinatorTurnResult> ProcessTurnAsync(
        IReadOnlyList<byte[]> chronologicalFrames,
        bool frameBatchStable,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        CancellationToken cancellationToken)
    {
        if (chronologicalFrames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(chronologicalFrames));

        var screenshotPng = chronologicalFrames[^1];
        var gameId = GetGameId();
        await EnsureProfileAsync(gameId, cancellationToken);
        var profile = _profile!;
        var visual = _progress.Observe(screenshotPng);
        var recalled = await _memory.RecallAsync(gameId, 30, cancellationToken);
        var rootGoal = GetRootGoal(profile);

        if (string.IsNullOrWhiteSpace(_activeGoal))
            _activeGoal = rootGoal;

        var query = $"{_activeGoal} {_activeTask}";
        var knowledge = _profiles.Search(profile, query);
        var context = new AgentContext(
            rootGoal,
            _activeGoal,
            _activeTask,
            _tasks.Where(task => task.Status == "Queued").Select(task => task.Description).ToArray(),
            visual.StuckCount,
            visual.Changed,
            CombineProfilePrompt(profile),
            knowledge,
            recalled
                .Where(memory => memory.State is MemoryState.Confirmed or MemoryState.Trusted or MemoryState.Pinned)
                .Where(memory => memory.Confidence * memory.Weight >= _settings.Current.MemoryPromptThreshold)
                .OrderByDescending(memory => memory.State == MemoryState.Pinned)
                .ThenByDescending(memory => memory.Confidence * memory.Weight)
                .Take(8)
                .Select(memory => $"{memory.Kind} ({memory.State}, {memory.Confidence * memory.Weight:P0}): {memory.Summary}")
                .ToArray());

        var perception = !visual.Changed && _previousPerception is not null
            ? _previousPerception with
            {
                ScreenStable = frameBatchStable,
                ChangeSummary = "Final frame is unchanged since the previous decision.",
                Metrics = InferenceMetrics.Empty("perception-cache")
            }
            : await _agent.PerceiveAsync(chronologicalFrames, frameBatchStable, cancellationToken);

        var turn = await _agent.DecideAsync(perception, recentHistory, context, cancellationToken);
        _previousPerception = perception;

        ApplyGoal(turn.Goal, rootGoal);
        ApplyTasks(turn.Task);
        foreach (var action in turn.Actions)
            _progress.RecordAction(action.Button);
        if (turn.Outcome.MeaningfulProgress || turn.Outcome.Kind is OutcomeKind.Success)
            _progress.MarkProgress();

        await StoreMeaningfulMemoriesAsync(gameId, turn, cancellationToken);
        var latest = await _memory.RecallAsync(gameId, 30, cancellationToken);

        return new CoordinatorTurnResult(
            turn,
            perception,
            _activeGoal,
            _activeTask,
            _tasks.ToArray(),
            latest.Select(ToDisplayItem).ToArray(),
            visual.StuckCount,
            visual.Changed);
    }

    public async Task SetGoalAsync(string goal, CancellationToken cancellationToken)
    {
        goal = Clean(goal, 500);
        if (string.IsNullOrWhiteSpace(goal))
            return;

        _activeGoal = goal;
        _activeTask = "";
        _tasks.Clear();
        _previousPerception = null;
        await _memory.RememberAsync(
            new MemoryRecord(
                GetGameId(),
                _sessionId,
                MemoryKind.Progress,
                $"User set a new objective: {goal}",
                1,
                "user",
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public Task<bool> DeleteMemoryAsync(
        MemoryDisplayItem memory,
        CancellationToken cancellationToken) =>
        _memory.DeleteAsync(
            GetGameId(),
            memory.Kind,
            memory.Summary,
            cancellationToken);

    public Task<bool> UpdateMemoryAsync(MemoryDisplayItem memory, MemoryState state,
        double weight, CancellationToken cancellationToken) =>
        _memory.UpdateAsync(GetGameId(), memory.Kind, memory.Summary, state,
            Math.Clamp(weight, 0, 2), cancellationToken);

    public async Task<bool> ClearMemoriesAsync(
        CancellationToken cancellationToken)
        => await _memory.ClearAsync(GetGameId(), cancellationToken);

    private async Task EnsureProfileAsync(string gameId, CancellationToken cancellationToken)
    {
        if (_profile is not null && string.Equals(gameId, _loadedGameId, StringComparison.Ordinal))
            return;

        _profile = await _profiles.ResolveAsync(gameId, cancellationToken);
        _loadedGameId = gameId;
        _activeGoal = "";
        _activeTask = "";
        _tasks.Clear();
        _previousPerception = null;
    }

    private string GetRootGoal(GameProfile profile)
    {
        var configured = _settings.Current.SessionGoal?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? DefaultRootGoal : Clean(configured, 800);
    }

    private string CombineProfilePrompt(GameProfile profile)
    {
        var configured = _settings.Current.GamePrompt?.Trim();
        return string.Join(
            "\n",
            new[] { profile.Prompt, configured }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private void ApplyGoal(GoalProposal proposal, string rootGoal)
    {
        if (proposal.CompletedCurrent)
            _activeGoal = "";

        if (!proposal.KeepCurrent || string.IsNullOrWhiteSpace(_activeGoal))
            _activeGoal = Clean(proposal.Description, 500);

        if (string.IsNullOrWhiteSpace(_activeGoal))
            _activeGoal = rootGoal;
    }

    private void ApplyTasks(TaskProposal proposal)
    {
        if (proposal.CompletedCurrent && _tasks.Count > 0)
        {
            _tasks[0] = _tasks[0] with { Status = "Completed" };
            _activeTask = "";
        }

        if (!proposal.KeepCurrent || string.IsNullOrWhiteSpace(_activeTask))
            _activeTask = Clean(proposal.Description, 300);

        _tasks.RemoveAll(task => task.Status == "Active" || task.Status == "Queued");
        if (!string.IsNullOrWhiteSpace(_activeTask))
            _tasks.Insert(0, new AgentTaskItem(_activeTask, "Active"));

        foreach (var queued in proposal.QueuedTasks
                     .Select(task => Clean(task, 300))
                     .Where(task => !string.IsNullOrWhiteSpace(task))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(5))
        {
            if (!string.Equals(queued, _activeTask, StringComparison.OrdinalIgnoreCase))
                _tasks.Add(new AgentTaskItem(queued, "Queued"));
        }

        while (_tasks.Count > 8)
            _tasks.RemoveAt(_tasks.Count - 1);
    }

    private async Task StoreMeaningfulMemoriesAsync(
        string gameId,
        CognitiveTurn turn,
        CancellationToken cancellationToken)
    {
        var threshold = Math.Clamp(_settings.Current.MemoryCandidateThreshold, 0, 1);
        if (turn.Outcome.Kind is not OutcomeKind.Unknown and not OutcomeKind.NoChange &&
            turn.Outcome.Confidence >= threshold)
        {
            var kind = turn.Outcome.Kind is OutcomeKind.Failure or OutcomeKind.GameOver
                ? MemoryKind.Failure
                : MemoryKind.Progress;
            await RememberAsync(gameId, kind, turn.Outcome.Summary, turn.Outcome.Confidence, "evaluator", cancellationToken);
        }

        foreach (var candidate in turn.Memories
                     .Where(memory =>
                         memory.Confidence >= threshold)
                     .Take(4))
        {
            await RememberAsync(
                gameId,
                candidate.Kind,
                candidate.Summary,
                candidate.Confidence,
                "agent",
                cancellationToken);
        }
    }

    private Task RememberAsync(
        string gameId,
        MemoryKind kind,
        string summary,
        double confidence,
        string source,
        CancellationToken cancellationToken) =>
        _memory.RememberAsync(
            new MemoryRecord(
                gameId,
                _sessionId,
                kind,
                Clean(summary, 800),
                Math.Clamp(confidence, 0, 1),
                source,
                DateTimeOffset.UtcNow),
            cancellationToken);

    private string GetGameId() =>
        string.IsNullOrWhiteSpace(_settings.Current.GameId)
            ? "unknown-gba-game"
            : _settings.Current.GameId.Trim();

    private static MemoryDisplayItem ToDisplayItem(MemoryRecord memory) =>
        new(memory.Kind, memory.Summary, memory.Confidence, memory.Weight,
            memory.State, memory.ConfirmationCount);

    private static string Clean(string? value, int maximumLength)
    {
        value = value?.Trim().ReplaceLineEndings(" ") ?? "";
        return value.Length <= maximumLength ? value : value[..maximumLength] + "…";
    }
}
