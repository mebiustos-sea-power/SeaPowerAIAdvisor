using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// An appointment's vote is transient; the session retains the person and shared vote totals.
internal sealed class CrewAppointment
{
    internal CrewPerson Person { get; }
    internal bool? RankBeforeName { get; }
    internal CrewAppointment(CrewPerson person, string? nameWithRank)
    {
        Person = person;
        if (string.IsNullOrWhiteSpace(nameWithRank)) return;
        var form = nameWithRank!.Trim();
        var before = form.StartsWith(person.Rank, StringComparison.OrdinalIgnoreCase);
        var after = form.EndsWith(person.Rank, StringComparison.OrdinalIgnoreCase);
        RankBeforeName = before == after ? null : before;
    }
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
    internal IReadOnlyDictionary<string, CrewAppointment> Appointments { get; }
    internal IReadOnlyList<CrewSubmission> Remarks { get; }
    internal CrewGeneration(IReadOnlyDictionary<string, CrewAppointment> appointments, IEnumerable<CrewSubmission> remarks)
    { Appointments = new Dictionary<string, CrewAppointment>(appointments); Remarks = Array.AsReadOnly(remarks.ToArray()); }
}

// One immutable batch can appoint crew and independently select remarks; it has no game operations or retrieval gateway.
internal sealed class CrewBatch
{
    internal const string ToolName = "submit_chatter";
    internal const int MaximumSpeakers = 2;
    internal const int MaximumAppointments = 32;
    private readonly JObject data;
    private readonly IReadOnlyDictionary<string, CrewPerson?> people;
    private readonly HashSet<string> appointments;
    private readonly IReadOnlyDictionary<string, CrewCharacter> characters;
    private readonly IReadOnlyDictionary<string, Dictionary<string, CrewCharacter>> automaticCharacters;
    internal CrewBatch(JObject data, IReadOnlyDictionary<string, CrewPerson?> people, IEnumerable<string> appointments, bool preferExchange = false, IReadOnlyDictionary<string, CrewCharacter>? characters = null,
        IReadOnlyDictionary<string, CrewCharacter[]>? automaticCharacters = null)
    {
        this.data = (JObject)data.DeepClone(); this.data["prefer_exchange"] = preferExchange; this.people = new Dictionary<string, CrewPerson?>(people);
        this.appointments = new HashSet<string>(appointments, StringComparer.Ordinal);
        this.characters = characters == null ? new Dictionary<string, CrewCharacter>() : new Dictionary<string, CrewCharacter>(characters);
        if (this.characters.Keys.Any(id => !this.appointments.Contains(id))) throw new ArgumentException("Character supplied for an unrequested appointment.", nameof(characters));
        this.automaticCharacters = automaticCharacters == null ? new Dictionary<string, Dictionary<string, CrewCharacter>>() :
            automaticCharacters.ToDictionary(p => p.Key, p => p.Value.ToDictionary(c => c.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        if (this.automaticCharacters.Keys.Any(id => !this.appointments.Contains(id) || this.characters.ContainsKey(id)))
            throw new ArgumentException("Automatic candidates supplied for a fixed or unrequested appointment.", nameof(automaticCharacters));
        var offered = this.automaticCharacters.SelectMany(p => p.Value.Values.Select(c => (UnitId: p.Key, Character: c))).GroupBy(p => p.Character.Id)
            .OrderByDescending(g => g.First().Character.AiAppointment == "priority").ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        if (offered.Length > 0) this.data["registered_characters"] = new JArray(offered.Select(g =>
        {
            var character = g.First().Character; var row = character.Hints(); row["id"] = character.Id;
            row["priority"] = character.AiAppointment == "priority"; row["allow_duplicates"] = character.AllowDuplicates;
            row["unit_ids"] = new JArray(g.Select(p => p.UnitId));
            return row;
        }));
    }
    internal AiProviderInput Input(string connectionPrompt)
    {
        var exchange = (bool)data["prefer_exchange"]! ?
            "Create a short exchange: one speaker voices a feeling about the supplied situation; a second responds to that person with acknowledgment, empathy or a contrasting feeling, not advice, orders or new factual claims. If no natural exchange fits, use independent remarks or silence. " : "";
        var instruction = "Use reply_language for crew names, ranks, personality labels and remarks, rendering names in that language's usual script. " +
            "Appoint only the units listed in input appointments, each exactly once, with a fictional full personal name, an appropriate military rank and a one-word personality. Consider the unit's nation, type, role and appointment_context when appointing crew. " +
            "For each appointment, return character_id: new for a new fictional person, or the selected registered character ID. If fixed_character_id is supplied, return that exact ID. " +
            "If input appointments is empty, return appointments: []. " +
            "Choose which, if any, of the input speakers would naturally speak now; if empty, return remarks: []. Use their existing or newly appointed personality. " + exchange +
            "Generate only spoken dialogue, without private thoughts or narration. Use one brief in-character sentence, occasionally two: a restrained feeling, concern, or impression rather than a status summary or tactical advice. " +
            "Let personality shape the remark; feelings are fictional, but their factual basis must come from the supplied player-known situation. " +
            "Silence is preferable to filler or repetition; submit an empty remarks array when no speaker has a worthwhile remark. Appoint the requested units even when they remain silent. " +
            "Use recent_remarks to avoid repetition, not as current observations. Make this response self-contained. Shared fleet contacts do not prove personal detection. active_threat_alerts are current warnings, not hits or sensor failure; illumination alone does not establish a missile launch. " +
            "air_operations flags include handling and waiting, not completed launches or recoveries. " +
            "posture states allegiance: enemy is confirmed hostile, unknown is undetermined. class_identified describes only class/type recognition. " +
            "Missing values are unknown; ammunition counts are inventory, not launch readiness, and launched_weapons counts are current weapons, not new launches or hits. Never invent events, damage, detections, or mission completion, or claim or announce actions or orders. " +
            "Describe unit facts only for the speaker's own unit or, for leaders, its listed formation members. " +
            "Use submit_chatter once, without questions or other commentary.";
        if (automaticCharacters.Count > 0)
            instruction += " registered_characters are optional candidates: choose only characters whose unit_ids include that appointment. Prefer priority candidates when suitable; use new when no candidate fits. Unless allow_duplicates is true, assign a character at most once in this response.";
        if (characters.Count > 0 || automaticCharacters.Count > 0)
            instruction += " For a selected registered character, preserve every nonempty name, rank and personality exactly, without translation or shortening. Fill only empty fields; gender and nationality are optional generation hints. A registered personality may be longer than one word. Appointments with fixed_character_id already have a chosen character; do not change it.";
        if (!string.IsNullOrWhiteSpace(connectionPrompt))
            instruction += "\nSupplemental instructions for this connection take priority if they conflict:\n" + connectionPrompt;
        var id = new JObject { ["type"] = "string", ["description"] = "The supplied unit ID." };
        var speakerId = new JObject { ["type"] = "string", ["description"] = "A unit ID from input speakers." };
        if (people.Count > 0) speakerId["enum"] = new JArray(people.Keys);
        JObject Rows(JObject fields, int maximum, int minimum = 0) => new()
        {
            ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = maximum,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray(fields.Properties().Select(p => p.Name)), ["properties"] = fields }
        };
        var tool = new JObject { ["name"] = ToolName, ["description"] = "Submit all requested appointments and optional remarks. Either array may be empty when none are requested or no one speaks.",
            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("appointments", "remarks"),
                ["properties"] = new JObject
                {
                    ["appointments"] = Rows(new JObject
                    {
                        ["id"] = id.DeepClone(),
                        ["character_id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(new[] { "new" }.Concat(characters.Values.Select(c => c.Id))
                            .Concat(automaticCharacters.Values.SelectMany(cs => cs.Keys)).Distinct(StringComparer.Ordinal)),
                            ["description"] = "The chosen registered character ID, or new to create a fictional person. Use fixed_character_id when supplied." },
                        ["person_name"] = new JObject { ["type"] = "string", ["description"] = "The fictional person's full personal name without rank, not a unit name or callsign." },
                        ["rank"] = new JObject { ["type"] = "string", ["description"] = "The military rank only, in reply_language; abbreviations are allowed." },
                        ["name_with_rank"] = new JObject { ["type"] = "string", ["description"] = "The person's full name from person_name, with rank placed naturally in reply_language; use rank exactly." },
                        ["personality"] = new JObject { ["type"] = "string", ["description"] = "The person's one-word personality." }
                    }, appointments.Count, appointments.Count),
                    ["remarks"] = Rows(new JObject
                    {
                        ["id"] = speakerId, ["text"] = new JObject { ["type"] = "string", ["description"] = "The person's remark, in display order within this response." }
                    }, Math.Min(MaximumSpeakers, people.Count))
                } } };
        return new AiProviderInput(data, instruction, tool);
    }
    internal CrewGeneration Read(AiReply reply)
    {
        if (reply.Calls.Count != 1 || reply.Calls[0].Name != ToolName) throw new CrewException("invalid_response");
        var args = reply.Calls[0].Arguments;
        if (args.Count != 2 || args["appointments"] is not JArray appointed || appointed.Count != appointments.Count ||
            args["remarks"] is not JArray rows || rows.Count > Math.Min(MaximumSpeakers, people.Count)) throw new CrewException("invalid_response");
        string Read(JObject row, string key, int max)
        {
            if (row[key]?.Type != JTokenType.String) throw new CrewException("invalid_response");
            var value = ((string)row[key]!).Trim();
            if (value.Length == 0 || value.Length > max || value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new CrewException("invalid_response");
            return value;
        }
        var persons = new Dictionary<string, CrewAppointment>(StringComparer.Ordinal);
        // Fixed requests were accepted before this batch; reserve them even when they appear later in the response.
        var claimed = new HashSet<string>(characters.Values.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var item in appointed)
        {
            if (item is not JObject row || row.Properties().Any(p => p.Name is not ("id" or "character_id" or "person_name" or "rank" or "name_with_rank" or "personality")))
                throw new CrewException("invalid_response");
            var key = Read(row, "id", 80);
            if (!appointments.Contains(key) || persons.ContainsKey(key)) throw new CrewException("invalid_response");
            var characterId = Read(row, "character_id", 80);
            CrewCharacter? character;
            if (characters.TryGetValue(key, out character))
            {
                if (characterId != character.Id) throw new CrewException("invalid_response");
            }
            else if (characterId == "new") character = null;
            else
            {
                if (!automaticCharacters.TryGetValue(key, out var offered) || !offered.TryGetValue(characterId, out character) ||
                    !character.AllowDuplicates && claimed.Contains(characterId)) throw new CrewException("invalid_response");
                claimed.Add(characterId);
            }
            // A missing or unusable display hint is an invalid vote, not a failed appointment.
            var form = row["name_with_rank"];
            CrewPerson person;
            if (character != null)
                person = character.Person(character.Name.Length == 0 ? Read(row, "person_name", 80) : character.Name,
                    character.Rank.Length == 0 ? Read(row, "rank", 80) : character.Rank,
                    character.Personality.Length == 0 ? Read(row, "personality", 40) : character.Personality);
            else person = new CrewPerson(Read(row, "person_name", 80), Read(row, "rank", 80), Read(row, "personality", 40));
            persons.Add(key, new CrewAppointment(person, form?.Type == JTokenType.String ? (string)form! : null));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal); var remarks = new List<CrewSubmission>();
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 2) throw new CrewException("invalid_response");
            var key = Read(row, "id", 80);
            if (!people.TryGetValue(key, out var person) || !seen.Add(key)) throw new CrewException("invalid_response");
            if (person == null)
            {
                if (!persons.TryGetValue(key, out var appointment)) throw new CrewException("invalid_response");
                person = appointment.Person;
            }
            remarks.Add(new CrewSubmission(key, person!, Read(row, "text", 1200)));
        }
        return new CrewGeneration(persons, remarks);
    }
}
