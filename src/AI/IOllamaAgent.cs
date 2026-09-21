using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.Models;

namespace OllamaNetGB.AI;

public interface IOllamaAgent
{
    Task<CognitiveTurn> DecideAsync(
        byte[] screenshotPng,
        byte[]? previousScreenshotPng,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        AgentContext context,
        CancellationToken cancellationToken);
}
