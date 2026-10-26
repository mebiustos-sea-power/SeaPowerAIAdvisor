using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Developer permissions and stable connection IDs live here; credentials stay in the connection library.
internal sealed class AdvisorLocalConnections
{
    internal static readonly AdvisorLocalConnections Empty = new(Array.Empty<string>(), null);
    internal IReadOnlyList<string> AllowedIds { get; }
    internal string? DefaultId { get; }
    internal bool AllowModelOverride { get; }
    internal AdvisorLocalConnections(IEnumerable<string> allowedIds, string? defaultId, bool allowModelOverride = false)
    {
        var ids = allowedIds.ToArray();
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 80 || id.Any(char.IsControl)) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            (defaultId != null && !ids.Contains(defaultId))) throw new AdvisorOptionsException("options_invalid");
        AllowedIds = Array.AsReadOnly(ids); DefaultId = defaultId; AllowModelOverride = allowModelOverride;
    }
    internal JObject Json() => new() { ["allowed_ids"] = new JArray(AllowedIds), ["default_id"] = DefaultId, ["allow_model_override"] = AllowModelOverride };
    internal static AdvisorLocalConnections Parse(JToken? token)
    {
        if (token == null) return Empty;
        if (token is not JObject value || value.Properties().Any(p => p.Name is not ("allowed_ids" or "default_id" or "allow_model_override")) ||
            value["allowed_ids"] is not JArray ids || ids.Any(id => id.Type != JTokenType.String) ||
            value["default_id"] == null || value["default_id"]!.Type is not (JTokenType.String or JTokenType.Null) ||
            (value["allow_model_override"] != null && value["allow_model_override"]!.Type != JTokenType.Boolean))
            throw new AdvisorOptionsException("options_invalid");
        return new AdvisorLocalConnections(ids.Values<string>()!, (string?)value["default_id"], (bool?)value["allow_model_override"] ?? false);
    }
    internal JObject Describe(AdvisorSettingsDraft settings)
    {
        var available = settings.ConfigurationsByName.Where(c => AllowedIds.Contains(c.Id)).ToArray();
        return new JObject
        {
            ["allow_model_override"] = AllowModelOverride,
            ["default_connection_id"] = available.Any(c => c.Id == DefaultId) ? DefaultId : null,
            ["connections"] = new JArray(available.Select(c => new JObject
            { ["id"] = c.Id, ["name"] = c.Name, ["provider"] = c.Provider, ["model"] = c.Model,
                ["situation_delivery"] = AdvisorServiceCatalog.BuiltIn.Find(c.Provider)?.Independent == true ? null : c.SituationDelivery.Code() }))
        };
    }
    internal void Select(AdvisorSettingsDraft settings, string id, bool allowFallback, string? model = null, string? prompt = null)
    {
        var selected = settings.Find(id);
        if (!AllowedIds.Contains(id) || selected == null)
            throw new AdvisorLocalApiException(403, "connection_not_allowed");
        if (model != null)
        {
            if (!AllowModelOverride) throw new AdvisorLocalApiException(403, "model_override_not_allowed");
            if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
                throw new AdvisorLocalApiException(400, "invalid_model");
            var service = AdvisorServiceCatalog.BuiltIn.Require(selected.Provider);
            if (!service.Fields.Any(f => f.Key == "model" && !f.Secret))
                throw new AdvisorLocalApiException(400, "model_override_unavailable");
            // The caller supplies a detached consultation draft, never a saved settings editor.
            selected.Model = model;
        }
        if (prompt != null) selected.Prompt = prompt;
        var sub = settings.SubId;
        settings.MainId = id;
        settings.SubId = allowFallback && sub != id && sub != null && AllowedIds.Contains(sub) ? sub : null;
    }
}
