using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor.Crew;

// Only explicit edits are staged, so background updates cannot restore a target that stopped reporting.
internal sealed class CrewTargetSelection
{
    private readonly Dictionary<string, bool> current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> edits = new(StringComparer.Ordinal);
    internal bool Dirty => edits.Count > 0;
    internal IReadOnlyCollection<string> Selected => current.Keys.Where(Checked).ToArray();
    internal void Sync(IReadOnlyList<CrewTarget> targets, Func<string, bool> enabled)
    {
        current.Clear(); foreach (var target in targets) current.Add(target.Id, enabled(target.Id));
        foreach (var id in edits.Keys.ToArray())
            if (!current.TryGetValue(id, out var value) || value == edits[id]) edits.Remove(id);
    }
    internal bool Checked(string id) => edits.TryGetValue(id, out var value) ? value : current.TryGetValue(id, out value) && value;
    internal void Set(string id, bool value)
    {
        if (!current.TryGetValue(id, out var existing)) return;
        if (value == existing) edits.Remove(id); else edits[id] = value;
    }
    internal void SetAll(bool value) { foreach (var id in current.Keys) Set(id, value); }
    internal void Discard() => edits.Clear();
}
