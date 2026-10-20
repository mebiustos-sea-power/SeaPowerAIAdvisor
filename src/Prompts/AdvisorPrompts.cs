using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A consultation retains one immutable snapshot; temporary edits never mutate an existing bundle.
internal sealed class AdvisorPrompts
{
    internal static string DefaultDirectory => Path.Combine(Path.GetDirectoryName(typeof(AdvisorPrompts).Assembly.Location)!, "Prompts");
    internal static AdvisorPrompts Current => AdvisorPromptOverrides.Session.Current;
    private readonly JObject common, campaign, battle, requests;
    private AdvisorPrompts(JObject common, JObject campaign, JObject battle, JObject requests)
    { this.common = common; this.campaign = campaign; this.battle = battle; this.requests = requests; }
    internal string PreferencesPolicy => (string)common["preferences_policy"]!;
    internal string AviationSemantics => (string)campaign["aviation_semantics"]!;
    internal JObject Instructions(bool tactical)
    {
        var result = (JObject)common["instructions"]!.DeepClone();
        var mode = tactical ? battle : campaign;
        result["evidence_policy"] = mode["evidence_policy"]!.DeepClone();
        result["retrieval"] = mode["retrieval"]!.DeepClone();
        if (tactical)
        {
            result["unit_links"] = battle["unit_links"]!.DeepClone();
            result["weapon_links"] = battle["weapon_links"]!.DeepClone();
        }
        return result;
    }
    internal string SystemInstruction(JToken instructions) => (string)common["system"]! + "\n" +
        instructions.ToString(Formatting.None) + "\n" + PreferencesPolicy;

    private (string Name, JObject Value)[] Documents => new[]
    { ("common.json", common), ("requests.json", requests), ("battle.json", battle), ("campaign.json", campaign) };
    internal IReadOnlyList<AdvisorPromptField> Fields
    {
        get
        {
            var result = new List<AdvisorPromptField>();
            foreach (var document in Documents)
                foreach (var property in document.Value.Properties().Where(p => p.Name != "schema_version"))
                    if (property.Value is JObject children)
                        result.AddRange(children.Properties().Select(child => new AdvisorPromptField(document.Name, property.Name + "." + child.Name, (string)child.Value!)));
                    else result.Add(new AdvisorPromptField(document.Name, property.Name, (string)property.Value!));
            return result.AsReadOnly();
        }
    }
    internal AdvisorPrompts WithText(IReadOnlyDictionary<string, string> values)
    {
        var fields = Fields;
        if (values.Count != fields.Count) throw Invalid("editor");
        var encoding = new UTF8Encoding(false, true);
        foreach (var field in fields)
        {
            if (!values.TryGetValue(field.Id, out var value) || value == null) throw Invalid(field.FileName);
            try { _ = encoding.GetByteCount(value); }
            catch (EncoderFallbackException) { throw Invalid(field.FileName); }
        }
        if (fields.All(f => values[f.Id] == f.Value)) return this;
        var documents = Documents.ToDictionary(d => d.Name, d => (JObject)d.Value.DeepClone());
        // Empty strings intentionally allow developers to try removing an individual instruction.
        // Keys, JSON structure and the startup files remain intact.
        foreach (var field in fields)
            documents[field.FileName].SelectToken(field.Key)!.Replace(new JValue(values[field.Id]));
        foreach (var document in documents)
            if (encoding.GetByteCount(document.Value.ToString(Formatting.None)) > 65_536) throw Invalid(document.Key);
        return new AdvisorPrompts(documents["common.json"], documents["campaign.json"], documents["battle.json"], documents["requests.json"]);
    }

