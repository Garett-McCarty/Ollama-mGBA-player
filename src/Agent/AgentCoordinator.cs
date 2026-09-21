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
	/// <summary>
	/// The default root goal for our agent
	/// </summary>
	private const string DefaultRootGoal =
		"Explore the game, learn its controls, avoid repeated failures, and make measurable progress.";

	/// <summary>
	/// The Ollama Agent interface
	/// </summary>
	private readonly IOllamaAgent _agent;

	/// <summary>
	/// The graph storage for our agent
	/// </summary>
	private readonly IAgentMemory _memory;

	/// <summary>
	/// The game profile provider for our agent to reference against our active game identifier
	/// </summary>
	private readonly IGameProfileProvider _profiles;

	/// <summary>
	/// The application settings service
	/// </summary>
	private readonly ISettingsService _settings;

	/// <summary>
	/// Determines frame progress
	/// </summary>
	private readonly FrameProgressDetector _progress = new();

	/// <summary>
	/// Current session identifier for this run
	/// </summary>
	private readonly string _sessionId = Guid.NewGuid().ToString("N");

	/// <summary>
	/// Queue of tasks our agent wants to complete
	/// </summary>
	private readonly List<AgentTaskItem> _tasks = [];

	/// <summary>
	/// The game profile
	/// </summary>
	private GameProfile? _profile;

	/// <summary>
	/// The game identifier that is loaded in mGBA
	/// </summary>
	private string _loadedGameId = "";

	/// <summary>
	/// The current goal of our agent
	/// </summary>
	private string _activeGoal = "";

	/// <summary>
	/// The current task of our agent
	/// </summary>
	private string _activeTask = "";

	/// <summary>
	/// Previous screenshot we captured
	/// </summary>
	private byte[]? _previousScreenshot;

	/// <summary>
	/// Construct a new AgentCoordinator
	/// </summary>
	/// <param name="agent"></param>
	/// <param name="memory"></param>
	/// <param name="profiles"></param>
	/// <param name="settings"></param>
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

	/// <summary>
	/// Get the agent's active goal
	/// </summary>
	public string ActiveGoal => _activeGoal;

	/// <summary>
	/// Get the agent's active task
	/// </summary>
	public string ActiveTask => _activeTask;

	/// <summary>
	/// 
	/// </summary>
	public string MemoryStatus => _memory.Status;

	public async Task<CoordinatorTurnResult> ProcessTurnAsync(
		byte[] screenshotPng,
		IReadOnlyList<DecisionLogEntry> recentHistory,
		CancellationToken cancellationToken)
	{
		var gameId = GetGameId();
		await EnsureProfileAsync(gameId, cancellationToken);
		var profile = _profile!;
		var visual = _progress.Observe(screenshotPng);
		var recalled = await _memory.RecallAsync(gameId, 8, cancellationToken);
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
			recalled.Select(memory => $"{memory.Kind}: {memory.Summary}").ToArray());

		var turn = await _agent.DecideAsync(
			screenshotPng,
			_previousScreenshot,
			recentHistory,
			context,
			cancellationToken);
		_previousScreenshot = screenshotPng.ToArray();

		ApplyGoal(turn.Goal, rootGoal);
		ApplyTasks(turn.Task);
		_progress.RecordAction(turn.Action.Button);
		if (turn.Outcome.MeaningfulProgress || turn.Outcome.Kind is OutcomeKind.Success)
			_progress.MarkProgress();

		await StoreMeaningfulMemoriesAsync(gameId, turn, cancellationToken);
		var latest = await _memory.RecallAsync(gameId, 8, cancellationToken);

		return new CoordinatorTurnResult(
			turn,
			_activeGoal,
			_activeTask,
			_tasks.ToArray(),
			latest.Select(ToDisplayItem).ToArray(),
			visual.StuckCount);
	}

	public async Task SetGoalAsync(string goal, CancellationToken cancellationToken)
	{
		goal = Clean(goal, 500);
		if (string.IsNullOrWhiteSpace(goal))
			return;

		_activeGoal = goal;
		_activeTask = "";
		_tasks.Clear();
		_previousScreenshot = null;
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

	private async Task EnsureProfileAsync(string gameId, CancellationToken cancellationToken)
	{
		if (_profile is not null && string.Equals(gameId, _loadedGameId, StringComparison.Ordinal))
			return;

		_profile = await _profiles.ResolveAsync(gameId, cancellationToken);
		_loadedGameId = gameId;
		_activeGoal = "";
		_activeTask = "";
		_tasks.Clear();
		_previousScreenshot = null;
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
		if (turn.Outcome.Kind is not OutcomeKind.Unknown and not OutcomeKind.NoChange &&
			turn.Outcome.Confidence >= 0.6)
		{
			var kind = turn.Outcome.Kind is OutcomeKind.Failure or OutcomeKind.GameOver
				? MemoryKind.Failure
				: MemoryKind.Progress;
			await RememberAsync(gameId, kind, turn.Outcome.Summary, turn.Outcome.Confidence, "evaluator", cancellationToken);
		}

		foreach (var candidate in turn.Memories
					 .Where(memory => memory.Confidence >= 0.6)
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
		new(memory.Kind.ToString(), memory.Summary, $"{memory.Confidence:P0}");

	private static string Clean(string? value, int maximumLength)
	{
		value = value?.Trim().ReplaceLineEndings(" ") ?? "";
		return value.Length <= maximumLength ? value : value[..maximumLength] + "…";
	}
}
