using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

internal static class AdvisorUnitSearch
{
    internal static IReadOnlyList<(int Id, string Name)> Find(IEnumerable<(int Id, string Name)> units, string query, IEnumerable<int> excludedIds)
    {
        var excluded = new HashSet<int>(excludedIds);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return units.Where(u => !excluded.Contains(u.Id) && terms.All(term => u.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(u => u.Id).ToArray();
    }
}
