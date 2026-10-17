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
    internal string ApiKey { get; }
    internal string Model { get; }
    internal AdvisorApiConfiguration(string id, string name, string key, string model)
    { Id = id; Name = name.Trim(); ApiKey = key.Trim(); Model = model.Trim(); }
    internal JObject Json() => new() { ["id"] = Id, ["name"] = Name, ["api_key"] = ApiKey, ["model"] = Model };
}

internal sealed class AdvisorApiLibrary
{
    internal const int SchemaVersion = 2;
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
        string Name(int number) => string.Format(CultureInfo.InvariantCulture, namePattern, number);
        if (source["schema_version"] == null)
        {
            if (new[] { "api_configurations", "main_api_id", "sub_api_id" }.Any(k => source[k] != null)) throw Invalid();
            var main = new AdvisorApiConfiguration("api-1", Name(1), String("api_key", ""), String("model", AdvisorSettings.DefaultModel));
            var key = String("fallback_api_key", "");
            var model = String("fallback_model", main.Model);
            var configurations = new List<AdvisorApiConfiguration>();
            if (main.ApiKey.Length > 0 && main.Model.Length > 0) configurations.Add(main);
            var sub = new AdvisorApiConfiguration("api-2", Name(2), key, model);
            // Missing fields are unconfigured, including legacy model-only sub settings.
            if (sub.ApiKey.Length > 0 && sub.Model.Length > 0) configurations.Add(sub);
            return new AdvisorApiLibrary(configurations, configurations.Contains(main) ? main.Id : null,
                configurations.Contains(sub) ? sub.Id : null);
        }
        if (source["schema_version"]!.Type != JTokenType.Integer || source["schema_version"]!.ToString(Newtonsoft.Json.Formatting.None) != "2" ||
            source["api_configurations"] is not JArray entries || source["main_api_id"] == null || source["main_api_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            source["sub_api_id"] == null || source["sub_api_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            new[] { "api_key", "fallback_api_key", "model", "fallback_model" }.Any(k => source[k] != null)) throw Invalid();
        var items = new List<AdvisorApiConfiguration>();
        foreach (var entry in entries)
        {
            var fields = new[] { "id", "name", "api_key", "model" };
            if (entry is not JObject item || item.Properties().Any(p => !fields.Contains(p.Name)) ||
                fields.Any(f => item[f]?.Type != JTokenType.String)) throw Invalid();
            items.Add(new AdvisorApiConfiguration((string)item["id"]!, (string)item["name"]!, (string)item["api_key"]!, (string)item["model"]!));
        }
        var result = new AdvisorApiLibrary(items, (string?)source["main_api_id"], (string?)source["sub_api_id"]);
        result.ValidateAssignments();
        return result;
    }

    internal void ValidateAssignments()
    {
        if (Configurations.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 80 || c.Id.Any(char.IsControl)) ||
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
    internal string ApiKey { get; set; }
    internal string Model { get; set; }
    internal AdvisorApiConfigurationDraft(AdvisorApiConfiguration value)
    { Id = value.Id; Name = value.Name; ApiKey = value.ApiKey; Model = value.Model; }
    internal AdvisorApiConfiguration Build() => new(Id, Name, ApiKey, Model);
}
