using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Only stable connection IDs are persisted here; credentials stay in the existing connection library.
internal sealed class AdvisorLocalConnections
{
    internal static readonly AdvisorLocalConnections Empty = new(Array.Empty<string>(), null);
    internal IReadOnlyList<string> AllowedIds { get; }
    internal string? DefaultId { get; }
    internal AdvisorLocalConnections(IEnumerable<string> allowedIds, string? defaultId)
    {
        var ids = allowedIds.ToArray();
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 80 || id.Any(char.IsControl)) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            (defaultId != null && !ids.Contains(defaultId))) throw new AdvisorOptionsException("options_invalid");
        AllowedIds = Array.AsReadOnly(ids); DefaultId = defaultId;
    }
    internal JObject Json() => new() { ["allowed_ids"] = new JArray(AllowedIds), ["default_id"] = DefaultId };
    internal static AdvisorLocalConnections Parse(JToken? token)
    {
        if (token == null) return Empty;
        if (token is not JObject value || value.Properties().Any(p => p.Name is not ("allowed_ids" or "default_id")) ||
            value["allowed_ids"] is not JArray ids || ids.Any(id => id.Type != JTokenType.String) ||
            value["default_id"] == null || value["default_id"]!.Type is not (JTokenType.String or JTokenType.Null))
            throw new AdvisorOptionsException("options_invalid");
        return new AdvisorLocalConnections(ids.Values<string>()!, (string?)value["default_id"]);
    }
    internal JObject Describe(AdvisorSettingsDraft settings)
    {
        var available = settings.Configurations.Where(c => AllowedIds.Contains(c.Id)).ToArray();
        return new JObject
        {
            ["default_connection_id"] = available.Any(c => c.Id == DefaultId) ? DefaultId : null,
            ["connections"] = new JArray(available.Select(c => new JObject
            { ["id"] = c.Id, ["name"] = c.Name, ["provider"] = c.Provider, ["model"] = c.Model }))
        };
    }
    internal void Select(AdvisorSettingsDraft settings, string id, bool allowFallback)
    {
        if (!AllowedIds.Contains(id) || settings.Find(id) == null)
            throw new AdvisorLocalApiException(403, "connection_not_allowed");
        var sub = settings.SubId;
        settings.MainId = id;
        settings.SubId = allowFallback && sub != id && sub != null && AllowedIds.Contains(sub) ? sub : null;
    }
}
