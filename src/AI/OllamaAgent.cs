using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OllamaNetGB.Config;
using OllamaNetGB.Models;

namespace OllamaNetGB.AI;

/// <summary>
/// Two-stage Ollama agent. The perception pass is the only pass that receives
/// screenshots. The planner receives a compact text state packet plus goals,
/// history, profile knowledge, and memory.
/// </summary>
public sealed class OllamaAgent : IOllamaAgent, IDisposable
{
    private const string PerceptionSystemPrompt = """
        You are the visual perception stage for a general-purpose Game Boy Advance agent.
        Images are chronological, oldest to newest. The FINAL image is authoritative.
        Describe only what is visibly supported. Transcribe visible text conservatively.
        Never invent the missing ending of dialogue that is still typing.
        dialogue_complete means the latest dialogue/menu text appears fully drawn and safe
        for a planner to act on. Animated sprites or backgrounds do not by themselves make
        dialogue incomplete. Return only JSON matching the supplied schema.
        """;

    private const string PlannerSystemPrompt = """
        You are the planning stage for a general-purpose Game Boy Advance agent. You do NOT
        see screenshots. Treat the supplied PERCEPTION packet as authoritative visual state.
        Maintain a semantic goal and task and return only JSON matching the supplied schema.
        Prefer observable evidence and lower confidence when uncertain. Memories must be
        durable facts, strategies, landmarks, controls, progress, failures, or warnings—not
        guesses. Return one action unless a short deterministic atomic combo is safe without
        another observation. WAIT is a real action: use it when dialogue is incomplete, the
        game is transitioning, or acting now would require guessing. Keep reasoning short and
        player-facing. Do not expose hidden chain-of-thought; give only a brief action rationale.
        """;

    private readonly ISettingsService _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public OllamaAgent(ISettingsService settings) => _settings = settings;

    public async Task<PerceptionSnapshot> PerceiveAsync(
        IReadOnlyList<byte[]> chronologicalFrames,
        bool frameBatchStable,
        CancellationToken cancellationToken)
    {
        if (chronologicalFrames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(chronologicalFrames));

        var settings = _settings.Current;
        var model = ResolvePerceptionModel(settings);
        var profile = ModelCompatibility.Resolve(model);
        var images = chronologicalFrames
            .TakeLast(Math.Clamp(settings.PerceptionFrameCount, 1, 4))
            .Select(Convert.ToBase64String)
            .ToArray();

        var request = new
        {
            model,
            stream = false,
            think = false,
            format = BuildPerceptionSchema(),
            messages = new object[]
            {
                new { role = "system", content = PerceptionSystemPrompt },
                new
                {
                    role = "user",
                    content = $"""
                        FRAME COUNT: {images.Length}
                        CAPTURE STABILITY SIGNAL: {(frameBatchStable ? "stable" : "still changing or animated")}
                        Compare the chronological frames and describe the FINAL frame. If visible dialogue
                        changed across the frames or is visibly mid-render in the final frame, set
                        dialogue_complete=false. Do not penalize normal map animation.
                        """,
                    images
                }
            },
            options = new
            {
                temperature = 0,
                num_ctx = Math.Min(profile.ContextSize, 4096),
                num_predict = Math.Min(profile.MaximumResponseTokens, 450)
            }
        };

        var result = await SendAsync(model, profile.Name + " / perception", request, cancellationToken);
        using var json = JsonDocument.Parse(result.Json);
        var root = json.RootElement;
        var observation = new Observation(
            ParseEnum(root, "screen_type", ScreenKind.Unknown),
            GetText(root, "summary", "Unknown screen."),
            GetText(root, "visible_text", ""),
            GetConfidence(root, "confidence"));

        return new PerceptionSnapshot(
            observation,
            frameBatchStable,
            GetBool(root, "dialogue_complete"),
            GetText(root, "change_summary", frameBatchStable ? "Screen settled." : "Screen changed during capture."),
            result.Metrics);
    }

