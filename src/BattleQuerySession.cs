using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

// Queries operate exclusively on the detached player view captured at consultation start.
internal sealed class BattleQuerySession : IAdvisorQuerySession
{
    private static readonly string[] names = { "catalog", "list_units", "get_units", "list_contacts", "get_briefing", "list_reports", "get_reports" };
    private readonly JObject snapshot;
    private readonly JObject initial;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int RoundsUsed { get; private set; }
    public int MaxRounds { get; }
    private int returnedBytes;

    internal BattleQuerySession(BattleSnapshot situation, string language, string? question, int maxRounds = DefaultMaxRounds)
    {
        if (maxRounds < 1 || maxRounds > MaximumRounds) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        MaxRounds = maxRounds;
        snapshot = situation.Copy();
        initial = AdvisorInputBuilder.Header(language, question);
        initial["schema_version"] = 3;
        initial["mode"] = "tactical_battle";
        initial["interaction_mode"] = "on_demand";
        initial["consultation_id"] = Id;
        initial["instructions"]!["evidence_policy"] =
            "Use only this consultation's player-known battle evidence. Never fill gaps using real-world history, typical armaments, geography-based guesses, or prior adviser assertions. " +
            "Prior advice is not evidence; explicitly correct unsupported earlier recommendations. Query relevant current unit details, public briefing and published reports before proposing concrete actions. " +
            "Public objectives and their displayed status are the only disclosed victory conditions. Hidden scoring rules, enemy objectives, future events and actual enemy state are unavailable. " +
            "Contact positions are sensor estimates or extrapolations, not exact enemy positions. A missing position, observation time or error bound is unknown. " +
            "Do not infer freshness from captured_at_utc; it is capture time, not detection time. Unknown posture is not confirmed hostile. " +
            "The prototype has no ammunition, damage, fuel, aircraft readiness, range or detailed capabilities. Names alone prove none of these. Do not invent aircraft, weapons or specific attack feasibility. " +
            "When giving advice, provide useful priorities and next checks; make unsupported operational options explicitly conditional. Preserve supplied display names and never print internal IDs or tool syntax.";
        initial["instructions"]!["retrieval"] = "Use query_situation for relevant evidence. Known IDs can be queried directly; batch independent requests. Answer using gathered facts when the retrieval budget ends.";
        initial["situation"] = new JObject
        {
            ["captured_at_utc"] = snapshot["captured_at_utc"]!.DeepClone(),
            ["mission_display_name"] = snapshot["mission_display_name"]!.DeepClone(),
            ["player_side"] = "player",
            ["victory_conditions"] = new JObject
            {
                ["source"] = "public_mission_objectives",
                ["objectives"] = snapshot["public_objectives"]!.DeepClone(),
                ["hidden_conditions_available"] = false
            },
            ["overview"] = new JObject
            {
                ["friendly_unit_count"] = Rows("friendly_units").Count,
                ["observed_contact_count"] = Rows("observed_contacts").Count,
                ["retained_report_count"] = Rows("published_reports").Count
            },
            ["coverage"] = new JObject
            {
                ["available_on_request"] = new JArray("friendly_units", "observed_contacts", "mission_briefing", "published_reports"),
                ["not_included"] = new JArray("ammunition", "damage", "fuel", "aircraft_readiness", "detailed_weapon_capabilities",
                    "contact_observation_times", "contact_error_bounds", "bearing_only_track_geometry", "unlaunched_aircraft", "hidden_triggers"),
                ["reference_scope"] = "this_consultation",
                ["report_limit"] = 10
            }
        };
        initial["knowledge_catalog"] = Catalogue();
        initial["query_tool"] = Tool();
        initial["query_limits"] = new JObject
        {
            ["max_rounds"] = MaxRounds, ["max_queries_per_round"] = MaxQueriesPerRound, ["max_ids_per_query"] = MaxIdsPerQuery,
            ["default_page_size"] = DefaultPageSize, ["max_page_size"] = MaxPageSize,
            ["max_request_utf8_bytes"] = MaxRequestBytes, ["max_response_utf8_bytes"] = MaxResponseBytes, ["max_total_data_utf8_bytes"] = MaxTotalBytes
        };
        returnedBytes = Bytes(initial);
        if (returnedBytes > MaxTotalBytes) throw new InvalidOperationException("Mandatory battle input exceeds the consultation payload limit.");
    }

    public JObject InitialInput() => (JObject)initial.DeepClone();

