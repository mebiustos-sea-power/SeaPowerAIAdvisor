using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// One immutable bundle is shared by all consultations. Files are never reread during a consultation.
internal sealed class AdvisorPrompts
{
    private static readonly Lazy<AdvisorPrompts> current = new(() => Load(DefaultDirectory));
    internal static string DefaultDirectory => Path.Combine(Path.GetDirectoryName(typeof(AdvisorPrompts).Assembly.Location)!, "Prompts");
    internal static AdvisorPrompts Current => current.Value;
    private readonly JObject common, campaign, battle;
    private AdvisorPrompts(JObject common, JObject campaign, JObject battle)
    { this.common = common; this.campaign = campaign; this.battle = battle; }
    internal string PreferencesPolicy => (string)common["preferences_policy"]!;
    internal string AviationSemantics => (string)campaign["aviation_semantics"]!;
    internal JObject Instructions(bool tactical)
    {
        var result = (JObject)common["instructions"]!.DeepClone();
        var mode = tactical ? battle : campaign;
        result["evidence_policy"] = mode["evidence_policy"]!.DeepClone();
        result["retrieval"] = mode["retrieval"]!.DeepClone();
        return result;
    }
    internal string SystemInstruction(JToken instructions) => (string)common["system"]! + "\n" +
        instructions.ToString(Formatting.None) + "\n" + PreferencesPolicy;

    internal static AdvisorPrompts Load(string directory)
    {
        var common = Read(directory, "common.json", "system", "instructions", "preferences_policy");
        if (!(common["instructions"] is JObject instructions)) throw Invalid("common.json");
        ValidateStrings(instructions, "common.json", "task", "retrieval_policy", "language", "knowledge", "source_text", "coverage", "information_policy");
        var campaign = Read(directory, "campaign.json", "evidence_policy", "retrieval", "aviation_semantics");
        var battle = Read(directory, "battle.json", "evidence_policy", "retrieval");
        return new AdvisorPrompts(common, campaign, battle);
    }
    private static JObject Read(string directory, string name, params string[] fields)
    {
        try
        {
            var path = Path.Combine(directory, name);
            if (new FileInfo(path).Length > 65_536) throw Invalid(name);
            var json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 5, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value["schema_version"]?.Type != JTokenType.Integer || value["schema_version"]!.ToString(Formatting.None) != "1" ||
                value.Properties().Any(p => p.Name != "schema_version" && !fields.Contains(p.Name)) ||
                fields.Any(f => value[f] == null)) throw Invalid(name);
            foreach (var field in fields.Where(f => f != "instructions"))
                if (value[field]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value[field])) throw Invalid(name);
            return value;
        }
        catch (FileNotFoundException) { throw new AdvisorPromptException("prompts_missing", name); }
        catch (DirectoryNotFoundException) { throw new AdvisorPromptException("prompts_missing", name); }
        catch (UnauthorizedAccessException) { throw new AdvisorPromptException("prompts_unreadable", name); }
        catch (IOException) { throw new AdvisorPromptException("prompts_unreadable", name); }
        catch (DecoderFallbackException) { throw Invalid(name); }
        catch (JsonException) { throw Invalid(name); }
    }
    private static void ValidateStrings(JObject value, string name, params string[] fields)
    {
        if (value.Properties().Any(p => !fields.Contains(p.Name)) || fields.Any(f => value[f]?.Type != JTokenType.String ||
            string.IsNullOrWhiteSpace((string?)value[f]))) throw Invalid(name);
    }
    private static AdvisorPromptException Invalid(string name) => new("prompts_invalid", name);
}

internal sealed class AdvisorPromptException : Exception
{
    internal string Code { get; }
    internal string FileName { get; }
    internal AdvisorPromptException(string code, string fileName) : base(code + ": " + fileName)
    { Code = code; FileName = fileName; }
}
