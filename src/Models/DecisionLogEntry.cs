using System;

namespace OllamaNetGB.Models;

public sealed record DecisionLogEntry(
    DateTime Timestamp,
    GbaButton Button,
    int HoldMs,
    string Reasoning
)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string ButtonText => Button.ToString().ToUpperInvariant();
}