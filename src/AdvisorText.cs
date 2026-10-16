using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace SeaPowerAIAdvisor;

// Presentation only. This catalogue never changes game data or the AI reply language.
internal sealed class AdvisorText
{
    private const string Prefix = "SeaPowerAIAdvisor.Localization.";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> bundled = new(ReadBundled);
    private readonly IReadOnlyDictionary<string, string> english;
    private readonly IReadOnlyDictionary<string, string> selected;
    internal string Language { get; }
    internal IEnumerable<KeyValuePair<string, string>> Entries =>
        english.Keys.Select(k => new KeyValuePair<string, string>(k, this[k]));

    private AdvisorText(string language, IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> selected)
    { Language = language; this.english = english; this.selected = selected; }

    internal string this[string key] => english.ContainsKey(key)
        ? selected.TryGetValue(key, out var value) ? value : english[key]
        : throw new KeyNotFoundException("Unknown advisor text key: " + key);
    internal bool Contains(string key) => english.ContainsKey(key);
    internal string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);
    internal static AdvisorText Load(string language) => FromJson(language, bundled.Value);

    internal static AdvisorText FromJson(string language, IReadOnlyDictionary<string, string> files)
    {
        var code = LanguageCode.Validate(language).ToLowerInvariant();
        if (!files.TryGetValue("en", out var source)) throw new FileNotFoundException("The English advisor text resource is missing.");
        var english = Parse(source, "en");
        var selected = english;
        var resolved = "en";
        foreach (var candidate in new[] { code, code.Split('-')[0] }.Distinct())
        {
            if (!files.TryGetValue(candidate, out source)) continue;
            selected = candidate == "en" ? english : Parse(source, candidate);
            if (selected.Keys.Any(k => !english.ContainsKey(k)))
                throw new InvalidDataException("Advisor translation contains a key absent from English: " + candidate);
            resolved = candidate;
            break;
        }
        return new AdvisorText(resolved, english, selected);
    }

    private static IReadOnlyDictionary<string, string> Parse(string json, string language)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException("Trailing translation content.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in value.Properties())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace((string?)property.Value))
                    throw new InvalidDataException("Invalid advisor translation entry: " + language);
                result.Add(property.Name, (string)property.Value!);
            }
            if (result.Count == 0) throw new InvalidDataException("The advisor translation is empty: " + language);
            return result;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid advisor translation JSON: " + language, error); }
    }

    private static IReadOnlyDictionary<string, string> ReadBundled()
    {
        var assembly = typeof(AdvisorText).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException("The bundled advisor translation is missing: " + name);
            using var reader = new StreamReader(stream);
            files.Add(name.Substring(Prefix.Length, name.Length - Prefix.Length - 5), reader.ReadToEnd());
        }
        foreach (var required in new[] { "en", "ja" })
            if (!files.ContainsKey(required)) throw new FileNotFoundException("The bundled advisor translation is missing: " + required);
        // Validate every bundled translation, even if it is not currently selected.
        foreach (var language in files.Keys) FromJson(language, files);
        return files;
    }
}