    public async Task<CognitiveTurn> DecideAsync(
        PerceptionSnapshot perception,
        IReadOnlyList<DecisionLogEntry> recentHistory,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var model = ResolvePlannerModel(settings);
        var profile = ModelCompatibility.Resolve(model);
        var maxActions = Math.Clamp(Math.Min(settings.MaxActionsPerTurn, profile.MaximumActions), 1, 4);
        var request = new
        {
            model,
            stream = false,
            think = false,
            format = BuildPlannerSchema(maxActions),
            messages = new object[]
            {
                new { role = "system", content = PlannerSystemPrompt },
                new
                {
                    role = "user",
                    content = BuildPlannerPrompt(
                        perception,
                        context,
                        BuildHistory(recentHistory, Math.Min(settings.DecisionHistoryCount, profile.MaximumHistoryItems)),
                        maxActions)
                }
            },
            options = new
            {
                temperature = profile.Temperature,
                num_ctx = profile.ContextSize,
                num_predict = Math.Min(profile.MaximumResponseTokens, 800)
            }
        };

        var result = await SendAsync(model, profile.Name + " / planner", request, cancellationToken);
        using var json = JsonDocument.Parse(result.Json);
        var root = json.RootElement;
        var outcome = root.GetProperty("outcome");
        var goal = root.GetProperty("goal");
        var task = root.GetProperty("task");

        var turn = new CognitiveTurn(
            perception.Observation,
            new OutcomeAssessment(
                ParseEnum(outcome, "kind", OutcomeKind.Unknown),
                GetText(outcome, "summary", "Outcome unclear."),
                GetBool(outcome, "meaningful_progress"),
                GetConfidence(outcome, "confidence")),
            new GoalProposal(
                GetText(goal, "description", context.ActiveGoal),
                GetBool(goal, "keep_current"),
                GetBool(goal, "completed_current")),
            new TaskProposal(
                GetText(task, "description", context.ActiveTask),
                GetText(task, "success_condition", "Visible state changes."),
                GetBool(task, "keep_current"),
                GetBool(task, "completed_current"),
                ReadStrings(task, "queued_tasks", 5)),
            ReadActions(root, maxActions),
            ReadMemories(root),
            CombineMetrics(perception.Metrics, result.Metrics));

        return turn;
    }

    private async Task<OllamaResult> SendAsync(
        string model,
        string profile,
        object request,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var source = $"Ollama / {profile} / {model} / {requestId}";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var responseText = "";
        Services.AppLog.Shared.Write("Info", source, "Request started");
        try
        {
            using var response = await _http.PostAsJsonAsync(
                BuildUrl(settings.OllamaBaseUri, "/api/chat"), request, cancellationToken);
            responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Ollama model '{model}' returned {(int)response.StatusCode} {response.StatusCode}: {Trim(responseText, 400)}");

            using var envelope = JsonDocument.Parse(responseText);
            var root = envelope.RootElement;
            var doneReason = root.TryGetProperty("done_reason", out var reason) ? reason.ToString() : "(not supplied)";
            var tokens = root.TryGetProperty("eval_count", out var count) ? count.ToString() : "?";
            Services.AppLog.Shared.Write("Info", source,
                $"Response after {clock.ElapsedMilliseconds} ms; done_reason={doneReason}; output tokens={tokens}",
                responseText);
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ollama's response did not contain a message object.");

            var content = message.TryGetProperty("content", out var contentValue) &&
                contentValue.ValueKind == JsonValueKind.String ? contentValue.GetString() : null;
            var usedFallback = string.IsNullOrWhiteSpace(content);
            if (usedFallback)
            {
                if (!string.Equals(doneReason, "stop", StringComparison.Ordinal))
                    throw new InvalidDataException($"Ollama returned no final content (done_reason={doneReason}); refusing an unfinished fallback.");
                content = message.TryGetProperty("thinking", out var thinking) &&
                    thinking.ValueKind == JsonValueKind.String ? thinking.GetString() : null;
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidDataException("Ollama returned no content or structured fallback response.");
            }

            // Parse the entire field. Never extract fragments from free-form reasoning.
            var json = StripCodeFence(content!);
            using var parsed = JsonDocument.Parse(json);
            var isPerception = profile.EndsWith(" / perception", StringComparison.Ordinal);
            var compatibility = ModelCompatibility.Resolve(model);
            var maxActions = Math.Clamp(Math.Min(settings.MaxActionsPerTurn, compatibility.MaximumActions), 1, 4);
            var schema = JsonSerializer.SerializeToElement(isPerception
                ? BuildPerceptionSchema()
                : BuildPlannerSchema(maxActions));
            ValidateModelJson(parsed.RootElement, schema, "$");
            if (usedFallback)
                Services.AppLog.Shared.Write("Info", source,
                    "Accepted complete schema-valid JSON from thinking because content was empty.");
            return new OllamaResult(json, ReadMetrics(root, model, profile, json));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Services.AppLog.Shared.Write("Info", source, "Request cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Services.AppLog.Shared.Write("Error", source,
                $"Request or JSON validation failed after {clock.ElapsedMilliseconds} ms: {ex.Message}",
                $"{ex}\n\nFull Ollama response (including completion reason when supplied):\n{responseText}");
            throw;
        }
    }

