using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Interactions wire format only. GeminiProvider owns conversation state; the caller owns tool execution and request budgets.
internal sealed class GeminiInteraction
{
    internal string? Id { get; }
    internal JObject[] Calls { get; }
    internal string Text { get; }
    internal long? TotalTokens { get; }

    private GeminiInteraction(string? id, JObject[] calls, string text, long? totalTokens)
    { Id = id; Calls = calls; Text = text; TotalTokens = totalTokens; }

    internal static JObject Request(AiConnectionSettings settings, JArray input, string? previous = null,
        string? system = null, IEnumerable<JObject>? tools = null, bool allowTools = false, bool store = true)
    {
        var body = new JObject
        {
            ["model"] = settings.Model, ["input"] = input.DeepClone(), ["store"] = store,
            ["generation_config"] = new JObject
            {
                ["max_output_tokens"] = settings.MaxOutputTokens,
                ["tool_choice"] = allowTools ? "auto" : "none"
            }
        };
        if (previous != null) body["previous_interaction_id"] = previous;
        if (system != null) body["system_instruction"] = system;
        var definitions = tools ?? Enumerable.Empty<JObject>();
        var functions = new JArray(definitions.Select(definition => new JObject
        {
            ["type"] = "function", ["name"] = definition["name"]!.DeepClone(),
            ["description"] = definition["description"]!.DeepClone(), ["parameters"] = definition["parameters"]!.DeepClone()
        }));
        if (functions.Count > 0) body["tools"] = functions;
        if (body["tools"] == null) ((JObject)body["generation_config"]!).Remove("tool_choice");
        return body;
    }

    internal static JArray UserInput(JObject data) => new(new JObject
    {
        ["type"] = "user_input", ["content"] = new JArray(new JObject
        { ["type"] = "text", ["text"] = data.ToString(Formatting.None) })
    });

    internal static JObject FunctionResult(JObject call, JObject result) => new()
    {
        ["type"] = "function_result", ["name"] = call["name"]!.DeepClone(), ["call_id"] = call["id"]!.DeepClone(),
        ["result"] = new JArray(new JObject { ["type"] = "text", ["text"] = result.ToString(Formatting.None) })
    };

    internal static GeminiInteraction Read(JObject response, bool requireStoredId = true)
    {
        if (response["status"]?.Type != JTokenType.String) throw new AiConnectionException("invalid_response");
        var status = (string)response["status"]!;
        if (status == "incomplete") throw new AiConnectionException("output_limit");
        if (status is "failed" or "cancelled") throw new AiConnectionException("generation_failed");
        if (status is not ("completed" or "requires_action")) throw new AiConnectionException("invalid_response");
        // Unstored connection checks may omit the ID; stored consultations still require it.
        var id = response["id"];
        if ((requireStoredId && id == null) ||
            (id != null && (id.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)id))) ||
            response["steps"] is not JArray steps || steps.Any(s => s is not JObject || s["type"]?.Type != JTokenType.String))
            throw new AiConnectionException("invalid_response");
        var calls = steps.OfType<JObject>().Where(s => (string?)s["type"] == "function_call").ToArray();
        if ((status == "requires_action") != (calls.Length > 0)) throw new AiConnectionException("invalid_response");
        foreach (var call in calls)
            if (call["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)call["id"]) ||
                call["name"]?.Type != JTokenType.String || call["arguments"] is not JObject)
                throw new AiConnectionException("invalid_function_call");
        if (calls.Select(c => (string)c["id"]!).Distinct(StringComparer.Ordinal).Count() != calls.Length)
            throw new AiConnectionException("invalid_function_call");
        var output = steps.OfType<JObject>().Where(s => (string?)s["type"] == "model_output").ToArray();
        if (output.Any(s => s["content"] is not JArray)) throw new AiConnectionException("invalid_response");
        var text = string.Concat(output.SelectMany(s => (JArray)s["content"]!).OfType<JObject>()
            .Where(p => (string?)p["type"] == "text" && p["text"]?.Type == JTokenType.String).Select(p => (string)p["text"]!));
        if (calls.Length == 0 && string.IsNullOrWhiteSpace(text)) throw new AiConnectionException("empty_response");
        var usage = response["usage"]?["total_tokens"];
        long? total = usage?.Type == JTokenType.Integer && long.TryParse(usage.ToString(), out var count) && count >= 0 ? count : (long?)null;
        return new GeminiInteraction((string?)id, calls, text, total);
    }
}
