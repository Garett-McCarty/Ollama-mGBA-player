using System.Text.Json;
using Microsoft.Data.Sqlite;
using OllamaNetGB.AI;
using OllamaNetGB.Config;
using OllamaNetGB.Models;

namespace OllamaNetGB.Metrics;

/// <summary>
/// Records lightweight, local run and turn telemetry for debugging and model
/// comparisons. Metrics failures never stop emulator play.
/// </summary>
public sealed class SqliteRunMetricsStore : IRunMetricsStore
{
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _runId;
    private string _status = "Metrics ready";

    public SqliteRunMetricsStore(ISettingsService settings)
    {
        _settings = settings;
        var directory = Path.GetDirectoryName(settings.ConfigFilePath)
            ?? AppContext.BaseDirectory;
        DatabasePath = Path.Combine(directory, "runs.db");
    }

    public string DatabasePath { get; }
    public string Status => _status;

    public async Task StartRunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            _runId = Guid.NewGuid().ToString("N");

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runs (id, started_at, game_id, model, status)
                VALUES ($id, $startedAt, $gameId, $model, 'Running');
                """;
            command.Parameters.AddWithValue("$id", _runId);
            command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$gameId", CurrentGameId());
            command.Parameters.AddWithValue("$model", _settings.Current.OllamaModel);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _status = $"Recording run {_runId[..8]}";
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
        {
            _status = $"Metrics offline: {exception.Message}";
            _runId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordTurnAsync(
        CoordinatorTurnResult result,
        CancellationToken cancellationToken)
    {
        if (_runId is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var metrics = result.Turn.Metrics;
            command.CommandText = """
                INSERT INTO turns (
                    run_id, occurred_at, game_id, model, compatibility_profile,
                    observation, goal, task, actions_json, memories_json,
                    total_duration_ms, load_duration_ms, prompt_eval_count,
                    prompt_eval_duration_ms, eval_count, eval_duration_ms,
                    tokens_per_second, valid_response, error, stuck_count,
                    frame_changed, raw_response)
                VALUES (
                    $runId, $occurredAt, $gameId, $model, $profile,
                    $observation, $goal, $task, $actions, $memories,
                    $totalMs, $loadMs, $promptCount, $promptMs, $evalCount,
                    $evalMs, $tokensPerSecond, 1, NULL, $stuckCount,
                    $frameChanged, $rawResponse);
                """;
            command.Parameters.AddWithValue("$runId", _runId);
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$gameId", CurrentGameId());
            command.Parameters.AddWithValue("$model", metrics.Model);
            command.Parameters.AddWithValue("$profile", metrics.CompatibilityProfile);
            command.Parameters.AddWithValue("$observation", result.Turn.Observation.Summary);
            command.Parameters.AddWithValue("$goal", result.ActiveGoal);
            command.Parameters.AddWithValue("$task", result.ActiveTask);
            command.Parameters.AddWithValue("$actions", JsonSerializer.Serialize(result.Turn.Actions));
            command.Parameters.AddWithValue("$memories", JsonSerializer.Serialize(result.Turn.Memories));
            command.Parameters.AddWithValue("$totalMs", metrics.TotalDurationMs);
            command.Parameters.AddWithValue("$loadMs", metrics.LoadDurationMs);
            command.Parameters.AddWithValue("$promptCount", metrics.PromptEvalCount);
            command.Parameters.AddWithValue("$promptMs", metrics.PromptEvalDurationMs);
            command.Parameters.AddWithValue("$evalCount", metrics.EvalCount);
            command.Parameters.AddWithValue("$evalMs", metrics.EvalDurationMs);
            command.Parameters.AddWithValue("$tokensPerSecond", metrics.TokensPerSecond);
            command.Parameters.AddWithValue("$stuckCount", result.StuckCount);
            command.Parameters.AddWithValue("$frameChanged", result.FrameChanged ? 1 : 0);
            command.Parameters.AddWithValue("$rawResponse", metrics.RawResponse);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _status = $"Recorded {metrics.TotalDurationMs / 1000:0.0}s · {metrics.TokensPerSecond:0.0} tok/s";
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
        {
            _status = $"Metrics offline: {exception.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordFailureAsync(
        Exception exception,
        double elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (_runId is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO turns (
                    run_id, occurred_at, game_id, model, compatibility_profile,
                    total_duration_ms, valid_response, error)
                VALUES ($runId, $occurredAt, $gameId, $model, $profile,
                        $totalMs, 0, $error);
                """;
            var model = _settings.Current.OllamaModel;
            command.Parameters.AddWithValue("$runId", _runId);
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$gameId", CurrentGameId());
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$profile", ModelCompatibility.Resolve(model).Name);
            command.Parameters.AddWithValue("$totalMs", elapsedMilliseconds);
            command.Parameters.AddWithValue("$error", exception.Message);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _status = "Recorded invalid model response";
        }
        catch (Exception metricsException) when (
            metricsException is SqliteException or IOException or InvalidOperationException)
        {
            _status = $"Metrics offline: {metricsException.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndRunAsync(CancellationToken cancellationToken)
    {
        if (_runId is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE runs SET ended_at = $endedAt, status = 'Completed'
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$endedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", _runId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _status = $"Run saved to {DatabasePath}";
            _runId = null;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
        {
            _status = $"Metrics offline: {exception.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                game_id TEXT NOT NULL,
                model TEXT NOT NULL,
                status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS turns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                game_id TEXT NOT NULL,
                model TEXT NOT NULL,
                compatibility_profile TEXT NOT NULL,
                observation TEXT,
                goal TEXT,
                task TEXT,
                actions_json TEXT,
                memories_json TEXT,
                total_duration_ms REAL NOT NULL DEFAULT 0,
                load_duration_ms REAL NOT NULL DEFAULT 0,
                prompt_eval_count INTEGER NOT NULL DEFAULT 0,
                prompt_eval_duration_ms REAL NOT NULL DEFAULT 0,
                eval_count INTEGER NOT NULL DEFAULT 0,
                eval_duration_ms REAL NOT NULL DEFAULT 0,
                tokens_per_second REAL NOT NULL DEFAULT 0,
                valid_response INTEGER NOT NULL,
                error TEXT,
                stuck_count INTEGER NOT NULL DEFAULT 0,
                frame_changed INTEGER NOT NULL DEFAULT 0,
                raw_response TEXT,
                FOREIGN KEY (run_id) REFERENCES runs(id)
            );

            CREATE INDEX IF NOT EXISTS idx_turns_run_id ON turns(run_id);
            CREATE INDEX IF NOT EXISTS idx_turns_model ON turns(model);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() =>
        new($"Data Source={DatabasePath};Cache=Shared");

    private string CurrentGameId() =>
        string.IsNullOrWhiteSpace(_settings.Current.GameId)
            ? "unknown-gba-game"
            : _settings.Current.GameId.Trim();
}
