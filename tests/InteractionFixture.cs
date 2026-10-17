using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Compact mock descriptions become documented Interactions wire steps; no live requests are made here.
internal static class InteractionFixture
{
    internal static JObject Initial(JObject body) => JObject.Parse((string)body["input"]![0]!["content"]![0]!["text"]!);
    internal static JObject Result(JObject body, int index = 0) => JObject.Parse((string)body["input"]![index]!["result"]![0]!["text"]!);
    internal static JObject Reply(params JObject[] parts)
    {
        var steps = new JArray();
        foreach (var part in parts)
        {
            if (part["type"] != null) { steps.Add(part.DeepClone()); continue; }
            if (part["thoughtSignature"] != null)
                steps.Add(new JObject { ["type"] = "thought", ["signature"] = part["thoughtSignature"]!.DeepClone() });
            if (part["functionCall"] is JObject call)
                steps.Add(new JObject
                {
                    ["type"] = "function_call", ["id"] = call["id"]?.DeepClone() ?? new JValue("call_" + Guid.NewGuid().ToString("N")),
                    ["name"] = call["name"]!.DeepClone(), ["arguments"] = call["args"]!.DeepClone()
                });
            else if ((bool?)part["thought"] == true)
                steps.Add(new JObject { ["type"] = "thought", ["signature"] = "not-a-user-answer" });
            else steps.Add(new JObject { ["type"] = "model_output", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = part["text"]!.DeepClone() }) });
        }
        return new JObject
        {
            ["id"] = "interaction_" + Guid.NewGuid().ToString("N"),
            ["status"] = steps.OfType<JObject>().Any(s => (string?)s["type"] == "function_call") ? "requires_action" : "completed",
            ["steps"] = steps, ["usage"] = new JObject { ["total_tokens"] = 10 }
        };
    }
}
