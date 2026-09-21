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
    AgentDecision Action,
    IReadOnlyList<MemoryCandidate> Memories);

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

public sealed record MemoryDisplayItem(
    string Kind,
    string Summary,
    string ConfidenceText);

public sealed record CoordinatorTurnResult(
    CognitiveTurn Turn,
    string ActiveGoal,
    string ActiveTask,
    IReadOnlyList<AgentTaskItem> Tasks,
    IReadOnlyList<MemoryDisplayItem> Memories,
    int StuckCount);
