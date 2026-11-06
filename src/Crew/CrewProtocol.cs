using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Crew's structured-generation boundary; no game state or connection selection belongs here.
internal static class CrewProtocol
{
    internal static JObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    internal static JObject Rows(JObject fields, int maximum, int minimum = 0) => new()
    {
        ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = maximum,
        ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray(fields.Properties().Select(p => p.Name)), ["properties"] = fields }
    };
    internal static AiProviderInput Input(JObject data, string instruction, string prompt, string name, string description, string field, JObject rows)
    {
        if (!string.IsNullOrWhiteSpace(prompt)) instruction += "\nSupplemental instructions for this connection take priority if they conflict:\n" + prompt;
        var tool = new JObject { ["name"] = name, ["description"] = description,
            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray(field),
                ["properties"] = new JObject { [field] = rows } } };
        return new AiProviderInput(data, instruction, tool);
    }
    internal static JArray Read(AiReply reply, string tool, string field, int maximum, int minimum = 0)
    {
        if (reply.Calls.Count != 1 || reply.Calls[0].Name != tool) throw new CrewException("invalid_response");
        var args = reply.Calls[0].Arguments;
        if (args.Count != 1 || args[field] is not JArray rows || rows.Count < minimum || rows.Count > maximum) throw new CrewException("invalid_response");
        return rows;
    }
    internal static string Text(JObject row, string key, int maximum)
    {
        if (row[key]?.Type != JTokenType.String) throw new CrewException("invalid_response");
        var value = ((string)row[key]!).Trim();
        if (value.Length == 0 || value.Length > maximum || value.Any(char.IsControl)) throw new CrewException("invalid_response");
        return value;
    }
}
