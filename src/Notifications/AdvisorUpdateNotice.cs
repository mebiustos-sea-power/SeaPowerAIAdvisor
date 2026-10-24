using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A release carries at most one notice. Its stable ID, not the game language or assembly version, identifies it.
internal sealed class AdvisorUpdateNotice
{
    private static readonly Lazy<AdvisorUpdateNotice?> bundled = new(ReadBundled);
    internal static AdvisorUpdateNotice? Current => bundled.Value;
    internal string Id { get; }
    internal string TitleKey { get; }
    internal string BodyKey { get; }
    private AdvisorUpdateNotice(string id, string titleKey, string bodyKey)
    { Id = id; TitleKey = titleKey; BodyKey = bodyKey; }
    internal static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
    internal static AdvisorUpdateNotice? Parse(string json)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 3 };
            var value = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Trailing update notice content.");
            if (value.Type == JTokenType.Null) return null;
            var keys = new[] { "id", "title_key", "body_key" };
            if (value is not JObject item || item.Properties().Any(p => !keys.Contains(p.Name)) ||
                keys.Any(k => item[k]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)item[k])) ||
                !ValidId((string)item["id"]!)) throw new InvalidDataException("Invalid update notice definition.");
            return new AdvisorUpdateNotice((string)item["id"]!, (string)item["title_key"]!, (string)item["body_key"]!);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid update notice JSON.", error); }
    }
    private static AdvisorUpdateNotice? ReadBundled()
    {
        using var stream = typeof(AdvisorUpdateNotice).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.UpdateNotice.json")
            ?? throw new FileNotFoundException("The bundled update notice definition is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return Parse(reader.ReadToEnd());
    }
}
