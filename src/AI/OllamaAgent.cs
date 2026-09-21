using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OllamaNetGB.Config;
using OllamaNetGB.Models;

namespace OllamaNetGB.AI;

/// <summary>
/// Sends the current frame and validated coordinator context to a vision model.
/// </summary>
public sealed class OllamaAgent : IOllamaAgent, IDisposable
{
    private const string SystemPrompt = """
        You are the perception, planning, and control component of a general-purpose Game Boy Advance agent.
        Inspect the current screenshot, assess the result of recent actions, maintain a semantic goal and task,
        and choose exactly one useful next button. Do not assume a specific game unless the supplied profile says so.
        Prefer observable evidence. Mark uncertain interpretations with lower confidence. A memory candidate must be
        a durable fact, strategy, landmark, control discovery, progress event, failure, or warning—not a guess.
        Keep reasoning short and player-facing; never provide hidden chain-of-thought. Return only the requested JSON.
        """;

    private readonly ISettingsService _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public OllamaAgent(ISettingsService settings) => _settings = settings;

    public async Task<CognitiveTurn> DecideAsync(
        byte[] screenshotPng,
        byte[]? previousScreenshotPng,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.OllamaModel))
            throw new InvalidOperationException("Choose an Ollama vision model in Settings.");

        var history = BuildHistory(recentHistory, settings.DecisionHistoryCount);
        var request = new
        {
            model = settings.OllamaModel.Trim(),
            stream = false,
            format = new
            {
                type = "object",
                properties = new
                {
                    observation = new
                    {
                        type = "object",
                        properties = new
                        {
                            screen_type = new { type = "string", @enum = Enum.GetNames<ScreenKind>() },
                            summary = new { type = "string" },
                            visible_text = new { type = "string" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 }
                        },
                        required = new[] { "screen_type", "summary", "visible_text", "confidence" }
                    },
                    outcome = new
                    {
                        type = "object",
                        properties = new
                        {
                            kind = new { type = "string", @enum = Enum.GetNames<OutcomeKind>() },
                            summary = new { type = "string" },
                            meaningful_progress = new { type = "boolean" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 }
                        },
                        required = new[] { "kind", "summary", "meaningful_progress", "confidence" }
                    },
                    goal = new
                    {
                        type = "object",
                        properties = new
                        {
                            description = new { type = "string" },
                            keep_current = new { type = "boolean" },
                            completed_current = new { type = "boolean" }
                        },
                        required = new[] { "description", "keep_current", "completed_current" }
                    },
                    task = new
                    {
                        type = "object",
                        properties = new
                        {
                            description = new { type = "string" },
                            success_condition = new { type = "string" },
                            keep_current = new { type = "boolean" },
                            completed_current = new { type = "boolean" },
                            queued_tasks = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                maxItems = 5
                            }
                        },
                        required = new[]
                        {
                            "description", "success_condition", "keep_current", "completed_current", "queued_tasks"
                        }
                    },
                    action = new
                    {
                        type = "object",
                        properties = new
                        {
                            button = new { type = "string", @enum = Enum.GetNames<GbaButton>() },
                            hold_ms = new { type = "integer", minimum = 16, maximum = 2000 },
                            reasoning = new { type = "string" }
                        },
                        required = new[] { "button", "hold_ms", "reasoning" }
                    },
                    memories = new
                    {
                        type = "array",
                        maxItems = 4,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                kind = new { type = "string", @enum = Enum.GetNames<MemoryKind>() },
                                summary = new { type = "string" },
                                confidence = new { type = "number", minimum = 0, maximum = 1 }
                            },
                            required = new[] { "kind", "summary", "confidence" }
                        }
                    }
                },
                required = new[] { "observation", "outcome", "goal", "task", "action", "memories" }
            },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = BuildContextPrompt(context, history, previousScreenshotPng is not null),
                    images = previousScreenshotPng is null
                        ? new[] { Convert.ToBase64String(screenshotPng) }
                        : new[]
                        {
                            Convert.ToBase64String(previousScreenshotPng),
                            Convert.ToBase64String(screenshotPng)
                        }
                }
            },
            options = new { temperature = 0.2 }
        };

        using var response = await _http.PostAsJsonAsync(
            BuildUrl(settings.OllamaBaseUri, "/api/chat"),
            request,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama returned {(int)response.StatusCode} {response.StatusCode}: {Trim(responseText, 400)}");

        using var envelope = JsonDocument.Parse(responseText);
        if (!envelope.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidDataException("Ollama's response did not contain message.content.");
        }

        var decisionJson = StripCodeFence(content.GetString() ?? string.Empty);
        using var decision = JsonDocument.Parse(decisionJson);
        var root = decision.RootElement;
        var observationJson = root.GetProperty("observation");
        var outcomeJson = root.GetProperty("outcome");
        var goalJson = root.GetProperty("goal");
        var taskJson = root.GetProperty("task");
        var actionJson = root.GetProperty("action");

        var buttonText = actionJson.GetProperty("button").GetString();
        if (!Enum.TryParse<GbaButton>(buttonText, ignoreCase: true, out var button))
            throw new InvalidDataException($"Ollama returned an unsupported button: {buttonText ?? "(null)"}.");

        var holdMs = Math.Clamp(actionJson.GetProperty("hold_ms").GetInt32(), 16, 2_000);
        var reasoning = actionJson.GetProperty("reasoning").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(reasoning))
            reasoning = $"Press {button}.";

        var queuedTasks = taskJson.GetProperty("queued_tasks")
            .EnumerateArray()
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(5)
            .ToArray();

        var memories = root.GetProperty("memories")
            .EnumerateArray()
            .Select(item => new MemoryCandidate(
                ParseEnum(item, "kind", MemoryKind.Fact),
                GetText(item, "summary", ""),
                GetConfidence(item, "confidence")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Summary))
            .Take(4)
            .ToArray();

        return new CognitiveTurn(
            new Observation(
                ParseEnum(observationJson, "screen_type", ScreenKind.Unknown),
                GetText(observationJson, "summary", "Unable to identify the current screen."),
                GetText(observationJson, "visible_text", ""),
                GetConfidence(observationJson, "confidence")),
            new OutcomeAssessment(
                ParseEnum(outcomeJson, "kind", OutcomeKind.Unknown),
                GetText(outcomeJson, "summary", "Outcome is not yet clear."),
                outcomeJson.GetProperty("meaningful_progress").GetBoolean(),
                GetConfidence(outcomeJson, "confidence")),
            new GoalProposal(
                GetText(goalJson, "description", context.ActiveGoal),
                goalJson.GetProperty("keep_current").GetBoolean(),
                goalJson.GetProperty("completed_current").GetBoolean()),
            new TaskProposal(
                GetText(taskJson, "description", context.ActiveTask),
                GetText(taskJson, "success_condition", "The visible game state meaningfully changes."),
                taskJson.GetProperty("keep_current").GetBoolean(),
                taskJson.GetProperty("completed_current").GetBoolean(),
                queuedTasks),
            new AgentDecision(button, holdMs, reasoning),
            memories);
    }

    private static string BuildContextPrompt(AgentContext context, string history, bool hasPreviousFrame)
    {
        static string Lines(IReadOnlyList<string> values) =>
            values.Count == 0 ? "(none)" : string.Join("\n", values.Select(value => "- " + value));

        return $"""
            ROOT GOAL (do not silently replace): {context.RootGoal}
            ACTIVE GOAL: {context.ActiveGoal}
            ACTIVE TASK: {context.ActiveTask}
            QUEUED TASKS:
            {Lines(context.QueuedTasks)}

            GAME PROFILE:
            {context.ProfilePrompt}

            RELEVANT KNOWLEDGE:
            {Lines(context.RelevantKnowledge)}

            RELEVANT LONG-TERM MEMORIES:
            {Lines(context.RelevantMemories)}

            VISUAL SIGNAL: frame_changed={context.FrameChanged}; stuck_count={context.StuckCount}
            IMAGE ORDER: {(hasPreviousFrame ? "first image is the previous frame; second image is the current frame" : "only the current frame is attached")}
            RECENT ACTIONS (newest first):
            {history}

            Evaluate the attached current frame. Preserve a useful goal/task when still valid; revise it when completed,
            disproven, blocked, or repeatedly failing. Choose one next GBA input.
            """;
    }

    private static TEnum ParseEnum<TEnum>(JsonElement parent, string property, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(parent.GetProperty(property).GetString(), true, out var value)
            ? value
            : fallback;

    private static string GetText(JsonElement parent, string property, string fallback)
    {
        var value = parent.GetProperty(property).GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : Trim(value, 1_000);
    }

    private static double GetConfidence(JsonElement parent, string property) =>
        Math.Clamp(parent.GetProperty(property).GetDouble(), 0, 1);

    private static string BuildHistory(IReadOnlyList<DecisionLogEntry> history, int requestedCount)
    {
        var count = Math.Clamp(requestedCount, 0, 30);
        if (count == 0 || history.Count == 0)
            return "(none)";

        var builder = new StringBuilder();
        for (var index = 0; index < Math.Min(count, history.Count); index++)
        {
            var item = history[index];
            builder.Append("- ")
                .Append(item.ButtonText)
                .Append(" for ")
                .Append(item.HoldMs)
                .Append(" ms: ")
                .AppendLine(Trim(item.Reasoning.ReplaceLineEndings(" "), 160));
        }

        return builder.ToString();
    }

    private static string BuildUrl(string baseUrl, string path)
    {
        baseUrl = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("The Ollama URL in Settings is invalid.");

        return baseUrl + path;
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    public void Dispose() => _http.Dispose();
}
