using OllamaNetGB.Models;

namespace OllamaNetGB.Memory;

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

    Task<bool> UpdateAsync(
        string gameId,
        MemoryKind kind,
        string summary,
        MemoryState state,
        double weight,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        string gameId,
        MemoryKind kind,
        string summary,
        CancellationToken cancellationToken);

    Task<bool> ClearAsync(
        string gameId,
        CancellationToken cancellationToken);
}

public sealed record MemoryRecord(
    string GameId,
    string SessionId,
    MemoryKind Kind,
    string Summary,
    double Confidence,
    string Source,
    DateTimeOffset CreatedAt,
    MemoryState State = MemoryState.Candidate,
    double Weight = 1,
    long ConfirmationCount = 1);
