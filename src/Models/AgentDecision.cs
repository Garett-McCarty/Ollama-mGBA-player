
namespace OllamaNetGB.Models;

public sealed record AgentDecision(GbaButton Button, int HoldMs, string Reasoning);