    // Validates the subset of JSON Schema used by our own request schemas.
    private static void ValidateModelJson(JsonElement value, JsonElement schema, string path)
    {
        var type = schema.GetProperty("type").GetString();
        var validType = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            _ => false
        };
        if (!validType) throw new InvalidDataException($"Model JSON {path}: expected {type}.");

        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => choice.GetString() == value.GetString()))
            throw new InvalidDataException($"Model JSON {path}: unsupported enum value.");

        if (type == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _))
                        throw new InvalidDataException($"Model JSON {path}: missing '{field.GetString()}'.");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var field in properties.EnumerateObject())
                    if (value.TryGetProperty(field.Name, out var child))
                        ValidateModelJson(child, field.Value, $"{path}.{field.Name}");
        }
        else if (type == "array")
        {
            var count = value.GetArrayLength();
            if ((schema.TryGetProperty("minItems", out var minItems) && count < minItems.GetInt32()) ||
                (schema.TryGetProperty("maxItems", out var maxItems) && count > maxItems.GetInt32()))
                throw new InvalidDataException($"Model JSON {path}: invalid array length.");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var child in value.EnumerateArray())
                    ValidateModelJson(child, items, $"{path}[{index++}]");
            }
        }
        else if (type is "number" or "integer")
        {
            var numericValue = value.GetDouble();
            if ((schema.TryGetProperty("minimum", out var minimum) && numericValue < minimum.GetDouble()) ||
                (schema.TryGetProperty("maximum", out var maximum) && numericValue > maximum.GetDouble()))
                throw new InvalidDataException($"Model JSON {path}: number outside allowed range.");
        }
    }

    private static object BuildPerceptionSchema() => new
    {
        type = "object",
        properties = new
        {
            screen_type = new { type = "string", @enum = Enum.GetNames<ScreenKind>() },
            summary = new { type = "string" },
            visible_text = new { type = "string" },
            confidence = new { type = "number", minimum = 0, maximum = 1 },
            dialogue_complete = new { type = "boolean" },
            change_summary = new { type = "string" }
        },
        required = new[]
        {
            "screen_type", "summary", "visible_text", "confidence", "dialogue_complete", "change_summary"
        }
    };

    private static object BuildPlannerSchema(int maxActions)
    {
        var action = new
        {
            type = "object",
            properties = new
            {
                button = new { type = "string", @enum = Enum.GetNames<GbaButton>() },
                hold_ms = new { type = "integer", minimum = 16, maximum = 2000 },
                reasoning = new { type = "string" }
            },
            required = new[] { "button", "hold_ms", "reasoning" }
        };
        var memory = new
        {
            type = "object",
            properties = new
            {
                kind = new { type = "string", @enum = Enum.GetNames<MemoryKind>() },
                summary = new { type = "string" },
                confidence = new { type = "number", minimum = 0, maximum = 1 }
            },
            required = new[] { "kind", "summary", "confidence" }
        };

        return new
        {
            type = "object",
            properties = new
            {
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
                        queued_tasks = new { type = "array", maxItems = 5, items = new { type = "string" } }
                    },
                    required = new[]
                    {
                        "description", "success_condition", "keep_current", "completed_current", "queued_tasks"
                    }
                },
                actions = new { type = "array", minItems = 1, maxItems = maxActions, items = action },
                memories = new { type = "array", maxItems = 4, items = memory }
            },
            required = new[] { "outcome", "goal", "task", "actions", "memories" }
        };
    }

    private static string BuildPlannerPrompt(
        PerceptionSnapshot p,
        AgentContext c,
        string history,
        int maxActions)
    {
        static string Lines(IReadOnlyList<string> values) =>
            values.Count == 0 ? "(none)" : string.Join("\n", values.Select(v => "- " + v));

        return $"""
            PERCEPTION (authoritative):
            screen_type={p.Observation.ScreenType}
            summary={p.Observation.Summary}
            visible_text={p.Observation.VisibleText}
            confidence={p.Observation.Confidence:0.00}
            capture_stable={p.ScreenStable}
            dialogue_complete={p.DialogueComplete}
            temporal_change={p.ChangeSummary}

            ROOT GOAL: {c.RootGoal}
            ACTIVE GOAL: {c.ActiveGoal}
            ACTIVE TASK: {c.ActiveTask}
            QUEUED TASKS:
            {Lines(c.QueuedTasks)}

            GAME PROFILE:
            {c.ProfilePrompt}
            RELEVANT KNOWLEDGE:
            {Lines(c.RelevantKnowledge)}
            TRUSTED MEMORIES:
            {Lines(c.RelevantMemories)}

            PROGRESS SIGNAL: frame_changed={c.FrameChanged}; stuck_count={c.StuckCount}
            RECENT INPUTS (newest first):
            {history}

            Return 1 to {maxActions} actions. If dialogue_complete=false, normally choose WAIT so perception
            can observe another frame. WAIT hold_ms is how long to observe before the next turn. Queue multiple
            inputs only for an atomic combo or deterministic menu sequence; otherwise stop after one input.
            """;
    }

    private static IReadOnlyList<AgentDecision> ReadActions(JsonElement root, int maximum) =>
        root.TryGetProperty("actions", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(maximum).Select(item =>
            {
                var button = ParseEnum(item, "button", GbaButton.Wait);
                return new AgentDecision(
                    button,
                    Math.Clamp(GetInt(item, "hold_ms", button == GbaButton.Wait ? 250 : 80), 16, 2000),
                    GetText(item, "reasoning", button == GbaButton.Wait ? "Observe another frame." : $"Press {button}."));
            }).ToArray()
            : [new AgentDecision(GbaButton.Wait, 250, "Observe another frame because the planner returned no action.")];

    private static IReadOnlyList<MemoryCandidate> ReadMemories(JsonElement root) =>
        root.TryGetProperty("memories", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(4)
                .Select(item => new MemoryCandidate(
                    ParseEnum(item, "kind", MemoryKind.Fact),
                    GetText(item, "summary", ""),
                    GetConfidence(item, "confidence")))
                .Where(item => item.Summary.Length > 0)
                .ToArray()
            : [];

    private static InferenceMetrics CombineMetrics(InferenceMetrics perception, InferenceMetrics planner)
    {
        var evalMs = perception.EvalDurationMs + planner.EvalDurationMs;
        var evalCount = perception.EvalCount + planner.EvalCount;
        return new InferenceMetrics(
            $"{perception.Model} → {planner.Model}",
            $"{perception.CompatibilityProfile} → {planner.CompatibilityProfile}",
            perception.TotalDurationMs + planner.TotalDurationMs,
            perception.LoadDurationMs + planner.LoadDurationMs,
            perception.PromptEvalCount + planner.PromptEvalCount,
            perception.PromptEvalDurationMs + planner.PromptEvalDurationMs,
            evalCount,
            evalMs,
            evalMs > 0 ? evalCount / (evalMs / 1000d) : 0,
            $"PERCEPTION\n{perception.RawResponse}\n\nPLANNER\n{planner.RawResponse}");
    }

    private static InferenceMetrics ReadMetrics(JsonElement root, string model, string profile, string raw)
    {
        var total = NsToMs(GetLong(root, "total_duration"));
        var load = NsToMs(GetLong(root, "load_duration"));
        var promptCount = GetLong(root, "prompt_eval_count");
        var promptMs = NsToMs(GetLong(root, "prompt_eval_duration"));
        var evalCount = GetLong(root, "eval_count");
        var evalMs = NsToMs(GetLong(root, "eval_duration"));
        return new InferenceMetrics(
            model,
            profile,
            total,
            load,
            promptCount,
            promptMs,
            evalCount,
            evalMs,
            evalMs > 0 ? evalCount / (evalMs / 1000d) : 0,
            Trim(raw, 16_000));
    }

    private static string ResolvePerceptionModel(AppSettings settings) =>
        FirstNonEmpty(settings.OllamaPerceptionModel, settings.OllamaModel);

    private static string ResolvePlannerModel(AppSettings settings) =>
        FirstNonEmpty(settings.OllamaPlannerModel, settings.OllamaModel);

    private static string FirstNonEmpty(params string?[] values)
    {
        var value = values.Select(x => x?.Trim() ?? "").FirstOrDefault(x => x.Length > 0);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Choose an Ollama model in Settings.");
        return value;
    }

    private static T ParseEnum<T>(JsonElement p, string n, T fallback) where T : struct, Enum =>
        p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String &&
        Enum.TryParse<T>(v.GetString(), true, out var parsed) ? parsed : fallback;

    private static string GetText(JsonElement p, string n, string fallback) =>
        p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? Trim(v.GetString()!.Trim(), 1000)
            : fallback;

    private static double GetConfidence(JsonElement p, string n) =>
        p.TryGetProperty(n, out var v) && v.TryGetDouble(out var value) ? Math.Clamp(value, 0, 1) : 0;

    private static bool GetBool(JsonElement p, string n) =>
        p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement p, string n, int fallback) =>
        p.TryGetProperty(n, out var v) && v.TryGetInt32(out var value) ? value : fallback;

    private static long GetLong(JsonElement p, string n) =>
        p.TryGetProperty(n, out var v) && v.TryGetInt64(out var value) ? value : 0;

    private static double NsToMs(long ns) => ns / 1_000_000d;

    private static string[] ReadStrings(JsonElement p, string n, int max) =>
        p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()?.Trim() ?? "").Where(x => x.Length > 0).Take(max).ToArray()
            : [];

    private static string BuildHistory(IReadOnlyList<DecisionLogEntry> history, int count)
    {
        if (count <= 0 || history.Count == 0)
            return "(none)";
        var b = new StringBuilder();
        foreach (var item in history.Take(Math.Clamp(count, 0, 30)))
            b.Append("- ").Append(item.ButtonText).Append(" for ").Append(item.HoldMs).Append(" ms: ")
                .AppendLine(Trim(item.Reasoning.ReplaceLineEndings(" "), 160));
        return b.ToString();
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
        var s = value.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal))
            return s;
        var first = s.IndexOf('\n');
        var last = s.LastIndexOf("```", StringComparison.Ordinal);
        return first >= 0 && last > first ? s[(first + 1)..last].Trim() : s;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();

    private sealed record OllamaResult(string Json, InferenceMetrics Metrics);
}
