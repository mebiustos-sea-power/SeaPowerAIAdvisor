using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Selected targets belong to the draft; submitted questions retain independent text and badge snapshots.
internal sealed class AdvisorUnitDraft
{
    private readonly List<(int Id, string Name)> units = new();
    internal IReadOnlyList<(int Id, string Name)> Units => units;
    internal bool Add(int id, string name)
    {
        if (units.Any(unit => unit.Id == id)) return false;
        units.Add((id, name)); return true;
    }
    internal void Remove(int id) => units.RemoveAll(unit => unit.Id == id);
    internal void Clear() => units.Clear();
    internal AdvisorQuestion Prepare(string text)
    {
        if (units.Count == 0) return new AdvisorQuestion(text);
        var references = string.Join("\n", units.Select(unit => AdvisorUnitMentions.Format(unit.Id, unit.Name)));
        var names = string.Join(" / ", units.Select(unit => unit.Name));
        return new AdvisorQuestion(text.Length == 0 ? references : text + "\n\n" + references,
            text.Length == 0 ? names : text + "\n\n" + names, false, draftText: text, units: units);
    }
}