    public JObject Execute(JObject request)
    {
        try
        {
            if (RoundsUsed >= MaxRounds) throw Error("round_limit", "No query rounds remain.");
            RoundsUsed++;
            Allowed(request, "consultation_id", "requests");
            if ((string?)request["consultation_id"] != Id) throw Error("wrong_consultation", "Use the current consultation.");
            if (Bytes(request) > MaxRequestBytes) throw Error("request_too_large", "Reduce query arguments.");
            if (!(request["requests"] is JArray batch) || batch.Count < 1 || batch.Count > MaxQueriesPerRound)
                throw Error("invalid_arguments", "Supply a bounded, nonempty requests array.");
            var results = new JArray();
            foreach (var item in batch)
            {
                if (!(item is JObject query) || query["query"]?.Type != JTokenType.String)
                    throw Error("invalid_arguments", "Each query needs a query name.");
                results.Add(new JObject { ["query"] = query["query"]!.DeepClone(), ["data"] = Dispatch(query) });
            }
            var response = Envelope("ok");
            response["results"] = results;
            var bytes = Bytes(response);
            if (bytes > MaxResponseBytes) throw Error("response_too_large", "Request smaller pages or fewer IDs.");
            if (returnedBytes + bytes > MaxTotalBytes) throw Error("payload_limit", "The consultation payload budget is exhausted.");
            returnedBytes += bytes;
            return response;
        }
        catch (AdvisorQueryException error)
        {
            var response = Envelope("error");
            response["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message };
            return response;
        }
    }

    private JToken Dispatch(JObject query)
    {
        var name = (string)query["query"]!;
        switch (name)
        {
            case "catalog":
                Allowed(query, "query"); return Catalogue();
            case "get_briefing":
                Allowed(query, "query"); return new JObject { ["text"] = snapshot["mission_briefing"]!.DeepClone() };
            case "list_units":
                Allowed(query, "query", "offset", "limit");
                return Page(query, new JArray(Rows("friendly_units").OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"]!.DeepClone(), ["display_name"] = r["display_name"]!.DeepClone(), ["kind"] = r["kind"]!.DeepClone() })));
            case "list_contacts":
                Allowed(query, "query", "offset", "limit"); return Page(query, Rows("observed_contacts"));
            case "list_reports":
                Allowed(query, "query", "offset", "limit");
                return Page(query, new JArray(Rows("published_reports").OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"]!.DeepClone(), ["published_at_utc"] = r["published_at_utc"]!.DeepClone() })));
            case "get_units":
            case "get_reports":
                Allowed(query, "query", "ids");
                if (!(query["ids"] is JArray ids) || ids.Count < 1 || ids.Count > MaxIdsPerQuery ||
                    ids.Any(i => i.Type != JTokenType.String) || ids.Values<string>().Distinct(StringComparer.Ordinal).Count() != ids.Count)
                    throw Error("invalid_arguments", "Supply a bounded list of unique IDs.");
                var rows = Rows(name == "get_units" ? "friendly_units" : "published_reports");
                var found = new JArray();
                foreach (var id in ids)
                {
                    var row = rows.OfType<JObject>().SingleOrDefault(r => (string?)r["id"] == (string?)id)
                        ?? throw Error("unknown_id", "An ID is unavailable for this query in this consultation.");
                    found.Add(row.DeepClone());
                }
                return found;
            default: throw Error("unknown_query", "Use a query from the current catalogue.");
        }
    }

    private JArray Rows(string name) => (JArray)snapshot[name]!;
    private JObject Catalogue() => new()
    {
        ["friendly_units"] = new JObject { ["item_count"] = Rows("friendly_units").Count, ["list_query"] = "list_units", ["detail_query"] = "get_units", ["description"] = "Own on-map units: displayed names, kind, position, heading and speed only." },
        ["observed_contacts"] = new JObject { ["item_count"] = Rows("observed_contacts").Count, ["list_query"] = "list_contacts", ["description"] = "Player-map contacts, including unknown or neutral posture. Sensor estimates may be stale; no actual enemy state." },
        ["mission_briefing"] = new JObject { ["query"] = "get_briefing", ["description"] = "The player's currently displayed mission briefing." },
        ["published_reports"] = new JObject { ["item_count"] = Rows("published_reports").Count, ["list_query"] = "list_reports", ["detail_query"] = "get_reports", ["description"] = "Up to 10 already published intelligence reports, oldest to newest." }
    };

    private JObject Envelope(string status) => new()
    {
        ["consultation_id"] = Id, ["status"] = status, ["round"] = RoundsUsed, ["remaining_rounds"] = Math.Max(0, MaxRounds - RoundsUsed)
    };
    private static JObject Page(JObject query, JArray rows)
    {
        var offset = Integer(query, "offset", 0, 0, int.MaxValue);
        var limit = Integer(query, "limit", DefaultPageSize, 1, MaxPageSize);
        var items = rows.Skip(offset).Take(limit).Select(r => r.DeepClone()).ToArray();
        var more = offset < rows.Count - items.Length;
        return new JObject { ["items"] = new JArray(items), ["total_count"] = rows.Count, ["offset"] = offset,
            ["has_more"] = more, ["next_offset"] = more ? new JValue(offset + items.Length) : JValue.CreateNull() };
    }
    private static int Integer(JObject query, string key, int fallback, int min, int max)
    {
        if (query[key] == null) return fallback;
        if (query[key]!.Type != JTokenType.Integer || !int.TryParse(query[key]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw Error("invalid_arguments", "An offset or limit is outside its allowed range.");
        return value;
    }
    private static void Allowed(JObject query, params string[] keys)
    {
        if (query.Properties().Any(p => !keys.Contains(p.Name))) throw Error("invalid_arguments", "Unsupported argument.");
    }
    private static AdvisorQueryException Error(string code, string message) => new(code, message);
    private static JObject Tool() => new()
    {
        ["name"] = "query_situation",
        ["description"] = "Read only this consultation's player-known battle snapshot. The host selects the consultation; supply requests only. Batch independent queries, use get_* for known IDs, and respect pagination.",
        ["parameters"] = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("requests"),
            ["properties"] = new JObject
            {
                ["requests"] = new JObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxQueriesPerRound,
                    ["items"] = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("query"),
                        ["properties"] = new JObject
                        {
                            ["query"] = new JObject { ["type"] = "string", ["enum"] = new JArray(names) },
                            ["ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = "get_units or get_reports only: IDs already supplied in this consultation." },
                            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Zero-based offset for list queries only." },
                            ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxPageSize, ["description"] = "Page size for list queries only." }
                        }
                    }
                }
            }
        }
    };
}
