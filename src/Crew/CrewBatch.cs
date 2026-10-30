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
internal sealed class CrewGeneration
{
    internal IReadOnlyDictionary<string, CrewPerson> Appointments { get; }
    internal IReadOnlyList<CrewSubmission> Reports { get; }
    internal CrewGeneration(IReadOnlyDictionary<string, CrewPerson> appointments, IEnumerable<CrewSubmission> reports)
    { Appointments = new Dictionary<string, CrewPerson>(appointments); Reports = Array.AsReadOnly(reports.ToArray()); }
}

// One immutable batch can appoint crew and independently select remarks; it has no game operations or retrieval gateway.
internal sealed class CrewBatch
{
    internal const string ToolName = "submit_reports";
    internal const int MaximumSpeakers = 2;
    internal const int MaximumAppointments = 32;
    private readonly JObject data;
    private readonly IReadOnlyDictionary<string, CrewPerson?> people;
    private readonly HashSet<string> appointments;
    internal CrewBatch(JObject data, IReadOnlyDictionary<string, CrewPerson?> people, IEnumerable<string> appointments, bool preferExchange = false)
    {
        this.data = (JObject)data.DeepClone(); this.data["prefer_exchange"] = preferExchange; this.people = new Dictionary<string, CrewPerson?>(people);
        this.appointments = new HashSet<string>(appointments, StringComparer.Ordinal);
    }
    internal AiProviderInput Input(string connectionPrompt)
    {
        var exchange = (bool)data["prefer_exchange"]! ?
            "Create a short exchange: one reporter voices a feeling about the supplied situation; a second responds to that person with acknowledgment, empathy or a contrasting feeling, not advice, orders or new factual claims. If no natural exchange fits, use independent remarks or silence. " : "";
        var instruction = "Appoint only the units listed in input appointments, each exactly once, with a fictional personal name appropriate to its nation and a one-word personality. " +
            "If input appointments is empty, return appointments: []. " +
            "Choose which, if any, of the reporters would naturally speak now, in reply_language. Use their existing or newly appointed personality. " + exchange +
            "Use one brief in-character sentence, occasionally two: a restrained feeling, concern, or impression rather than a status summary or tactical advice. " +
            "Let personality shape the remark; thoughts and feelings are fictional, but their factual basis must come from the supplied player-known situation. " +
            "Silence is preferable to filler or repetition; submit an empty reports array when no reporter has a worthwhile remark. Appoint the requested units even when they remain silent. " +
            "Use recent_remarks to avoid repetition, not as current observations. Make this response self-contained. Shared fleet contacts do not prove personal detection. " +
            "posture states allegiance: enemy is confirmed hostile, unknown is undetermined. class_identified describes only class/type recognition. " +
            "Missing values are unknown; ammunition counts are inventory, not launch readiness. Never invent events, damage, detections, or mission completion, or claim or announce actions or orders. " +
            "Report unit facts only for the speaker's own unit or, for leaders, its listed formation members; reacting to another speaker's remark is allowed. " +
            "Use submit_reports once, without questions or other commentary.";
        if (!string.IsNullOrWhiteSpace(connectionPrompt))
            instruction += "\nSupplemental instructions for this connection take priority if they conflict:\n" + connectionPrompt;
        var id = new JObject { ["type"] = "string", ["description"] = "The supplied unit ID." };
        JObject Rows(JObject fields, int maximum, int minimum = 0) => new()
        {
            ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = maximum,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray(fields.Properties().Select(p => p.Name)), ["properties"] = fields }
        };
        var tool = new JObject { ["name"] = ToolName, ["description"] = "Submit all requested appointments and optional remarks. Either array may be empty when none are requested or no one speaks.",
            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("appointments", "reports"),
                ["properties"] = new JObject
                {
                    ["appointments"] = Rows(new JObject
                    {
                        ["id"] = id.DeepClone(),
                        ["person_name"] = new JObject { ["type"] = "string", ["description"] = "The fictional person's personal name, not a unit name or callsign." },
                        ["personality"] = new JObject { ["type"] = "string", ["description"] = "The person's one-word personality." }
                    }, appointments.Count, appointments.Count),
                    ["reports"] = Rows(new JObject
                    {
                        ["id"] = id.DeepClone(), ["text"] = new JObject { ["type"] = "string", ["description"] = "The person's remark, in display order within this response." }
                    }, Math.Min(MaximumSpeakers, people.Count))
                } } };
        return new AiProviderInput(data, instruction, tool);
    }
    internal CrewGeneration Read(AiReply reply)
    {
        if (reply.Calls.Count != 1 || reply.Calls[0].Name != ToolName) throw new CrewException("invalid_response");
        var args = reply.Calls[0].Arguments;
        if (args.Count != 2 || args["appointments"] is not JArray appointed || appointed.Count != appointments.Count ||
            args["reports"] is not JArray rows || rows.Count > Math.Min(MaximumSpeakers, people.Count)) throw new CrewException("invalid_response");
        string Read(JObject row, string key, int max)
        {
            if (row[key]?.Type != JTokenType.String) throw new CrewException("invalid_response");
            var value = ((string)row[key]!).Trim();
            if (value.Length == 0 || value.Length > max || value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new CrewException("invalid_response");
            return value;
        }
        var persons = new Dictionary<string, CrewPerson>(StringComparer.Ordinal);
        foreach (var item in appointed)
        {
            if (item is not JObject row || row.Count != 3) throw new CrewException("invalid_response");
            var key = Read(row, "id", 80);
            if (!appointments.Contains(key) || persons.ContainsKey(key)) throw new CrewException("invalid_response");
            persons.Add(key, new CrewPerson(Read(row, "person_name", 80), Read(row, "personality", 40)));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal); var reports = new List<CrewSubmission>();
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 2) throw new CrewException("invalid_response");
            var key = Read(row, "id", 80);
            if (!people.TryGetValue(key, out var person) || !seen.Add(key)) throw new CrewException("invalid_response");
            if (person == null && !persons.TryGetValue(key, out person)) throw new CrewException("invalid_response");
            reports.Add(new CrewSubmission(key, person!, Read(row, "text", 1200)));
        }
        return new CrewGeneration(persons, reports);
    }
}
