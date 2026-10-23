using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Evaluate the published boundary schema independently of the request owners' field tables.
internal static class RequestSchemaFixture
{
    internal static IEnumerable<JToken> Variants(JToken schema) => schema["anyOf"] is JArray choices ? choices : new[] { schema };
    internal static JToken Items(JToken tool) => tool["parameters"]!["properties"]!["requests"]!["items"]!;
    internal static IEnumerable<string?> Queries(JToken tool) => Variants(Items(tool))
        .SelectMany(v => v["properties"]!["query"]!["enum"]!.Values<string>()).Distinct(StringComparer.Ordinal);
    internal static JToken Field(JToken tool, string query, string field) => Variants(Items(tool))
        .First(v => v["properties"]?["query"]?["enum"]?.Values<string>().Contains(query) == true)["properties"]![field]!;

    internal static bool Accepts(JToken schema, JToken value)
    {
        if (schema["anyOf"] is JArray alternatives) return alternatives.Any(s => Accepts(s, value));
        if (schema["enum"] is JArray options && !options.Any(o => JToken.DeepEquals(o, value))) return false;
        switch ((string?)schema["type"])
        {
            case "object":
                if (value is not JObject obj) return false;
                var fields = schema["properties"] as JObject ?? new JObject();
                if (schema["required"] is JArray required && required.Values<string>().Any(k => obj[k!] == null)) return false;
                return obj.Properties().All(p => fields[p.Name] != null ? Accepts(fields[p.Name]!, p.Value) : (bool?)schema["additionalProperties"] != false);
            case "array":
                if (value is not JArray array || array.Count < ((int?)schema["minItems"] ?? 0) || array.Count > ((int?)schema["maxItems"] ?? int.MaxValue)) return false;
                if ((bool?)schema["uniqueItems"] == true && array.Where((x, i) => array.Take(i).Any(y => JToken.DeepEquals(x, y))).Any()) return false;
                return schema["items"] == null || array.All(x => Accepts(schema["items"]!, x));
            case "string": return value.Type == JTokenType.String && ((string)value!).Length >= ((int?)schema["minLength"] ?? 0) &&
                ((string)value!).Length <= ((int?)schema["maxLength"] ?? int.MaxValue);
            case "integer": return value.Type == JTokenType.Integer && (double)value >= ((double?)schema["minimum"] ?? double.NegativeInfinity) &&
                (double)value <= ((double?)schema["maximum"] ?? double.PositiveInfinity);
            case "boolean": return value.Type == JTokenType.Boolean;
            default: throw new InvalidOperationException("Unsupported fixture schema: " + schema);
        }
    }
}
