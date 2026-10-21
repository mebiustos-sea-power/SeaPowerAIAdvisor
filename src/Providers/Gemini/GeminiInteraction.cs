using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Interactions wire format only. GeminiProvider owns conversation state; AdvisorEngine owns retrieval policy.
internal sealed class GeminiInteraction
{
    internal string? Id { get; }
    internal JObject[] Calls { get; }
    internal string Text { get; }
    internal long? TotalTokens { get; }

    private GeminiInteraction(string? id, JObject[] calls, string text, long? totalTokens)
    { Id = id; Calls = calls; Text = text; TotalTokens = totalTokens; }

    internal static JObject Request(AdvisorSettings settings, JArray input, string? previous = null,
        string? system = null, JObject? tool = null, bool allowTools = false, bool store = true, JObject? operationTool = null)
    {
        var body = new JObject
        {
            ["model"] = settings.RequestModel, ["input"] = input.DeepClone(), ["store"] = store,
            ["generation_config"] = new JObject
            {
                ["max_output_tokens"] = settings.MaxOutputTokens,
                ["tool_choice"] = allowTools ? "auto" : "none"
            }
        };
        if (previous != null) body["previous_interaction_id"] = previous;
        if (system != null) body["system_instruction"] = system;
        if (tool != null) body["tools"] = new JArray(new JObject
        {
            ["type"] = "function", ["name"] = tool["name"]!.DeepClone(),
            ["description"] = tool["description"]!.DeepClone(), ["parameters"] = tool["parameters"]!.DeepClone()
        });
        if (operationTool != null)
        {
            var tools = body["tools"] as JArray ?? new JArray();
            tools.Add(new JObject { ["type"] = "function", ["name"] = operationTool["name"]!.DeepClone(),
                ["description"] = operationTool["description"]!.DeepClone(), ["parameters"] = operationTool["parameters"]!.DeepClone() });
            body["tools"] = tools;
        }
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
        if (response["status"]?.Type != JTokenType.String) throw new AdvisorConnectionException("invalid_response");
        var status = (string)response["status"]!;
        if (status == "incomplete") throw new AdvisorConnectionException("output_limit");
        if (status is "failed" or "cancelled") throw new AdvisorConnectionException("generation_failed");
        if (status is not ("completed" or "requires_action")) throw new AdvisorConnectionException("invalid_response");
        // Unstored connection checks may omit the ID; stored consultations still require it.
        var id = response["id"];
        if ((requireStoredId && id == null) ||
            (id != null && (id.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)id))) ||
            response["steps"] is not JArray steps || steps.Any(s => s is not JObject || s["type"]?.Type != JTokenType.String))
            throw new AdvisorConnectionException("invalid_response");
        var calls = steps.OfType<JObject>().Where(s => (string?)s["type"] == "function_call").ToArray();
        if ((status == "requires_action") != (calls.Length > 0)) throw new AdvisorConnectionException("invalid_response");
        foreach (var call in calls)
            if (call["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)call["id"]) ||
                call["name"]?.Type != JTokenType.String || call["arguments"] is not JObject)
                throw new AdvisorConnectionException("invalid_function_call");
        if (calls.Select(c => (string)c["id"]!).Distinct(StringComparer.Ordinal).Count() != calls.Length)
            throw new AdvisorConnectionException("invalid_function_call");
        var output = steps.OfType<JObject>().Where(s => (string?)s["type"] == "model_output").ToArray();
        if (output.Any(s => s["content"] is not JArray)) throw new AdvisorConnectionException("invalid_response");
        var text = string.Concat(output.SelectMany(s => (JArray)s["content"]!).OfType<JObject>()
            .Where(p => (string?)p["type"] == "text" && p["text"]?.Type == JTokenType.String).Select(p => (string)p["text"]!));
        if (calls.Length == 0 && string.IsNullOrWhiteSpace(text)) throw new AdvisorConnectionException("empty_response");
        var usage = response["usage"]?["total_tokens"];
        long? total = usage?.Type == JTokenType.Integer && long.TryParse(usage.ToString(), out var count) && count >= 0 ? count : (long?)null;
        return new GeminiInteraction((string?)id, calls, text, total);
    }
}
