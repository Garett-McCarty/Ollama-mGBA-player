using OllamaNetGB.Models;

namespace OllamaNetGB.Metrics;

public interface IRunMetricsStore
{
    string DatabasePath { get; }
    string Status { get; }

    Task StartRunAsync(CancellationToken cancellationToken);

    Task RecordTurnAsync(
        CoordinatorTurnResult result,
        CancellationToken cancellationToken);

    Task RecordFailureAsync(
        Exception exception,
        double elapsedMilliseconds,
        CancellationToken cancellationToken);

    Task EndRunAsync(CancellationToken cancellationToken);
}
