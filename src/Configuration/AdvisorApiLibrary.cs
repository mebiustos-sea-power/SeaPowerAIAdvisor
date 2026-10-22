using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Registered credentials have stable identities; main/sub assignments never move their contents.
internal sealed class AdvisorApiConfiguration
{
    internal string Id { get; }
    internal string Name { get; }
    internal string Provider { get; }
    internal int RequestTimeoutSeconds { get; }
    internal JObject Settings { get; }
    internal AdvisorSituationDelivery SituationDelivery { get; }
    internal string ApiKey => (string?)Settings["api_key"] ?? "";
    internal string Model => (string?)Settings["model"] ?? "";
    internal AdvisorApiConfiguration(string id, string name, string key, string model, int requestTimeoutSeconds = AdvisorRequestTimeout.DefaultSeconds)
        : this(id, name, "gemini", new JObject { ["api_key"] = key.Trim(), ["model"] = model.Trim() }, requestTimeoutSeconds) { }
    internal AdvisorApiConfiguration(string id, string name, string provider, JObject settings, int requestTimeoutSeconds = AdvisorRequestTimeout.DefaultSeconds, AdvisorSituationDelivery situationDelivery = AdvisorSituationDelivery.OnDemand)
    { SituationDelivery = situationDelivery; _ = situationDelivery.Code(); Id = id; Name = name.Trim(); Provider = provider; Settings = (JObject)settings.DeepClone(); RequestTimeoutSeconds = AdvisorRequestTimeout.Validate(requestTimeoutSeconds); }
    internal JObject Json() => new() { ["id"] = Id, ["name"] = Name, ["provider"] = Provider, ["settings"] = Settings.DeepClone(), ["request_timeout_seconds"] = RequestTimeoutSeconds, ["situation_delivery"] = SituationDelivery.Code() };
}

internal sealed class AdvisorApiLibrary
{
    internal const int SchemaVersion = 3;
    internal IReadOnlyList<AdvisorApiConfiguration> Configurations { get; }
    internal string? MainId { get; }
    internal string? SubId { get; }
    internal AdvisorApiLibrary(IEnumerable<AdvisorApiConfiguration> configurations, string? mainId, string? subId)
    { Configurations = Array.AsReadOnly(configurations.ToArray()); MainId = mainId; SubId = subId; }
    internal AdvisorApiConfiguration? Find(string? id) => id == null ? null : Configurations.SingleOrDefault(c => c.Id == id);

    internal static AdvisorApiLibrary Read(JObject source, string namePattern = "API setting {0}")
    {
        string String(string key, string fallback) => source[key] == null ? fallback :
            source[key]!.Type == JTokenType.String ? (string)source[key]! : throw Invalid();
        var legacyTimeout = AdvisorRequestTimeout.Read(source);
        string Name(int number) => string.Format(CultureInfo.InvariantCulture, namePattern, number);
        if (source["schema_version"] == null)
        {
            if (new[] { "api_configurations", "main_api_id", "sub_api_id" }.Any(k => source[k] != null)) throw Invalid();
            var main = new AdvisorApiConfiguration("api-1", Name(1), String("api_key", ""), String("model", AdvisorSettings.DefaultModel), legacyTimeout);
            var key = String("fallback_api_key", "");
            var model = String("fallback_model", main.Model);
            var configurations = new List<AdvisorApiConfiguration>();
            if (main.ApiKey.Length > 0 && main.Model.Length > 0) configurations.Add(main);
            var sub = new AdvisorApiConfiguration("api-2", Name(2), key, model, legacyTimeout);
            // Missing fields are unconfigured, including legacy model-only sub settings.
            if (sub.ApiKey.Length > 0 && sub.Model.Length > 0) configurations.Add(sub);
            return new AdvisorApiLibrary(configurations, configurations.Contains(main) ? main.Id : null,
                configurations.Contains(sub) ? sub.Id : null);
        }
        if (source["schema_version"]!.Type != JTokenType.Integer || source["schema_version"]!.ToString(Newtonsoft.Json.Formatting.None) is not ("2" or "3") ||
            source["api_configurations"] is not JArray entries || source["main_api_id"] == null || source["main_api_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            source["sub_api_id"] == null || source["sub_api_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            new[] { "api_key", "fallback_api_key", "model", "fallback_model" }.Any(k => source[k] != null)) throw Invalid();
        var items = new List<AdvisorApiConfiguration>();
        foreach (var entry in entries)
        {
            var legacy = (int)source["schema_version"]! == 2;
            var fields = legacy ? new[] { "id", "name", "api_key", "model" } : new[] { "id", "name", "provider", "settings" };
            if (entry is not JObject item || item.Properties().Any(p => !fields.Contains(p.Name) && (legacy || (p.Name != "request_timeout_seconds" && p.Name != "situation_delivery"))) ||
                fields.Any(f => item[f]?.Type != (f == "settings" ? JTokenType.Object : JTokenType.String))) throw Invalid();
            items.Add(legacy
                ? new AdvisorApiConfiguration((string)item["id"]!, (string)item["name"]!, (string)item["api_key"]!, (string)item["model"]!, legacyTimeout)
                : new AdvisorApiConfiguration((string)item["id"]!, (string)item["name"]!, (string)item["provider"]!, (JObject)item["settings"]!, AdvisorRequestTimeout.Read(item, legacyTimeout), AdvisorSituationDeliverySetting.Read(item)));
        }
        var result = new AdvisorApiLibrary(items, (string?)source["main_api_id"], (string?)source["sub_api_id"]);
        result.ValidateAssignments();
        return result;
    }

    internal void ValidateAssignments()
    {
        if (Configurations.Any(c => string.IsNullOrWhiteSpace(c.Provider) || c.Provider.Length > 80 || c.Provider.Any(char.IsControl)) ||
            Configurations.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 80 || c.Id.Any(char.IsControl)) ||
            Configurations.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != Configurations.Count) throw Invalid();
        if (Configurations.Any(c => c.Name.Length == 0 || c.Name.Length > 80 || c.Name.Any(char.IsControl)) ||
            Configurations.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Configurations.Count)
            throw new AdvisorConnectionException("api_configuration_name_invalid");
        if ((MainId != null && Find(MainId) == null) || (SubId != null && Find(SubId) == null)) throw new AdvisorConnectionException("api_selection_invalid");
        if (MainId != null && MainId == SubId) throw new AdvisorConnectionException("api_selection_same");
    }

