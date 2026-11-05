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
    internal string? PortraitFile { get; }
    internal CrewPerson(string name, string rank, string personality, string? portraitFile = null)
    {
        Name = Read(name, 80, false, false); Rank = Read(rank, 80, true, false);
        Personality = Read(personality, MaximumPersonalityLength, true, true); PortraitFile = portraitFile;
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
    internal string? PortraitFile { get; private set; }
    internal byte[]? SelectedPortrait { get; private set; }
    internal CrewPersonDraft(CrewPerson original)
    { Original = original; Name = original.Name; Rank = original.Rank; Personality = original.Personality; PortraitFile = original.PortraitFile; }
    internal bool HasChanges => Name != Original.Name || Rank != Original.Rank || Personality != Original.Personality ||
        PortraitFile != Original.PortraitFile || SelectedPortrait != null;
    internal void SelectPortrait(byte[] bytes) => SelectedPortrait = (byte[])bytes.Clone();
    internal void UseCommonPortrait() { SelectedPortrait = null; PortraitFile = null; }
    internal CrewPerson Build(string? importedPortrait = null) => new(Name, Rank, Personality, importedPortrait ?? PortraitFile);
}
