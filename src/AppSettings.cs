
namespace OllamaNetGB;

/// <summary>
/// Settings for our application runtime
/// </summary>
public sealed class AppSettings
{
	/// <summary>
	/// The file path of our mGBA binary
	/// </summary>
	public string MgbaBinaryPath { get; set; } = "";

	/// <summary>
	/// The file path of our mGBA-http binary
	/// </summary>
	public string MgbaHttpBinaryPath { get; set; } = "";

	/// <summary>
	/// The file path of our mGBA-SocketLua script that connects our mGBA process to our mGBA-http process.
	/// </summary>
	public string MgbaSocketLuaPath { get; set; } = "";

	/// <summary>
	/// The host connection uri for our mGBA-http process.
	/// </summary>
	public string MgbaHttpBaseUrl { get; set; } = "http://localhost:5000";

	/// <summary>
	/// The host connection uri for our Ollama process.
	/// </summary>
	public string OllamaBaseUri { get; set; } = "http://localhost:11434";

	/// <summary>
	/// The model we want Ollama to use to play our gameboy game.
	/// </summary>
	public string OllamaModel { get; set; } = "qwen2.5vl:3b";

	/// <summary>
	/// The session goal of our agent to play our gameboy game.
	/// </summary>
	public string SessionGoal { get; set; } = "Explore the game, learn its controls, avoid repeated failures, and make measurable progress.";

	/// <summary>
	/// The optional game prompt to help our agent play our gameboy game.
	/// </summary>
	public string GamePrompt { get; set; } = "";

	/// <summary>
	/// How often our game screenshot will be sent to our agent.
	/// </summary>
	public int FrameIntervalMs { get; set; } = 1500;

	/// <summary>
	/// The amount of decisions to store in history to determine our next action.
	/// </summary>
	public int DecisionHistoryCount { get; set; } = 12;

	/// <summary>
	/// The host connection uri for our Neo4j process.
	/// </summary>
	public string Neo4jUri { get; set; } = "bolt://localhost:7687";

	/// <summary>
	/// The username for our Neo4j process access
	/// </summary>
	public string Neo4jUser { get; set; } = "neo4j";

	/// <summary>
	/// The password for our Neo4j process access
	/// </summary>
	public string Neo4jPassword { get; set; } = "";

	/// <summary>
	/// The game identifier used to pair profiles and such.
	/// </summary>
	public string GameId { get; set; } = "";

	/// <summary>
	/// The file path to our game rom to launch with mGBA.
	/// </summary>
	public string RomPath { get; set; } = "";
}
