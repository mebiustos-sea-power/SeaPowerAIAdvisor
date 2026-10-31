using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Crew;

// Crew owns its immutable selection and projects detached facts before building AI input.
internal sealed class CrewInformation
{
    internal static readonly IReadOnlyList<(string Id, string[] UnitFields)> Items = Array.AsReadOnly(new[]
    {
        ("movement", new[] { "position", "heading_deg", "speed_knots", "altitude_ft", "depth_ft" }),
        ("damage", new[] { "damage" }), ("fuel", new[] { "fuel" }),
        ("weapons", new[] { "weapons" }), ("sensors", new[] { "sensors" }),
        ("contacts", Array.Empty<string>())
    });
    internal static readonly CrewInformation Default = new(Array.Empty<string>());
    private readonly HashSet<string> excluded;
    private CrewInformation(IEnumerable<string> excluded)
    {
        this.excluded = new HashSet<string>(excluded, StringComparer.Ordinal);
        Key = string.Join(",", Items.Where(i => !Allows(i.Id)).Select(i => i.Id));
    }
    internal bool Allows(string id) => !excluded.Contains(id);
    internal string Key { get; }
    internal CrewInformation With(string id, bool enabled)
    {
        if (!Items.Any(i => i.Id == id)) throw new ArgumentOutOfRangeException(nameof(id));
        var next = new HashSet<string>(excluded, StringComparer.Ordinal);
        if (enabled) next.Remove(id); else next.Add(id);
        return new CrewInformation(next);
    }
    internal JObject Json() => new(Items.Select(i => new JProperty(i.Id, Allows(i.Id))));
    internal static CrewInformation Parse(JToken? token)
    {
        if (token == null) return Default;
        if (token is not JObject values || values.Properties().Any(p => p.Value.Type != JTokenType.Boolean || !Items.Any(i => i.Id == p.Name)))
            throw new CrewException("information_invalid");
        return new CrewInformation(values.Properties().Where(p => !(bool)p.Value).Select(p => p.Name));
    }
    internal JObject Filter(JObject situation)
    {
        var result = (JObject)situation.DeepClone();
        if (result["units"] is JToken units)
            foreach (JObject unit in (JArray)units)
                foreach (var item in Items.Where(i => !Allows(i.Id)))
                    foreach (var field in item.UnitFields) unit.Remove(field);
        if (!Allows("contacts")) result.Remove("contacts");
        if (excluded.Count > 0) result["excluded_information"] = new JArray(Items.Where(i => !Allows(i.Id)).Select(i => i.Id));
        return result;
    }
}
