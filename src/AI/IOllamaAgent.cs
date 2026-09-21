using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.Models;

namespace OllamaNetGB.AI;

public interface IOllamaAgent
{
    Task<PerceptionSnapshot> PerceiveAsync(
        IReadOnlyList<byte[]> chronologicalFrames,
        bool frameBatchStable,
        CancellationToken cancellationToken);

    Task<CognitiveTurn> DecideAsync(
        PerceptionSnapshot perception,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        AgentContext context,
        CancellationToken cancellationToken);
}
