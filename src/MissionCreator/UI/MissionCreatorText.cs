using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIMissionCreator.UI;

internal static class MissionCreatorText
{
    internal static (string Launcher, string Title) Load(string language)
    {
        var code = LanguageCode.Validate(language).ToLowerInvariant();
        var english = Read("en") ?? throw new FileNotFoundException("The English Mission Creator translation is missing.");
        var selected = Read(code) ?? Read(code.Split('-')[0]) ?? english;
        string Value(string key)
        {
            var fallback = english[key];
            if (fallback?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)fallback))
                throw new InvalidDataException("Missing English Mission Creator text: " + key);
            var value = selected[key] ?? fallback;
            if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value))
                throw new InvalidDataException("Invalid Mission Creator text: " + key);
            return (string)value!;
        }
        return (Value("launcher"), Value("title"));
    }

    private static JObject? Read(string language)
    {
        using var stream = typeof(MissionCreatorText).Assembly.GetManifestResourceStream("SeaPowerAIMissionCreator.Localization." + language + ".json");
        if (stream == null) return null;
        using var source = new StreamReader(stream);
        using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 4 };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing Mission Creator translation content.");
        return result;
    }
}
