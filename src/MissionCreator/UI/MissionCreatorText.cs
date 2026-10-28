using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIMissionCreator.UI;

internal sealed class MissionCreatorText
{
    private readonly JObject english, selected;
    internal string Language { get; }
    internal string Launcher => this["launcher"];
    internal string Title => this["title"];
    private MissionCreatorText(string language, JObject english, JObject selected)
    { Language = language; this.english = english; this.selected = selected; }
    internal string this[string key]
    {
        get
        {
            var value = selected[key] ?? english[key];
            if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value)) throw new InvalidDataException("Missing Mission Creator text: " + key);
            return (string)value!;
        }
    }
    internal string Error(string code) => english["connection." + code] != null ? this["connection." + code] : string.Format(System.Globalization.CultureInfo.InvariantCulture, this["error"], code);
    internal string Format(string key, params object[] values) => string.Format(System.Globalization.CultureInfo.InvariantCulture, this[key], values);
    internal static MissionCreatorText Load(string language)
    {
        var code = LanguageCode.Validate(language).ToLowerInvariant();
        var english = Read("en") ?? throw new FileNotFoundException("The English Mission Creator translation is missing.");
        return new MissionCreatorText(code, english, Read(code) ?? Read(code.Split('-')[0]) ?? english);
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
