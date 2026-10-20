using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// A consultation-local lookup of supplied IDs to player-map IDs; never sent to a provider.
internal sealed class AdvisorUnitReferences
{
    internal static readonly AdvisorUnitReferences Empty = new(Array.Empty<(string, int)>());
    private readonly Dictionary<string, int> units;
    internal AdvisorUnitReferences(IEnumerable<(string Id, int UnitId)> units) =>
        this.units = units.ToDictionary(unit => unit.Id, unit => unit.UnitId, StringComparer.Ordinal);
    internal int? Resolve(string id) => units.TryGetValue(id, out var unit) ? unit : null;
    internal string? IdFor(int unitId) => units.FirstOrDefault(unit => unit.Value == unitId).Key;
    internal AdvisorUnitReferences Only(IEnumerable<string> ids) =>
        new(ids.Where(units.ContainsKey).Select(id => (id, units[id])));
}

internal interface IAdvisorUnitReferenceSource
{
    AdvisorUnitReferences UnitReferences { get; }
}
