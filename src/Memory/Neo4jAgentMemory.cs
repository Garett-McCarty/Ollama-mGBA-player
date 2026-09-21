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
                       m.createdAt AS createdAt
                ORDER BY m.confidence DESC, m.createdAt DESC
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
					: DateTimeOffset.UtcNow));

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
                              m.confirmationCount = 1
                ON MATCH SET m.confidence = CASE
                                                WHEN m.confidence < $confidence THEN $confidence
                                                ELSE m.confidence
                                            END,
                             m.lastConfirmedAt = $createdAt,
                             m.confirmationCount = coalesce(m.confirmationCount, 1) + 1
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
					createdAt = memory.CreatedAt.ToString("O")
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
