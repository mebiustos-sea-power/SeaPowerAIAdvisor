using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Each command owns its resources; resolution and validation use the shared UI text rules.
internal sealed class AdvisorOperationText
{
    private readonly IReadOnlyDictionary<string, AdvisorText> languages;
    internal AdvisorOperationText(IReadOnlyDictionary<string, string> files)
    {
        var source = files.ToDictionary(p => LanguageCode.Validate(p.Key).ToLowerInvariant(), p => p.Value, StringComparer.Ordinal);
        var english = AdvisorText.FromJson("en", source);
        foreach (var key in new[] { "name", "summary", "requirements" })
            if (!english.Contains(key)) throw new InvalidDataException("Missing command text: " + key);
        languages = source.Keys.ToDictionary(code => code, code => AdvisorText.FromJson(code, source), StringComparer.Ordinal);
    }
    internal AdvisorText For(string language)
    {
        var code = LanguageCode.Validate(language).ToLowerInvariant();
        foreach (var candidate in new[] { code, code.Split('-')[0], "en" })
            if (languages.TryGetValue(candidate, out var text)) return text;
        throw new InvalidOperationException("The English command text was not loaded.");
    }
    internal static AdvisorOperationText FromResources(Assembly assembly, string prefix, params string[] requiredLanguages) =>
        new(AdvisorText.ReadResources(assembly, prefix, requiredLanguages));
}
