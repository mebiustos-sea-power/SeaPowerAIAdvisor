using System.Collections.Generic;
using System.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Draft references belong to the editor; submitted questions retain independent text and badge snapshots.
internal sealed class AdvisorDraft
{
    private readonly List<(int Id, string Name)> units = new();
    private readonly List<AdvisorLocation> locations = new();
    internal IReadOnlyList<(int Id, string Name)> Units => units;
    internal IReadOnlyList<AdvisorLocation> Locations => locations;
    internal AdvisorLocation AddLocation(AdvisorMapCoordinate coordinate, string labelPattern)
    {
        var number = locations.Count == 0 ? 0 : locations[locations.Count - 1].Index + 1;
        var index = number;
        var suffix = "";
        do { suffix = (char)('A' + index % 26) + suffix; index = index / 26 - 1; } while (index >= 0);
        var location = new AdvisorLocation(number, string.Format(System.Globalization.CultureInfo.InvariantCulture, labelPattern, suffix), coordinate);
        locations.Add(location);
        return location;
    }
    internal void RemoveLocation(AdvisorLocation location) => locations.Remove(location);
    internal bool Add(int id, string name)
    {
        if (units.Any(unit => unit.Id == id)) return false;
        units.Add((id, name)); return true;
    }
    internal void Remove(int id) => units.RemoveAll(unit => unit.Id == id);
    internal void Clear() { units.Clear(); locations.Clear(); }
    internal AdvisorQuestion Prepare(string text)
    {
        if (units.Count == 0 && locations.Count == 0) return new AdvisorQuestion(text);
        var references = string.Join("\n", units.Select(unit => AdvisorUnitMentions.Format(unit.Id, unit.Name))
            .Concat(locations.Select(location => location.Reference)));
        var names = string.Join(" / ", units.Select(unit => unit.Name).Concat(locations.Select(location => location.Label)));
        return new AdvisorQuestion(text.Length == 0 ? references : text + "\n\n" + references,
            text.Length == 0 ? names : text + "\n\n" + names, false, draftText: text, units: units, locations: locations);
    }
}
