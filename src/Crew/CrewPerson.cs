using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Crew;

// Immutable values let history retain the person who actually spoke.
internal sealed class CrewPerson
{
    internal const int MaximumPersonalityLength = 255;
    internal string Name { get; }
    internal string Rank { get; }
    internal string Personality { get; }
    internal string? PortraitId { get; }
    internal string? RegisteredCharacterId { get; }
    internal CrewPerson(string name, string rank, string personality, string? portraitId = null, string? registeredCharacterId = null)
    {
        Name = Read(name, 80, false, false); Rank = Read(rank, 80, true, false);
        Personality = Read(personality, MaximumPersonalityLength, true, true);
        if (portraitId != null && !CrewPortraitLibrary.IsId(portraitId)) throw new CrewPortraitLibraryException("portrait_id_invalid");
        if (registeredCharacterId != null && !CrewCharacter.IsId(registeredCharacterId)) throw new CrewException("character_invalid");
        PortraitId = portraitId; RegisteredCharacterId = registeredCharacterId;
    }
    private static string Read(string value, int maximum, bool allowEmpty, bool multiline)
    {
        if (value == null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t')))
            throw new CrewException("person_invalid");
        return value.Trim();
    }
    internal string DisplayName(bool showRanks, bool rankBeforeName) => !showRanks || Rank.Length == 0 ? Name : rankBeforeName ? Rank + " " + Name : Name + " " + Rank;
    // Portraits are local presentation data, not AI input.
    internal JObject Json() => new() { ["name"] = Name, ["rank"] = Rank, ["personality"] = Personality };
}

internal sealed class CrewPersonDraft
{
    internal CrewPerson Original { get; }
    internal string Name { get; set; }
    internal string Rank { get; set; }
    internal string Personality { get; set; }
    internal string? PortraitId { get; private set; }
    internal CrewPersonDraft(CrewPerson original)
    { Original = original; Name = original.Name; Rank = original.Rank; Personality = original.Personality; PortraitId = original.PortraitId; }
    internal bool HasChanges => Name != Original.Name || Rank != Original.Rank || Personality != Original.Personality ||
        PortraitId != Original.PortraitId;
    internal void SelectPortrait(string? id) => PortraitId = id;
    internal CrewPerson Build() => new(Name, Rank, Personality, PortraitId, Original.RegisteredCharacterId);
}
