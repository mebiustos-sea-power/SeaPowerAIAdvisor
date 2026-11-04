using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Registered credentials have stable identities; role assignments never move their contents.
internal sealed class AdvisorApiConfiguration : AiConnectionProfile
{
    internal AdvisorSituationDelivery SituationDelivery { get; }
    internal AdvisorApiConfiguration(string id, string name, string key, string model, int requestTimeoutSeconds = AiRequestTimeout.DefaultSeconds, int maxOutputTokens = AiOutputTokens.Default)
        : this(id, name, "gemini", new JObject { ["api_key"] = key.Trim(), ["model"] = model.Trim() }, requestTimeoutSeconds, maxOutputTokens: maxOutputTokens) { }
    internal AdvisorApiConfiguration(string id, string name, string provider, JObject settings, int requestTimeoutSeconds = AiRequestTimeout.DefaultSeconds, AdvisorSituationDelivery situationDelivery = AdvisorSituationDelivery.OnDemand, int maxOutputTokens = AiOutputTokens.Default, string prompt = "")
        : base(id, name, provider, settings, requestTimeoutSeconds, maxOutputTokens, prompt)
    { SituationDelivery = situationDelivery; _ = situationDelivery.Code(); }
    internal override JObject Json() { var value = base.Json(); value["situation_delivery"] = SituationDelivery.Code(); return value; }
}

internal sealed class AdvisorApiLibrary
{
    internal const int SchemaVersion = AdvisorApiSettingsMigration.CurrentVersion;
    internal const string FollowAdvisor = "";
    internal IReadOnlyList<AdvisorApiConfiguration> Configurations { get; }
    internal string? MainId { get; }
    internal string? SubId { get; }
    // Null disables translation; an empty ID follows the advisor assignment without copying its current ID.
    internal string? TranslationId { get; }
    internal AdvisorApiLibrary(IEnumerable<AdvisorApiConfiguration> configurations, string? mainId, string? subId, string? translationId = null)
    { Configurations = Array.AsReadOnly(configurations.ToArray()); MainId = mainId; SubId = subId; TranslationId = translationId; }
    internal AdvisorApiConfiguration? Find(string? id) => id == null ? null : Configurations.SingleOrDefault(c => c.Id == id);

    internal static AdvisorApiLibrary Read(JObject source)
    {
        if (ConfigMigration.Version(source) != SchemaVersion || source["api_configurations"] is not JArray entries ||
            source["main_api_id"] == null || source["main_api_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            source["sub_api_id"] == null || source["sub_api_id"]!.Type is not (JTokenType.String or JTokenType.Null)) throw Invalid();
        if (source["translation_api_id"] != null && source["translation_api_id"]!.Type is not (JTokenType.String or JTokenType.Null)) throw Invalid();
        var items = new List<AdvisorApiConfiguration>();
        foreach (var entry in entries)
        {
            var fields = new[] { "id", "name", "provider", "settings" };
            if (entry is not JObject item || item.Properties().Any(p => !fields.Contains(p.Name) && (p.Name != "request_timeout_seconds" && p.Name != "situation_delivery" && p.Name != "max_output_tokens" && p.Name != "prompt")) ||
                fields.Any(f => item[f]?.Type != (f == "settings" ? JTokenType.Object : JTokenType.String))) throw Invalid();
            if (item["prompt"] != null && item["prompt"]!.Type != JTokenType.String)
                throw new AiConnectionException("prompt_invalid");
            items.Add(new AdvisorApiConfiguration((string)item["id"]!, (string)item["name"]!, (string)item["provider"]!, (JObject)item["settings"]!,
                AiRequestTimeout.Read(item), AdvisorSituationDeliverySetting.Read(item), AiOutputTokens.Read(item), (string?)item["prompt"] ?? ""));
        }
        var result = new AdvisorApiLibrary(items, (string?)source["main_api_id"], (string?)source["sub_api_id"], (string?)source["translation_api_id"]);
        result.ValidateAssignments();
        return result;
    }

    internal void ValidateAssignments()
    {
        AiConnectionProfile.ValidateLibrary(Configurations);
        if ((MainId != null && Find(MainId) == null) || (SubId != null && Find(SubId) == null) || (TranslationId != null && TranslationId != FollowAdvisor && Find(TranslationId) == null)) throw new AiConnectionException("api_selection_invalid");
        if (MainId != null && MainId == SubId) throw new AiConnectionException("api_selection_same");
    }

    internal JObject Document(JObject source, bool log)
    {
        ValidateAssignments();
        var result = Shared(source);
        result["schema_version"] = SchemaVersion;
        result["api_configurations"] = new JArray(Configurations.Select(c => c.Json()));
        result["main_api_id"] = MainId == null ? JValue.CreateNull() : new JValue(MainId);
        result["sub_api_id"] = SubId == null ? JValue.CreateNull() : new JValue(SubId);
        result["translation_api_id"] = TranslationId == null ? JValue.CreateNull() : new JValue(TranslationId);
        result["log_ai_json"] = log;
        return result;
    }

    private static JObject Shared(JObject source) => new(source.Properties()
        .Where(p => p.Name is "request_timeout_seconds" or "log_ai_json")
        .Select(p => new JProperty(p.Name, p.Value.DeepClone())));
    private static AiConnectionException Invalid() => new("settings_invalid");
}

internal sealed class AdvisorApiConfigurationDraft : AiConnectionDraft
{
    internal AdvisorSituationDelivery SituationDelivery { get; set; }
    internal AdvisorApiConfigurationDraft(AdvisorApiConfiguration value) : base(value) { SituationDelivery = value.SituationDelivery; }
    internal new AdvisorApiConfiguration Build() => new(Id, Name, Provider, Settings, RequestTimeoutSeconds, SituationDelivery, MaxOutputTokens, Prompt);
}
