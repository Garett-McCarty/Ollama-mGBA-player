namespace OllamaNetGB.AI;

public enum ResponseSchemaMode
{
    Full,
    Compact
}

public sealed record ModelCompatibilityProfile(
    string Name,
    ResponseSchemaMode SchemaMode,
    bool SupportsThinking,
    int ContextSize,
    int MaximumResponseTokens,
    int MaximumHistoryItems,
    int MaximumActions,
    double Temperature);

public static class ModelCompatibility
{
    public static ModelCompatibilityProfile Resolve(string modelName)
    {
        var name = modelName.Trim().ToLowerInvariant();

        if (name.Contains("qwen3-vl:2b", StringComparison.Ordinal))
        {
            return new ModelCompatibilityProfile(
                "Qwen3-VL 2B / compact",
                ResponseSchemaMode.Compact,
                true,
                4096,
                800,
                6,
                4,
                0);
        }

        if (name.Contains("qwen3-vl", StringComparison.Ordinal))
        {
            return new ModelCompatibilityProfile(
                "Qwen3-VL / strict",
                ResponseSchemaMode.Full,
                true,
                4096,
                1000,
                8,
                4,
                0);
        }

        if (name.Contains("qwen2.5vl", StringComparison.Ordinal))
        {
            return new ModelCompatibilityProfile(
                "Qwen2.5-VL / structured",
                ResponseSchemaMode.Full,
                false,
                4096,
                1000,
                8,
                4,
                0);
        }

        if (name.Contains("qwen3:", StringComparison.Ordinal) || name == "qwen3")
        {
            return new ModelCompatibilityProfile(
                "Qwen 3 / planner",
                ResponseSchemaMode.Compact,
                true,
                4096,
                700,
                8,
                4,
                0);
        }

        if (name.Contains("gemma3", StringComparison.Ordinal))
        {
            return new ModelCompatibilityProfile(
                "Gemma 3 Vision",
                ResponseSchemaMode.Compact,
                false,
                4096,
                900,
                6,
                3,
                0);
        }

        if (name.Contains("llama3.2-vision", StringComparison.Ordinal))
        {
            return new ModelCompatibilityProfile(
                "Llama 3.2 Vision",
                ResponseSchemaMode.Full,
                false,
                4096,
                1000,
                10,
                4,
                0.1);
        }

        return new ModelCompatibilityProfile(
            "Generic Ollama model",
            ResponseSchemaMode.Compact,
            false,
            4096,
            900,
            6,
            3,
            0);
    }
}
