using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// This adapter is called by the MOD's existing main-thread queue. It never sends its own AI requests.
internal sealed class CrewLocalApi
{
    internal const string Path = "/v1/crew/chatter";
    internal string ScopeId { get; } = Guid.NewGuid().ToString("N");
    private readonly CrewChatter chatter;
    private readonly Func<double> now;
    private readonly Action validateConnection;
    internal CrewLocalApi(CrewChatter chatter, Func<double> now, Action validateConnection)
    { this.chatter = chatter; this.now = now; this.validateConnection = validateConnection; }
    internal JObject Status()
    {
        var usage = chatter.Usage.Snapshot;
        return new JObject
        {
            ["usage"] = new JObject { ["api_requests"] = usage.ApiRequests, ["known_total_tokens"] = usage.KnownTokens,
                ["unknown_requests"] = usage.UnknownRequests, ["last_total_tokens"] = usage.LastTokens },
            ["scope_id"] = ScopeId, ["busy"] = chatter.Busy, ["paused"] = chatter.Paused, ["error"] = chatter.Error,
            ["targets"] = new JArray(chatter.Targets.Select(t => new JObject
            {
                ["id"] = t.Id, ["display_name"] = t.Name, ["nation"] = t.Nation, ["role"] = t.Role,
                ["member_unit_ids"] = new JArray(t.Members), ["enabled"] = chatter.Enabled(t.Id), ["can_speak"] = t.CanSpeak,
                ["person"] = chatter.Person(t.Id) is CrewPerson person ? new JObject { ["name"] = person.Name, ["personality"] = person.Personality } : JValue.CreateNull()
            })),
            ["history"] = new JArray(chatter.History.Select(r => new JObject
            {
                ["target_id"] = r.Target.Id, ["display_name"] = r.Target.Name, ["person_name"] = r.Submission.Person.Name,
                ["personality"] = r.Submission.Person.Personality, ["text"] = r.Submission.Text,
                ["received_at"] = r.ReceivedAt.ToString("o", CultureInfo.InvariantCulture)
            }))
        };
    }
    internal LocalApiResponse Dispatch(string method, string path, JObject body)
    {
        if (method == "PATCH" && path == Path + "/targets")
        {
            RequireScope(body, "target_ids", "enabled");
            if (body["enabled"]?.Type != JTokenType.Boolean || body["target_ids"] is not JArray ids || ids.Count is < 1 or > 64 || ids.Any(t => t.Type != JTokenType.String))
                throw new LocalApiException(400, "invalid_request");
            var selected = ids.Values<string>().ToArray();
            if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length || selected.Any(id => !chatter.Targets.Any(t => t.Id == id)))
                throw new LocalApiException(400, "unknown_chatter_target");
            var enabled = (bool)body["enabled"]!;
            if (enabled) validateConnection();
            var committed = new System.Collections.Generic.HashSet<string>(chatter.Targets.Where(t => chatter.Enabled(t.Id)).Select(t => t.Id), StringComparer.Ordinal);
            foreach (var id in selected) { if (enabled) committed.Add(id!); else committed.Remove(id!); }
            chatter.ApplyTargets(committed, now());
            return new LocalApiResponse(200, Status());
        }
        if (method == "POST" && path == Path + "/pause")
        {
            RequireScope(body, "paused");
            if (body["paused"]?.Type != JTokenType.Boolean) throw new LocalApiException(400, "invalid_request");
            var paused = (bool)body["paused"]!;
            if (!paused && chatter.HasEnabledTargets) validateConnection();
            chatter.SetPaused(paused, now()); return new LocalApiResponse(200, Status());
        }
        if (method == "POST" && path == Path + "/stop")
        {
            RequireScope(body); chatter.Suspend(); return new LocalApiResponse(200, Status());
        }
        return LocalApiResponse.Error(404, "not_found");
    }
    internal void RequireScope(JObject body, params string[] fields)
    {
        if (body.Properties().Any(p => p.Name != "scope_id" && !fields.Contains(p.Name)) || body["scope_id"]?.Type != JTokenType.String)
            throw new LocalApiException(400, "invalid_request");
        if ((string?)body["scope_id"] != ScopeId) throw new LocalApiException(409, "stale_chatter_scope");
    }
}
