using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.AI;
using OllamaNetGB.Models;

namespace OllamaNetGB.Ai;

public sealed class FakeOllamaAgent : IOllamaAgent
{
    private static readonly GbaButton[] Buttons = { GbaButton.A, GbaButton.Right, GbaButton.B, GbaButton.Down };
    private readonly Random _random = new();

    public async Task<PerceptionSnapshot> PerceiveAsync(IReadOnlyList<byte[]> chronologicalFrames, bool frameBatchStable, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        return new PerceptionSnapshot(
            new Observation(ScreenKind.Gameplay, "Fake observation", "", 1),
            frameBatchStable, true, "Fake temporal comparison", InferenceMetrics.Empty("fake-perception"));
    }

    public async Task<CognitiveTurn> DecideAsync(PerceptionSnapshot perception, IReadOnlyList<DecisionLogEntry> recentHistory, AgentContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        var button = Buttons[_random.Next(Buttons.Length)];
        return new CognitiveTurn(
            perception.Observation,
            new OutcomeAssessment(OutcomeKind.Unknown, "Fake outcome", false, 1),
            new GoalProposal(context.ActiveGoal, true, false),
            new TaskProposal("Explore", "The visible state changes", false, false, []),
            [new AgentDecision(button, 80, $"Fake decision: press {button}.")], [], InferenceMetrics.Empty("fake-planner"));
    }
}
