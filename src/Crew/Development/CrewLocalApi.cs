using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// This adapter is called by the MOD's existing main-thread queue. It never sends its own AI requests.
internal sealed class CrewLocalApi
{
    internal const string Path = "/v1/crew/reports";
    internal string ScopeId { get; } = Guid.NewGuid().ToString("N");
    private readonly CrewReports reports;
    private readonly Func<double> now;
    private readonly Action validateConnection;
    internal CrewLocalApi(CrewReports reports, Func<double> now, Action validateConnection)
    { this.reports = reports; this.now = now; this.validateConnection = validateConnection; }
    internal JObject Status() => new()
    {
        ["scope_id"] = ScopeId, ["busy"] = reports.Busy, ["error"] = reports.Error,
        ["targets"] = new JArray(reports.Targets.Select(t => new JObject
        {
            ["id"] = t.Id, ["display_name"] = t.Name, ["nation"] = t.Nation, ["role"] = t.Role,
            ["member_unit_ids"] = new JArray(t.Members), ["enabled"] = reports.Enabled(t.Id),
            ["person"] = reports.Person(t.Id) is CrewPerson person ? new JObject { ["name"] = person.Name, ["personality"] = person.Personality } : JValue.CreateNull()
        })),
        ["history"] = new JArray(reports.History.Select(r => new JObject
        {
            ["target_id"] = r.Target.Id, ["display_name"] = r.Target.Name, ["person_name"] = r.Submission.Person.Name,
            ["personality"] = r.Submission.Person.Personality, ["text"] = r.Submission.Text,
            ["received_at"] = r.ReceivedAt.ToString("o", CultureInfo.InvariantCulture)
        }))
    };
    internal LocalApiResponse Dispatch(string method, string path, JObject body)
    {
        if (method == "PATCH" && path == Path + "/targets")
        {
            RequireScope(body, "target_ids", "enabled");
            if (body["enabled"]?.Type != JTokenType.Boolean || body["target_ids"] is not JArray ids || ids.Count is < 1 or > 64 || ids.Any(t => t.Type != JTokenType.String))
                throw new LocalApiException(400, "invalid_request");
            var selected = ids.Values<string>().ToArray();
            if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length || selected.Any(id => !reports.Targets.Any(t => t.Id == id)))
                throw new LocalApiException(400, "unknown_report_target");
            var enabled = (bool)body["enabled"]!;
            if (enabled) validateConnection();
            foreach (var id in selected) reports.SetEnabled(id!, enabled, now());
            return new LocalApiResponse(200, Status());
        }
        if (method == "POST" && path == Path + "/stop")
        {
            RequireScope(body); reports.Suspend(); return new LocalApiResponse(200, Status());
        }
        return LocalApiResponse.Error(404, "not_found");
    }
    internal void RequireScope(JObject body, params string[] fields)
    {
        if (body.Properties().Any(p => p.Name != "scope_id" && !fields.Contains(p.Name)) || body["scope_id"]?.Type != JTokenType.String)
            throw new LocalApiException(400, "invalid_request");
        if ((string?)body["scope_id"] != ScopeId) throw new LocalApiException(409, "stale_reports_scope");
    }
}
