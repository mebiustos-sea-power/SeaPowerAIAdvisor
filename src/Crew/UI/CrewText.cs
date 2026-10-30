using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Crew;

internal sealed class CrewText
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    internal CrewText(string language)
    {
        void Read(string code)
        {
            using var source = typeof(CrewText).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Crew.Localization." + code + ".json")
                ?? throw new FileNotFoundException("The embedded crew localization is missing: " + code);
            using var reader = new StreamReader(source);
            foreach (var property in JObject.Parse(reader.ReadToEnd()).Properties()) values[property.Name] = (string?)property.Value ?? throw new InvalidDataException("Crew text is not a string: " + property.Name);
        }
        Read("en"); if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || language.Equals("Japanese", StringComparison.OrdinalIgnoreCase)) Read("ja");
    }
    internal string this[string key] => values.TryGetValue(key, out var text) ? text : throw new InvalidOperationException("Missing crew localization: " + key);
    internal IEnumerable<KeyValuePair<string, string>> Values => values;
}
