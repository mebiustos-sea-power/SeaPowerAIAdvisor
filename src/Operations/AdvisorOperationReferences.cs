using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Immutable, permission-filtered identities captured for one consultation, without live game objects.
internal sealed class AdvisorOperationReferences
{
    internal static readonly AdvisorOperationReferences Empty = new(AdvisorUnitReferences.Empty);
    internal AdvisorUnitReferences Units { get; }
    internal AdvisorWeaponReferences Weapons { get; }
    internal AdvisorOperationReferences(AdvisorUnitReferences units, AdvisorWeaponReferences? weapons = null)
    { Units = units; Weapons = weapons ?? AdvisorWeaponReferences.Empty; }
}

internal interface IAdvisorOperationReferenceSource
{
    AdvisorOperationReferences OperationReferences { get; }
}

// Commands supply their eligible identities. Matching never substitutes a merely available alternative.
internal sealed class AdvisorWeaponReferences
{
    internal static readonly AdvisorWeaponReferences Empty = new(Array.Empty<(string, string, string)>());
    private readonly Dictionary<string, string> keys;
    private readonly Dictionary<string, string[]> names;
    internal AdvisorWeaponReferences(IEnumerable<(string ReferenceId, string Key, string Name)> weapons)
    {
        var rows = weapons.ToArray();
        keys = rows.GroupBy(w => w.ReferenceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Key).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        names = rows.GroupBy(w => w.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Key).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
    internal string[] Match(string requested, IEnumerable<(string Id, string Name)> candidates, out string reason)
    {
        var rows = candidates.ToArray();
        var allowed = rows.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        reason = "canonical_identifier";
        if (allowed.Contains(requested)) return new[] { requested };
        if (keys.TryGetValue(requested, out var key))
        {
            reason = "consultation_weapon_reference";
            return allowed.Contains(key) ? new[] { key } : Array.Empty<string>();
        }
        // A known key or unknown reference alias cannot be reinterpreted as a display name.
        if (keys.Values.Contains(requested, StringComparer.Ordinal) || requested.StartsWith("weapon_reference_", StringComparison.Ordinal))
            return Array.Empty<string>();
        reason = "ammunition_display_name";
        return rows.Where(c => c.Name == requested).Select(c => c.Id)
            .Concat(names.TryGetValue(requested, out var named) ? named.Where(allowed.Contains) : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
