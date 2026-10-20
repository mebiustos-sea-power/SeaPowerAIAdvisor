using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Draft references use map identities; bind them only against the permitted consultation snapshot.
internal static class AdvisorUnitMentions
{
    private static readonly Regex reference = new(@"\(unit_id: (-?[0-9]+)\)", RegexOptions.CultureInvariant);

    internal static string Format(int id, string name) => name + " (unit_id: " + id.ToString(CultureInfo.InvariantCulture) + ")";

    internal static JArray Bind(string question, AdvisorUnitReferences references, System.Collections.Generic.IEnumerable<JObject> units)
    {
        var rows = units.ToArray();
        var result = new JArray();
        var ids = reference.Matches(question).Cast<Match>().Select(m =>
            int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? (int?)id : null)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct();
        foreach (var id in ids)
        {
            var key = references.IdFor(id);
            var unit = key == null ? null : rows.SingleOrDefault(row => (string?)row["id"] == key);
            var binding = new JObject { ["unit_id"] = id, ["status"] = unit == null ? "unavailable" : "available" };
            if (unit != null)
            {
                binding["id"] = unit["id"]!.DeepClone();
                binding["display_name"] = unit["display_name"]!.DeepClone();
            }
            result.Add(binding);
        }
        return result;
    }
}
