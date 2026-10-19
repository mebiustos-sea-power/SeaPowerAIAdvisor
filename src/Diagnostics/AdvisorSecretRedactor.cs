using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Both diagnostic sinks remove declared secrets before retaining or displaying data.
internal sealed class AdvisorSecretRedactor
{
    private readonly string[] secrets;
    internal AdvisorSecretRedactor(IEnumerable<string> values) => secrets = values.Where(k => k.Length > 0)
        .SelectMany(KeyVariants).Distinct().OrderByDescending(k => k.Length).ToArray();

    // Preserve the original JSON text unless a secret must be removed. Parsing also
    // catches an echoed key encoded as JSON escapes; malformed bodies remain useful text.
    internal string RedactBody(string body)
    {
        try
        {
            using var source = new StringReader(body);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) return Scrub(body);
            return RedactToken(token) ? token.ToString(Formatting.None) : body;
        }
        catch (JsonException) { return Scrub(body); }
    }

    internal bool RedactToken(JToken token)
    {
        var changed = false;
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToArray())
            {
                changed |= RedactToken(property.Value);
                var name = Scrub(property.Name);
                if (name != property.Name)
                {
                    property.Replace(new JProperty(name, property.Value.DeepClone()));
                    changed = true;
                }
            }
        }
        else if (token is JArray array)
            foreach (var item in array) changed |= RedactToken(item);
        else if (token is JValue value && value.Type == JTokenType.String && value.Value is string original)
        {
            var safe = Scrub(original);
            if (safe != original) { value.Value = safe; changed = true; }
        }
        return changed;
    }

    private static string[] KeyVariants(string key)
    {
        var variants = new string[5];
        variants[0] = key;
        for (var i = 1; i < 4; i++)
        {
            var quoted = JsonConvert.ToString(variants[i - 1]);
            variants[i] = quoted.Substring(1, quoted.Length - 2);
        }
        variants[4] = Uri.EscapeDataString(key);
        return variants;
    }

    internal string Scrub(string value)
    {
        foreach (var secret in secrets)
            value = value.Replace(secret, "<api-key-removed>");
        return value;
    }

}
