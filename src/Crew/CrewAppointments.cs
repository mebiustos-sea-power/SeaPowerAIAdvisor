using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Owns appointment-specific AI stages and fixed identity resolution. No partial result escapes this job.
internal sealed class CrewAppointments
{
    private const string AssessmentTool = "assess_crew_fit", SelectionTool = "select_crew", CompletionTool = "submit_crew_people";
    private readonly JObject data;
    private readonly Dictionary<string, JObject> units;
    private readonly IReadOnlyDictionary<string, CrewCharacter> fixedCharacters;
    private readonly Dictionary<string, CrewCharacter> registered;
    private readonly Dictionary<string, HashSet<string>> eligible;
    private readonly int year;
    internal CrewAppointments(JObject data, string[] ids, IReadOnlyDictionary<string, CrewCharacter>? fixedCharacters,
        IReadOnlyDictionary<string, CrewCharacter[]>? automaticCharacters)
    {
        this.data = data;
        units = ((JArray?)data["appointments"] ?? throw new ArgumentException("Appointment input is missing.")).OfType<JObject>().ToDictionary(u => (string)u["id"]!, StringComparer.Ordinal);
        if (ids.Length > CrewBatch.MaximumAppointments || units.Count != ids.Length || !units.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ids))
            throw new ArgumentException("Appointment IDs do not match the input.");
        this.fixedCharacters = fixedCharacters == null ? new Dictionary<string, CrewCharacter>() : new Dictionary<string, CrewCharacter>(fixedCharacters);
        if (this.fixedCharacters.Keys.Any(id => !units.ContainsKey(id))) throw new ArgumentException("Fixed character supplied for an unrequested unit.");
        foreach (var entry in this.fixedCharacters) { units[entry.Key]["fixed_character_id"] = entry.Value.Id; units[entry.Key]["character"] = entry.Value.Identity(); }
        var automatic = automaticCharacters ?? new Dictionary<string, CrewCharacter[]>();
        if (automatic.Keys.Any(id => !units.ContainsKey(id) || this.fixedCharacters.ContainsKey(id))) throw new ArgumentException("Automatic characters supplied for a fixed or unrequested unit.");
        var offered = automatic.SelectMany(p => p.Value.Select(c => (UnitId: p.Key, Character: c))).GroupBy(p => p.Character.Id)
            .OrderByDescending(g => g.First().Character.AiAppointment == "priority").ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        registered = offered.ToDictionary(g => g.Key, g => g.First().Character, StringComparer.Ordinal);
        eligible = offered.ToDictionary(g => g.Key, g => g.Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        if (offered.Length > 0) data["registered_characters"] = new JArray(offered.Select(g =>
        {
            var character = g.First().Character; var row = character.Identity(); row["id"] = character.Id;
            row["priority"] = character.AiAppointment == "priority"; row["allow_duplicates"] = character.AllowDuplicates;
            row["unit_ids"] = new JArray(g.Select(p => p.UnitId)); return row;
        }));
        var date = (string?)data["appointment_context"]?["captured_at_utc"];
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var captured)) throw new ArgumentException("Appointment context requires the mission date.");
        year = captured.Year;
    }
    internal async Task<CrewGeneration> GenerateAsync(string prompt, Func<AiProviderInput, Task<AiReply>> send, CancellationToken cancellation)
    {
        var scoped = (JObject)data.DeepClone();
        var fitting = eligible.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        async Task<AiReply> Request(AiProviderInput input)
        {
            cancellation.ThrowIfCancellationRequested(); var reply = await send(input).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); return reply;
        }
        if (registered.Count > 0)
        {
            var reply = await Request(Assessment(prompt)).ConfigureAwait(false);
            fitting = ReadAssessment(reply);
            foreach (JObject character in (JArray)scoped["registered_characters"]!)
                character["unit_ids"] = new JArray(fitting[(string)character["id"]!]);
        }
        var selection = await Request(Selection(scoped, prompt)).ConfigureAwait(false);
        var identities = ReadSelection(selection, fitting);
        var input = new JObject { ["reply_language"] = data["reply_language"]!.DeepClone(), ["year"] = year,
            ["people"] = new JArray(identities.Values.Select(v => v.Data)) };
        var completed = await Request(Completion(input, prompt)).ConfigureAwait(false);
        return ReadPeople(completed, identities);
    }
    private AiProviderInput Assessment(string prompt)
    {
        var fields = new JObject
        {
            ["character_id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(registered.Keys) },
            ["reason"] = CrewProtocol.Text("Briefly assess this person's own fixed nationality and other specified facts against the offered units' service, roles and mission setting. Explain the included or absent suitable placements."),
            ["unit_ids"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(units.Keys) }, ["uniqueItems"] = true,
                ["description"] = "Reasonably coherent placements for this person, chosen within their supplied unit_ids. Empty if none fits. This is an assessment, not a final assignment." }
        };
        return CrewProtocol.Input(data, AssessmentInstruction, prompt, AssessmentTool, "Assess coherent placements for each registered character before any final assignment.", "appointments",
            CrewProtocol.Rows(fields, registered.Count, registered.Count));
    }
    private Dictionary<string, HashSet<string>> ReadAssessment(AiReply reply)
    {
        var rows = CrewProtocol.Read(reply, AssessmentTool, "appointments", registered.Count, registered.Count);
        var fitting = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 3 || row["reason"]?.Type != JTokenType.String || row["unit_ids"] is not JArray ids) throw new CrewException("invalid_response");
            var id = CrewProtocol.Text(row, "character_id", 80);
            if (!eligible.TryGetValue(id, out var offered) || fitting.ContainsKey(id)) throw new CrewException("invalid_response");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in ids)
                if (unit.Type != JTokenType.String || !offered.Contains((string)unit!) || !selected.Add((string)unit!)) throw new CrewException("invalid_response");
            fitting.Add(id, selected);
        }
        return fitting;
    }
    private AiProviderInput Selection(JObject scoped, string prompt)
    {
        var input = new JObject { ["reply_language"] = scoped["reply_language"]!.DeepClone(), ["appointment_context"] = scoped["appointment_context"]!.DeepClone(), ["appointments"] = scoped["appointments"]!.DeepClone() };
        if (scoped["registered_characters"] != null) input["registered_characters"] = scoped["registered_characters"]!.DeepClone();
        var fields = new JObject
        {
            ["id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(units.Keys) },
            ["character_id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(new[] { "new" }.Concat(registered.Keys).Concat(fixedCharacters.Values.Select(c => c.Id)).Distinct(StringComparer.Ordinal)),
                ["description"] = "Use fixed_character_id when supplied; otherwise a registered ID eligible for this unit, or new." },
            ["rank"] = CrewProtocol.Text("Military rank for this person's own national service and assigned duty, written naturally in reply_language; abbreviations allowed. Preserve a nonempty registered rank exactly.")
        };
        return CrewProtocol.Input(input, SelectionInstruction, prompt, SelectionTool, "Choose a registered character or new for every supplied appointment.", "appointments", CrewProtocol.Rows(fields, units.Count, units.Count));
    }
    private sealed class Identity
    {
        internal CrewCharacter? Character { get; }
        internal JObject Data { get; }
        internal Identity(string id, JObject unit, CrewCharacter? character, string rank)
        {
            Character = character;
            Data = new JObject { ["id"] = id, ["nationality"] = string.IsNullOrWhiteSpace(character?.Nationality) ? unit["nation"]!.DeepClone() : character!.Nationality,
                ["gender"] = character?.Gender ?? "", ["name"] = character?.Name ?? "", ["rank"] = rank, ["personality"] = character?.Personality ?? "" };
        }
    }
    private Dictionary<string, Identity> ReadSelection(AiReply reply, Dictionary<string, HashSet<string>> fitting)
    {
        var rows = CrewProtocol.Read(reply, SelectionTool, "appointments", units.Count, units.Count);
        var chosen = new Dictionary<string, Identity>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(fixedCharacters.Values.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 3 || row.Properties().Any(p => p.Name is not ("id" or "character_id" or "rank"))) throw new CrewException("invalid_response");
            var id = CrewProtocol.Text(row, "id", 80); var characterId = CrewProtocol.Text(row, "character_id", 80);
            if (!units.TryGetValue(id, out var unit) || chosen.ContainsKey(id)) throw new CrewException("invalid_response");
            CrewCharacter? character;
            if (fixedCharacters.TryGetValue(id, out character))
            { if (characterId != character.Id) throw new CrewException("invalid_response"); }
            else if (characterId == "new") character = null;
            else
            {
                if (!registered.TryGetValue(characterId, out character) || !fitting[characterId].Contains(id) || !character.AllowDuplicates && !claimed.Add(characterId)) throw new CrewException("invalid_response");
            }
            var rank = !string.IsNullOrEmpty(character?.Rank) ? character!.Rank : CrewProtocol.Text(row, "rank", 80);
            chosen.Add(id, new Identity(id, unit, character, rank));
        }
        // The completion input follows the original requested order, independently of AI's submission order.
        return units.Keys.ToDictionary(id => id, id => chosen[id], StringComparer.Ordinal);
    }
    private static AiProviderInput Completion(JObject input, string prompt)
    {
        var fields = new JObject
        {
            ["id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(input["people"]!.Select(v => v["id"]!.DeepClone())) },
            ["person_name"] = CrewProtocol.Text("The person's full name appropriate to their specified nationality and gender, in reply_language. Preserve a nonempty supplied name exactly. No rank or callsign."),
            ["name_with_rank"] = CrewProtocol.Text("Combine person_name and rank naturally in reply_language; use rank exactly."),
            ["personality"] = CrewProtocol.Text("Preserve a nonempty supplied personality exactly. Otherwise choose a one-word personality in reply_language.")
        };
        var count = ((JArray)input["people"]!).Count;
        return CrewProtocol.Input(input, CompletionInstruction, prompt, CompletionTool, "Submit the completed identities of all supplied people.", "appointments", CrewProtocol.Rows(fields, count, count));
    }
    private static CrewGeneration ReadPeople(AiReply reply, Dictionary<string, Identity> identities)
    {
        var rows = CrewProtocol.Read(reply, CompletionTool, "appointments", identities.Count, identities.Count);
        var result = new Dictionary<string, CrewAppointment>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            if (item is not JObject row) throw new CrewException("invalid_response");
            var id = CrewProtocol.Text(row, "id", 80);
            if (!identities.TryGetValue(id, out var identity) || result.ContainsKey(id)) throw new CrewException("invalid_response");
            var character = identity.Character;
            var name = !string.IsNullOrEmpty(character?.Name) ? character!.Name : CrewProtocol.Text(row, "person_name", 80);
            var rank = (string)identity.Data["rank"]!;
            var personality = !string.IsNullOrEmpty(character?.Personality) ? character!.Personality : CrewProtocol.Text(row, "personality", 40);
            var person = character == null ? new CrewPerson(name, rank, personality) : character.Person(name, rank, personality);
            var form = row["name_with_rank"];
            result.Add(id, new CrewAppointment(person, form?.Type == JTokenType.String ? (string)form! : null));
        }
        return new CrewGeneration(result, Array.Empty<CrewSubmission>());
    }
    private const string AssessmentInstruction = "Assess each registered character's suitability for the actual captain or pilot duties on the offered units in appointment_context. Their specified nationality, gender and other filled identity fields are fixed facts. The supplied unit_ids express platform eligibility only; they do not establish that the person's nationality and service fit that appointment. Judge the consistency of the person's identity, the unit's nation and actual role, and the mission setting. Do not invent a special transfer or exchange merely to use a registered character. Include only reasonably coherent placements; an empty list is valid. Treat an unspecified identity field as unspecified. Return every registered character exactly once with a brief reason and suitable unit_ids. Do not choose a final assignment, generate names or ranks, or alter personal facts. Use assess_crew_fit once without other commentary.";
    private const string SelectionInstruction = "Select fictional captains and pilots for the input appointments, each exactly once. Choose a suitable registered character or new. Registered nationality and gender are fixed personal facts. Before selecting a character, assess the consistency of that identity with the unit's service, actual role and mission setting. Prioritize coherent appointments over using every registered character; use new when a registered person's placement would need an implausible explanation. Choose only characters whose unit_ids include the unit. Prefer suitable priority candidates. Unless allow_duplicates is true, use a character at most once. Keep fixed_character_id exactly when provided. Use new when no registered character is suitable. Choose each selected person's rank using their own nationality, the unit's actual role and class, and the mission date, and write the rank title in reply_language. For unspecified nationality or a new person, use the unit's nation. The role captain means commanding duty, not a fixed military grade. Preserve any nonempty registered rank exactly. Do not generate names, personalities or backgrounds. Use select_crew once without other commentary.";
    private const string CompletionInstruction = "Complete the fictional people's missing names and personalities, each exactly once. Assignment and rank are already decided; do not choose or change them. Nationality and any specified gender are fixed personal identity. Generate missing full names consistent with that identity, in reply_language and its usual script. Preserve nonempty name and personality exactly, without translation or shortening. Otherwise generate a full personal name and a one-word personality. Use the supplied rank exactly when writing name_with_rank, placed naturally in reply_language; do not return a separate rank field. Generate no backgrounds or remarks. Use submit_crew_people once without other commentary.";
}