    internal static AdvisorPrompts Load(string directory)
    {
        var common = Read(directory, "common.json", "system", "instructions", "preferences_policy");
        if (!(common["instructions"] is JObject instructions)) throw Invalid("common.json");
        ValidateStrings(instructions, "common.json", "task", "advice_policy", "retrieval_policy", "language", "knowledge", "source_text", "coverage", "information_policy", "format");
        var campaign = Read(directory, "campaign.json", CampaignFields);
        var battle = Read(directory, "battle.json", BattleFields);
        var requests = directory == DefaultDirectory ? (JObject)DefaultRequests.Value.DeepClone() : ReadRequests(directory);
        return new AdvisorPrompts(common, campaign, battle, requests);
    }
    internal string Text(string file, string key)
    {
        var document = file switch
        {
            "common.json" => common, "requests.json" => requests,
            "battle.json" => battle, "campaign.json" => campaign,
            _ => throw Invalid(file)
        };
        return (string?)document.SelectToken(key) ?? throw Invalid(file);
    }
    internal string Format(string file, string key, params object[] arguments)
    {
        try { return string.Format(CultureInfo.InvariantCulture, Text(file, key), arguments); }
        catch (FormatException) { throw Invalid(file); }
    }
    // Connection tests remain independent of unavailable battle/campaign instructions.
    private static readonly Lazy<JObject> DefaultRequests = new(() => ReadRequests(DefaultDirectory));
    internal static string ConnectionTestRequest(AdvisorPrompts? prompts = null) =>
        (prompts ?? AdvisorPromptOverrides.LoadedCurrent)?.Text("requests.json", "connection_test")
        ?? (string)DefaultRequests.Value["connection_test"]!;
    internal static string ToolConnectionTestRequest(AdvisorPrompts? prompts = null) =>
        (prompts ?? AdvisorPromptOverrides.LoadedCurrent)?.Text("requests.json", "connection_tool_test")
        ?? (string)DefaultRequests.Value["connection_tool_test"]!;
    internal static JObject ReadRequests(string directory) => Read(directory, "requests.json", RequestFields);
    private static readonly string[] BattleFields =
    {
        "evidence_policy", "retrieval", "unit_links", "weapon_links", "error_round_limit", "error_wrong_consultation", "error_briefing_unavailable", "base_briefing_note",
        "error_request_too_large", "error_requests_invalid", "error_query_name_invalid", "error_response_too_large",
        "error_payload_limit", "error_information_disabled", "error_contact_kind_invalid", "error_ids_invalid",
        "error_unknown_id", "error_unknown_query", "error_sections_invalid", "error_sections_disabled",
        "error_pagination_invalid", "error_unsupported_argument", "catalog_friendly_units", "catalog_friendly_ground_units",
        "catalog_friendly_bases", "catalog_hosted_air_units", "catalog_aviation_tasks", "catalog_unit_capabilities",
        "catalog_public_class_threat_references", "catalog_observed_contacts", "catalog_mission_briefing", "catalog_published_reports",
        "weapon_reference_delivery", "sensor_reference_delivery", "tool_enabled_sections", "tool_description", "tool_ids",
        "tool_host_ids", "tool_air_unit_ids", "tool_kind", "tool_sections",
        "tool_offset", "tool_limit", "host_count_semantics",
        "catalog_identification_candidates", "catalog_friendly_launched_weapons", "catalog_movement_plans",
    };
    private static readonly string[] CampaignFields =
    {
        "evidence_policy", "retrieval", "aviation_semantics", "error_requests_count",
        "error_ids_count", "error_integer_type", "error_integer_range", "error_boolean_type",
        "error_string_type", "error_round_limit", "error_wrong_consultation", "error_request_too_large",
        "error_query_object_invalid", "error_response_too_large", "error_payload_limit", "error_information_disabled",
        "error_ships_disabled", "error_unknown_ship", "error_force_filter_disabled", "error_air_filters_conflict",
        "error_unknown_base", "error_unknown_ground_unit", "error_report_kind_invalid", "error_unknown_query",
        "error_unknown_force", "error_unknown_id", "error_duplicate_ids", "error_unsupported_argument",
        "catalog_friendly_task_forces", "catalog_friendly_ships", "catalog_friendly_air_units", "catalog_friendly_air_flights",
        "catalog_observed_contacts", "catalog_public_bases", "catalog_friendly_bases", "catalog_friendly_ground_units",
        "catalog_base_observations", "catalog_published_orders", "catalog_recent_player_reports", "tool_description",
        "tool_ids", "tool_force_ids", "tool_base_ids", "tool_offset",
        "tool_limit", "tool_include_ships", "tool_kind", "byte_counting",
        "victory_description",
    };
    private static readonly string[] RequestFields =
    {
        "next_steps", "more_detail", "key_risks", "radar_operations", "own_units", "contacts", "connection_test", "connection_tool_test", "contact_assessment", "contact_reassessment",
    };

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

internal sealed class AdvisorPromptField
{
    internal string FileName { get; }
    internal string Key { get; }
    internal string Value { get; }
    internal string Id => FileName + ":" + Key;
    internal AdvisorPromptField(string fileName, string key, string value)
    { FileName = fileName; Key = key; Value = value; }
}

internal sealed class AdvisorPromptException : Exception
{
    internal string Code { get; }
    internal string FileName { get; }
    internal AdvisorPromptException(string code, string fileName) : base(code + ": " + fileName)
    { Code = code; FileName = fileName; }
}
