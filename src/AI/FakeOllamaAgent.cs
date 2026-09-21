using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OllamaNetGB.AI;
using OllamaNetGB.Models;

namespace OllamaNetGB.Ai;

public sealed class FakeOllamaAgent : IOllamaAgent
{
    private static readonly GbaButton[] buttons =
    {
        GbaButton.A,
        GbaButton.Right,
        GbaButton.B,
        GbaButton.Down
    };

    private readonly Random random = new();

    public async Task<CognitiveTurn> DecideAsync(
        byte[] screenshotPng,
        byte[]? previousScreenshotPng,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        await Task.Delay(800, cancellationToken);
        var button = buttons[random.Next(buttons.Length)];
        return new CognitiveTurn(
            new Observation(ScreenKind.Gameplay, "Fake observation", "", 1),
            new OutcomeAssessment(OutcomeKind.Unknown, "Fake outcome", false, 1),
            new GoalProposal(context.ActiveGoal, true, false),
            new TaskProposal("Explore", "The visible state changes", false, false, []),
            new AgentDecision(button, 80, $"Fake decision: press {button}."),
            []);
    }
}
