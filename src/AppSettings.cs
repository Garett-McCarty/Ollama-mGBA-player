
namespace OllamaNetGB;

public sealed class AppSettings
{
    public string MgbaBinaryPath { get; set; } = "";
    public string MgbaHttpBinaryPath { get; set; } = "";
    public string MgbaSocketLuaPath { get; set; } = "";
    public string MgbaHttpBaseUrl { get; set; } = "http://localhost:5000";
    public string OllamaBaseUri { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "gemma3:4b";
    public string OllamaPerceptionModel { get; set; } = "";
    public string OllamaPlannerModel { get; set; } = "";
    public string SessionGoal { get; set; } = "Explore the game, learn its controls, avoid repeated failures, and make measurable progress.";
    public string GamePrompt { get; set; } = "";
    public int FrameIntervalMs { get; set; } = 750;
    public int PerceptionFrameCount { get; set; } = 3;
    public int StableFrameCaptureDelayMs { get; set; } = 120;
    public int StableFrameMatchCount { get; set; } = 3;
    public int StableFrameTimeoutMs { get; set; } = 2500;
    public int DecisionHistoryCount { get; set; } = 12;
    public int MaxActionsPerTurn { get; set; } = 4;
    public double MemoryCandidateThreshold { get; set; } = 0.55;
    public double MemoryPromptThreshold { get; set; } = 0.50;
    public string Neo4jUri { get; set; } = "bolt://localhost:7687";
    public string Neo4jUser { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";
    public string GameId { get; set; } = "";
    public string RomPath { get; set; } = "";
}
