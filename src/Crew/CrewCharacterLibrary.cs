using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Registered definitions are persistent templates. Appointments copy values and retain their source ID.
internal sealed class CrewCharacter
{
    internal string Id { get; }
    internal string Name { get; }
    internal string Rank { get; }
    internal string Personality { get; }
    internal string Gender { get; }
    internal string Nationality { get; }
    internal string? PortraitId { get; }
    internal string AiAppointment { get; }
    internal bool AllowDuplicates { get; }
    internal bool IsComplete => Name.Length > 0 && Rank.Length > 0 && Personality.Length > 0;
    internal CrewCharacter(string id, string name = "", string rank = "", string personality = "", string gender = "", string nationality = "",
        string? portraitId = null, string aiAppointment = "normal", bool allowDuplicates = false)
    {
        if (!IsId(id) || gender is not ("" or "male" or "female") || aiAppointment is not ("excluded" or "normal" or "priority") ||
            portraitId != null && !CrewPortraitLibrary.IsId(portraitId)) throw new CrewException("character_invalid");
        Id = id; Name = Read(name, 80); Rank = Read(rank, 80); Personality = Read(personality, CrewPerson.MaximumPersonalityLength, true);
        Gender = gender; Nationality = Read(nationality, 80); PortraitId = portraitId; AiAppointment = aiAppointment; AllowDuplicates = allowDuplicates;
    }
    internal static bool IsId(string id) => id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    private static string Read(string value, int maximum, bool multiline = false)
    {
        if (value == null || value.Length > maximum || value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t')))
            throw new CrewException("character_invalid");
        return value.Trim();
    }
    // Only generation hints go to AI; local IDs, portraits and eligibility belong to the MOD.
    internal JObject Hints() => new() { ["name"] = Name, ["rank"] = Rank, ["personality"] = Personality, ["gender"] = Gender, ["nationality"] = Nationality };
    internal JObject Json()
    {
        var value = Hints(); value["id"] = Id; value["portrait_id"] = PortraitId; value["ai_appointment"] = AiAppointment; value["allow_duplicates"] = AllowDuplicates;
        return value;
    }
    internal CrewPerson Person(string name, string rank, string personality) => new(Name.Length == 0 ? name : Name,
        Rank.Length == 0 ? rank : Rank, Personality.Length == 0 ? personality : Personality, PortraitId, Id);
    internal static CrewCharacter Parse(JObject value)
    {
        if (value.Count != 9 || value.Properties().Any(p => p.Name is not ("id" or "name" or "rank" or "personality" or "gender" or "nationality" or "portrait_id" or "ai_appointment" or "allow_duplicates")))
            throw new CrewException("character_invalid");
        string Text(string key) => value[key]?.Type == JTokenType.String ? (string)value[key]! : throw new CrewException("character_invalid");
        if (value["allow_duplicates"]?.Type != JTokenType.Boolean || value["portrait_id"]?.Type is not (JTokenType.Null or JTokenType.String)) throw new CrewException("character_invalid");
        return new CrewCharacter(Text("id"), Text("name"), Text("rank"), Text("personality"), Text("gender"), Text("nationality"),
            (string?)value["portrait_id"], Text("ai_appointment"), (bool)value["allow_duplicates"]!);
    }
}

internal sealed class CrewCharacterLibrary
{
    private readonly string path;
    private IReadOnlyList<CrewCharacter> characters = Array.Empty<CrewCharacter>();
    internal IReadOnlyList<CrewCharacter> Characters => characters;
    internal int Version { get; private set; }
    internal CrewCharacterLibrary(string settingsDirectory)
    {
        path = Path.Combine(settingsDirectory, "Crew", "characters.json");
        try
        {
            if (Directory.Exists(path)) throw new IOException("The character library path is not a file.");
            var bytes = ConfigFile.Backup(path); if (bytes == null) return;
            var value = JObject.Parse(ConfigFile.Decode(bytes));
            if (value.Count != 2 || value["format_version"]?.Type != JTokenType.Integer || (long)value["format_version"]! != 1 || value["characters"] is not JArray rows)
                throw new CrewException("character_library_invalid");
            var loaded = rows.Select(row => row is JObject obj ? CrewCharacter.Parse(obj) : throw new CrewException("character_library_invalid")).ToArray();
            if (loaded.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Length) throw new CrewException("character_library_invalid");
            characters = Array.AsReadOnly(loaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CrewException("character_library_read_failed", e); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or OverflowException) { throw new CrewException("character_library_invalid", e); }
    }
    internal CrewCharacter? Find(string id) => characters.FirstOrDefault(c => c.Id == id);
    internal void Save(CrewCharacter character)
    {
        var next = characters.ToList(); var index = next.FindIndex(c => c.Id == character.Id);
        if (index < 0) next.Add(character); else next[index] = character;
        Commit(next);
    }
    internal void Delete(string id)
    {
        if (Find(id) == null) throw new CrewException("character_unavailable");
        Commit(characters.Where(c => c.Id != id).ToArray());
    }
    private void Commit(IEnumerable<CrewCharacter> next)
    {
        var list = next.ToArray();
        var value = new JObject { ["format_version"] = 1, ["characters"] = new JArray(list.Select(c => c.Json())) };
        try { ConfigFile.Write(path, Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CrewException("character_library_write_failed", e); }
        characters = Array.AsReadOnly(list); Version++;
    }
}
