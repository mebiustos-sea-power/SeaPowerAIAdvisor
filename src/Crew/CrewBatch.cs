using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

internal sealed class CrewPerson
{
    internal string Name { get; }
    internal string Personality { get; }
    internal CrewPerson(string name, string personality) { Name = name; Personality = personality; }
}
internal sealed class CrewSubmission
{
    internal string Id { get; }
    internal CrewPerson Person { get; }
    internal string Text { get; }
    internal CrewSubmission(string id, CrewPerson person, string text) { Id = id; Person = person; Text = text; }
}

// One immutable batch is one generation. The submission tool has no game operations or retrieval gateway.
internal sealed class CrewBatch
{
    internal const string ToolName = "submit_reports";
    private readonly JObject data;
    private readonly IReadOnlyDictionary<string, CrewPerson?> people;
    internal CrewBatch(JObject data, IReadOnlyDictionary<string, CrewPerson?> people)
    { this.data = (JObject)data.DeepClone(); this.people = new Dictionary<string, CrewPerson?>(people); }
    internal AiProviderInput Input(string connectionPrompt)
    {
        var instruction = "Write one or two brief in-character sentences per reporter in reply_language. " +
            "A reporter is a fictional person, not the unit itself. Formation leaders speak for every listed member; other reporters speak for their own unit. " +
            "Use only supplied player-known facts. Shared fleet contacts do not prove this reporter detected them. " +
            "posture states allegiance: enemy is confirmed hostile, unknown is undetermined. class_identified describes only class/type recognition. " +
            "Missing values are unknown; ammunition counts are inventory, not launch readiness. " +
            "Personality changes tone, never facts. Do not invent events, damage, detections, orders, actions, or mission completion. " +
            "For an unappointed reporter, create a fictional personal name appropriate to its nation and a one-word personality. " +
            "Keep an appointed person's name and personality. Focus on relevant status or concerns; omit routine coordinates and exhaustive contact lists. " +
            "Submit each supplied reporter ID once with submit_reports, without questions or other commentary.";
        if (!string.IsNullOrWhiteSpace(connectionPrompt))
            instruction += "\nSupplemental instructions for this connection take priority if they conflict:\n" + connectionPrompt;
        var properties = new JObject
        {
            ["id"] = new JObject { ["type"] = "string", ["description"] = "The supplied reporter ID." },
            ["person_name"] = new JObject { ["type"] = "string", ["description"] = "The fictional person's personal name, not a unit name or callsign." },
            ["personality"] = new JObject { ["type"] = "string", ["description"] = "The person's one-word personality." },
            ["text"] = new JObject { ["type"] = "string", ["description"] = "The person's current status report." }
        };
        var tool = new JObject { ["name"] = ToolName, ["description"] = "Submit all crew reports, matching the supplied reporter IDs.",
            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("reports"),
                ["properties"] = new JObject { ["reports"] = new JObject { ["type"] = "array", ["items"] = new JObject {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("id", "person_name", "personality", "text"), ["properties"] = properties } } } } };
        return new AiProviderInput(data, instruction, tool);
    }
    internal IReadOnlyList<CrewSubmission> Read(AiReply reply)
    {
        if (reply.Calls.Count != 1 || reply.Calls[0].Name != ToolName) throw new CrewException("invalid_response");
        var args = reply.Calls[0].Arguments;
        if (args.Count != 1 || args["reports"] is not JArray rows || rows.Count != people.Count) throw new CrewException("invalid_response");
        var seen = new HashSet<string>(StringComparer.Ordinal); var result = new List<CrewSubmission>();
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 4) throw new CrewException("invalid_response");
            string Read(string key, int max)
            {
                if (row[key]?.Type != JTokenType.String) throw new CrewException("invalid_response");
                var value = ((string)row[key]!).Trim();
                if (value.Length == 0 || value.Length > max || value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                    throw new CrewException("invalid_response");
                return value;
            }
            var id = Read("id", 80);
            if (!people.TryGetValue(id, out var person) || !seen.Add(id)) throw new CrewException("invalid_response");
            var name = Read("person_name", 80); var personality = Read("personality", 40); var text = Read("text", 1200);
            // Model output can never rename an already appointed person.
            result.Add(new CrewSubmission(id, person ?? new CrewPerson(name, personality), text));
        }
        return result;
    }
}
