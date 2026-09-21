using CommunityToolkit.Mvvm.ComponentModel;

namespace OllamaNetGB.Models;

public enum ScreenKind
{
    Unknown,
    Title,
    Gameplay,
    Dialogue,
    Menu,
    Combat,
    Puzzle,
    Cutscene,
    Loading,
    GameOver
}

public enum OutcomeKind
{
    Unknown,
    NoChange,
    Progress,
    Success,
    Failure,
    Reset,
    GameOver
}

public enum MemoryKind
{
    Fact,
    Strategy,
    Landmark,
    Control,
    Progress,
    Failure,
    Warning
}

public enum MemoryState
{
    Candidate,
    Confirmed,
    Trusted,
    Pinned,
    Rejected
}


public sealed record PerceptionSnapshot(
    Observation Observation,
    bool ScreenStable,
    bool DialogueComplete,
    string ChangeSummary,
    InferenceMetrics Metrics);

public sealed record Observation(
    ScreenKind ScreenType,
    string Summary,
    string VisibleText,
    double Confidence);

public sealed record OutcomeAssessment(
    OutcomeKind Kind,
    string Summary,
    bool MeaningfulProgress,
    double Confidence);

public sealed record GoalProposal(
    string Description,
    bool KeepCurrent,
    bool CompletedCurrent);

public sealed record TaskProposal(
    string Description,
    string SuccessCondition,
    bool KeepCurrent,
    bool CompletedCurrent,
    IReadOnlyList<string> QueuedTasks);

public sealed record MemoryCandidate(
    MemoryKind Kind,
    string Summary,
    double Confidence);

public sealed record CognitiveTurn(
    Observation Observation,
    OutcomeAssessment Outcome,
    GoalProposal Goal,
    TaskProposal Task,
    IReadOnlyList<AgentDecision> Actions,
    IReadOnlyList<MemoryCandidate> Memories,
    InferenceMetrics Metrics)
{
    public AgentDecision Action => Actions.Count > 0
        ? Actions[0]
        : new AgentDecision(GbaButton.Wait, 250, "Wait because the model returned no actions.");
}

public sealed record InferenceMetrics(
    string Model,
    string CompatibilityProfile,
    double TotalDurationMs,
    double LoadDurationMs,
    long PromptEvalCount,
    double PromptEvalDurationMs,
    long EvalCount,
    double EvalDurationMs,
    double TokensPerSecond,
    string RawResponse)
{
    public static InferenceMetrics Empty(string model = "unknown") =>
        new(model, "unknown", 0, 0, 0, 0, 0, 0, 0, "");
}

public sealed record AgentContext(
    string RootGoal,
    string ActiveGoal,
    string ActiveTask,
    IReadOnlyList<string> QueuedTasks,
    int StuckCount,
    bool FrameChanged,
    string ProfilePrompt,
    IReadOnlyList<string> RelevantKnowledge,
    IReadOnlyList<string> RelevantMemories);

public sealed record AgentTaskItem(string Description, string Status);

public partial class MemoryDisplayItem : ObservableObject
{
    public MemoryDisplayItem(
        MemoryKind kind,
        string summary,
        double confidence,
        double weight,
        MemoryState state,
        long confirmationCount)
    {
        Kind = kind;
        Summary = summary;
        Confidence = confidence;
        _weight = weight;
        State = state;
        ConfirmationCount = confirmationCount;
    }

    public MemoryKind Kind { get; }
    public string Summary { get; }
    public double Confidence { get; }
    public MemoryState State { get; }
    public long ConfirmationCount { get; }
    public string ConfidenceText => $"{Confidence:P0}";
    public string StateText => $"{State} · {ConfirmationCount} confirmation{(ConfirmationCount == 1 ? "" : "s")}";
    public string EffectiveConfidenceText => $"Effective {Math.Clamp(Confidence * Weight, 0, 2):P0}";

    [ObservableProperty]
    private double _weight;

    partial void OnWeightChanged(double value) =>
        OnPropertyChanged(nameof(EffectiveConfidenceText));
}

public sealed record CoordinatorTurnResult(
    CognitiveTurn Turn,
    PerceptionSnapshot Perception,
    string ActiveGoal,
    string ActiveTask,
    IReadOnlyList<AgentTaskItem> Tasks,
    IReadOnlyList<MemoryDisplayItem> Memories,
    int StuckCount,
    bool FrameChanged);
