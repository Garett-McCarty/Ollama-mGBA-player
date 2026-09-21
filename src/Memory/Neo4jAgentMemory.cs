using Neo4j.Driver;
using OllamaNetGB.Config;
using OllamaNetGB.Models;

namespace OllamaNetGB.Memory;

/// <summary>
/// Durable, game-scoped semantic memory. Database failures are intentionally
/// non-fatal so an emulator session can continue without persistence.
/// </summary>
public sealed class Neo4jAgentMemory : IAgentMemory, IDisposable
{
    private readonly ISettingsService _settings;
    private IDriver? _driver;
    private string _connectionKey = "";
    private string _status = "Memory ready";

    public Neo4jAgentMemory(ISettingsService settings) => _settings = settings;

    public string Status => _status;

    public async Task<IReadOnlyList<MemoryRecord>> RecallAsync(
        string gameId,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var session = GetDriver().AsyncSession();
            var cursor = await session.RunAsync(
                """
                MATCH (m:AgentMemory {gameId: $gameId})
                RETURN m.sessionId AS sessionId,
                       m.kind AS kind,
                       m.summary AS summary,
                       m.confidence AS confidence,
                       m.source AS source,
                       m.createdAt AS createdAt,
                       coalesce(m.state, 'Candidate') AS state,
                       coalesce(m.userWeight, 1.0) AS userWeight,
                       coalesce(m.confirmationCount, 1) AS confirmationCount
                ORDER BY CASE coalesce(m.state, 'Candidate')
                           WHEN 'Pinned' THEN 4 WHEN 'Trusted' THEN 3 WHEN 'Confirmed' THEN 2
                           WHEN 'Candidate' THEN 1 ELSE 0 END DESC,
                         m.confidence * coalesce(m.userWeight, 1.0) DESC, m.createdAt DESC
                LIMIT $limit
                """,
                new { gameId, limit = Math.Clamp(limit, 1, 30) });

            var records = await cursor.ToListAsync(record => new MemoryRecord(
                gameId,
                record["sessionId"].As<string>(),
                Enum.TryParse<MemoryKind>(record["kind"].As<string>(), true, out var kind)
                    ? kind
                    : MemoryKind.Fact,
                record["summary"].As<string>(),
                record["confidence"].As<double>(),
                record["source"].As<string>(),
                DateTimeOffset.TryParse(record["createdAt"].As<string>(), out var created)
                    ? created
                    : DateTimeOffset.UtcNow,
                Enum.TryParse<MemoryState>(record["state"].As<string>(), true, out var state)
                    ? state : MemoryState.Candidate,
                record["userWeight"].As<double>(),
                record["confirmationCount"].As<long>()));

            _status = $"Memory connected ({records.Count} recalled)";
            return records;
        }
        catch (Exception exception) when (
            exception is Neo4jException or IOException or ArgumentException or InvalidOperationException)
        {
            _status = $"Memory offline: {exception.Message}";
            return [];
        }
    }

    public async Task RememberAsync(MemoryRecord memory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(memory.Summary))
            return;

        try
        {
            await using var session = GetDriver().AsyncSession();
            var cursor = await session.RunAsync(
                """
                MERGE (g:Game {gameId: $gameId})
                MERGE (m:AgentMemory {gameId: $gameId, kind: $kind, summary: $summary})
                ON CREATE SET m.sessionId = $sessionId,
                              m.confidence = $confidence,
                              m.source = $source,
                              m.createdAt = $createdAt,
                              m.confirmationCount = 1,
                              m.state = $state,
                              m.userWeight = $userWeight
                ON MATCH SET m.confidence = CASE
                                                WHEN m.confidence < $confidence THEN $confidence
                                                ELSE m.confidence
                                            END,
                             m.lastConfirmedAt = $createdAt,
                             m.confirmationCount = coalesce(m.confirmationCount, 1) + 1,
                             m.userWeight = coalesce(m.userWeight, 1.0),
                             m.state = CASE
                                WHEN m.state IN ['Pinned', 'Rejected'] THEN m.state
                                WHEN coalesce(m.confirmationCount, 1) + 1 >= 3 THEN 'Trusted'
                                WHEN coalesce(m.confirmationCount, 1) + 1 >= 2 THEN 'Confirmed'
                                ELSE coalesce(m.state, 'Candidate') END
                MERGE (g)-[:HAS_MEMORY]->(m)
                """,
                new
                {
                    gameId = memory.GameId,
                    sessionId = memory.SessionId,
                    kind = memory.Kind.ToString(),
                    summary = memory.Summary,
                    confidence = Math.Clamp(memory.Confidence, 0, 1),
                    source = memory.Source,
                    createdAt = memory.CreatedAt.ToString("O"),
                    state = memory.State.ToString(),
                    userWeight = Math.Clamp(memory.Weight, 0, 2)
                });
            await cursor.ConsumeAsync();
            _status = "Memory saved";
        }
        catch (Exception exception) when (
            exception is Neo4jException or IOException or ArgumentException or InvalidOperationException)
        {
            _status = $"Memory offline: {exception.Message}";
        }
    }

    public async Task<bool> UpdateAsync(string gameId, MemoryKind kind, string summary,
        MemoryState state, double weight, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = GetDriver().AsyncSession();
            var cursor = await session.RunAsync(
                """
                MATCH (m:AgentMemory {gameId: $gameId, kind: $kind, summary: $summary})
                SET m.state = $state, m.userWeight = $userWeight, m.updatedAt = $updatedAt
                RETURN count(m) AS updated
                """,
                new { gameId, kind = kind.ToString(), summary, state = state.ToString(),
                    userWeight = Math.Clamp(weight, 0, 2), updatedAt = DateTimeOffset.UtcNow.ToString("O") });
            var updated = (await cursor.SingleAsync())["updated"].As<long>();
            _status = updated > 0 ? $"Memory marked {state}" : "Memory was not found";
            return updated > 0;
        }
        catch (Exception exception) when (exception is Neo4jException or IOException or ArgumentException or InvalidOperationException)
        {
            _status = $"Memory offline: {exception.Message}";
            return false;
        }
    }

    public async Task<bool> DeleteAsync(
        string gameId,
        MemoryKind kind,
        string summary,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var session = GetDriver().AsyncSession();
            var cursor = await session.RunAsync(
                """
                MATCH (m:AgentMemory {gameId: $gameId, kind: $kind, summary: $summary})
                SET m.state = 'Rejected', m.updatedAt = $updatedAt
                RETURN count(m) AS deleted
                """,
                new
                {
                    gameId,
                    kind = kind.ToString(),
                    summary,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O")
                });
            var record = await cursor.SingleAsync();
            var deleted = record["deleted"].As<long>();
            _status = deleted > 0 ? "Memory rejected" : "Memory was already absent";
            return deleted > 0;
        }
        catch (Exception exception) when (
            exception is Neo4jException or IOException or ArgumentException or InvalidOperationException)
        {
            _status = $"Memory offline: {exception.Message}";
            return false;
        }
    }

    public async Task<bool> ClearAsync(string gameId, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = GetDriver().AsyncSession();
            var cursor = await session.RunAsync(
                """
                MATCH (m:AgentMemory {gameId: $gameId})
                WITH collect(m) AS memories
                FOREACH (memory IN memories | DETACH DELETE memory)
                RETURN size(memories) AS deleted
                """,
                new { gameId });
            var record = await cursor.SingleAsync();
            var deleted = record["deleted"].As<long>();
            _status = $"Cleared {deleted} memories for {gameId}";
            return true;
        }
        catch (Exception exception) when (
            exception is Neo4jException or IOException or ArgumentException or InvalidOperationException)
        {
            _status = $"Memory offline: {exception.Message}";
            return false;
        }
    }

    private IDriver GetDriver()
    {
        var current = _settings.Current;
        var key = $"{current.Neo4jUri}\n{current.Neo4jUser}\n{current.Neo4jPassword}";
        if (_driver is not null && string.Equals(key, _connectionKey, StringComparison.Ordinal))
            return _driver;

        if (_driver is not null)
            _driver.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _driver = GraphDatabase.Driver(
            current.Neo4jUri,
            AuthTokens.Basic(current.Neo4jUser, current.Neo4jPassword));
        _connectionKey = key;
        return _driver;
    }

    public void Dispose()
    {
        if (_driver is not null)
            _driver.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
