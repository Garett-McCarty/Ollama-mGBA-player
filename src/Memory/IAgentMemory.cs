using OllamaNetGB.Models;

namespace OllamaNetGB.Memory;

/// <summary>
/// Interface for interacting with agent memory
/// </summary>
public interface IAgentMemory
{
	string Status { get; }

	Task<IReadOnlyList<MemoryRecord>> RecallAsync(
		string gameId,
		int limit,
		CancellationToken cancellationToken);

	Task RememberAsync(
		MemoryRecord memory,
		CancellationToken cancellationToken);
}

public sealed record MemoryRecord(
	string GameId,
	string SessionId,
	MemoryKind Kind,
	string Summary,
	double Confidence,
	string Source,
	DateTimeOffset CreatedAt);
