using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Request owners share allowed/required fields with their published schema; value and game-state checks stay with the owner.
internal sealed class AdvisorRequestShape
{
    internal string[] Names { get; }
    private readonly string[] required;
    private readonly string[] optional;
    internal AdvisorRequestShape(string[] names, string[] required, params string[] optional)
    { Names = names; this.required = required; this.optional = optional; }

    internal bool Matches(string name) => Names.Contains(name, StringComparer.Ordinal);
    internal bool AllowsFields(JObject request, string tag) => request.Properties().All(p =>
        p.Name == tag || required.Contains(p.Name, StringComparer.Ordinal) || optional.Contains(p.Name, StringComparer.Ordinal));
    internal bool HasRequired(JObject request) => required.All(key => request[key] != null);

    internal JObject Schema(string tag, JObject fields, IEnumerable<string> enabled)
    {
        var names = Names.Where(enabled.Contains).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("An empty request variant cannot be published.");
        var properties = new JObject { [tag] = fields[tag]!.DeepClone() };
        properties[tag]!["enum"] = new JArray(names);
        foreach (var name in required.Concat(optional).Distinct(StringComparer.Ordinal))
        {
            if (fields[name] != null) properties[name] = fields[name]!.DeepClone();
            else if (required.Contains(name, StringComparer.Ordinal))
                throw new InvalidOperationException("A required request field has no schema: " + name);
        }
        return new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray(new[] { tag }.Concat(required)), ["properties"] = properties };
    }
    internal static JObject Union(string tag, IEnumerable<AdvisorRequestShape> shapes, JObject fields, IEnumerable<string> enabled)
    {
        var names = enabled.ToHashSet(StringComparer.Ordinal);
        return new JObject { ["anyOf"] = new JArray(shapes.Where(s => s.Names.Any(names.Contains)).Select(s => s.Schema(tag, fields, names))) };
    }
    internal static IEnumerable<JObject> Variants(JObject schema) => schema["anyOf"] is JArray variants
        ? variants.Select(v => v as JObject ?? throw new InvalidOperationException("A request variant must be an object."))
        : new[] { schema };
    internal static IEnumerable<string> Values(JObject schema, string field) => Variants(schema)
        .SelectMany(s => ((JArray)s["properties"]![field]!["enum"]!).Values<string>())
        .Select(value => value ?? throw new InvalidOperationException("A request name is missing.")).Distinct(StringComparer.Ordinal);
}