    internal JObject Document(JObject source, bool log)
    {
        ValidateAssignments();
        var result = Shared(source);
        result["schema_version"] = SchemaVersion;
        result["api_configurations"] = new JArray(Configurations.Select(c => c.Json()));
        result["main_api_id"] = MainId == null ? JValue.CreateNull() : new JValue(MainId);
        result["sub_api_id"] = SubId == null ? JValue.CreateNull() : new JValue(SubId);
        result["log_ai_json"] = log;
        return result;
    }

    internal static JObject Connection(JObject source, AdvisorApiConfiguration main, AdvisorApiConfiguration? sub = null)
    {
        var result = Shared(source);
        result["request_timeout_seconds"] = main.RequestTimeoutSeconds;
        result["fallback_request_timeout_seconds"] = sub?.RequestTimeoutSeconds ?? main.RequestTimeoutSeconds;
        result["api_key"] = main.ApiKey;
        result["model"] = main.Model;
        result["fallback_api_key"] = sub?.ApiKey ?? "";
        result["fallback_model"] = sub?.Model ?? "";
        return result;
    }

    private static JObject Shared(JObject source) => new(source.Properties()
        .Where(p => p.Name is "request_timeout_seconds" or "consultation_timeout_seconds" or "max_output_tokens" or "log_ai_json")
        .Select(p => new JProperty(p.Name, p.Value.DeepClone())));
    private static AdvisorConnectionException Invalid() => new("settings_invalid");
}

internal sealed class AdvisorApiConfigurationDraft
{
    internal string Id { get; }
    internal string Name { get; set; }
    internal string Provider { get; set; }
    internal int RequestTimeoutSeconds { get; set; }
    internal JObject Settings { get; set; }
    internal AdvisorSituationDelivery SituationDelivery { get; set; }
    internal string ApiKey { get => (string?)Settings["api_key"] ?? ""; set => Settings["api_key"] = value.Trim(); }
    internal string Model { get => (string?)Settings["model"] ?? ""; set => Settings["model"] = value.Trim(); }
    internal AdvisorApiConfigurationDraft(AdvisorApiConfiguration value)
    { Id = value.Id; Name = value.Name; Provider = value.Provider; Settings = (JObject)value.Settings.DeepClone(); RequestTimeoutSeconds = value.RequestTimeoutSeconds; SituationDelivery = value.SituationDelivery; }
    internal AdvisorApiConfiguration Build() => new(Id, Name, Provider, Settings, RequestTimeoutSeconds, SituationDelivery);
}
