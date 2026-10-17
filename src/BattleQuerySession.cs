using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

// Queries operate exclusively on the detached player view captured at consultation start.
internal sealed class BattleQuerySession : IAdvisorQuerySession
{
    private static readonly string[] names = { "catalog", "list_units", "get_units", "list_contacts", "get_briefing", "list_reports", "get_reports", "get_unit_capabilities", "get_contact_threats", "list_ground_units", "get_ground_units", "list_bases", "get_bases", "list_air_units", "get_air_units", "list_air_tasks" };
    private readonly JObject snapshot;
    private readonly AdvisorInformation information;
    private readonly JObject initial;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int RoundsUsed { get; private set; }
    public int MaxRounds { get; }
    public AdvisorPrompts Prompts { get; }
    private int returnedBytes;

    internal BattleQuerySession(BattleSnapshot situation, string language, string? question, int maxRounds = DefaultMaxRounds, AdvisorInformation? information = null, AdvisorPrompts? prompts = null)
    {
        if (maxRounds < 1 || maxRounds > MaximumRounds) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        MaxRounds = maxRounds;
        Prompts = prompts ?? AdvisorPrompts.Current;
        this.information = information ?? AdvisorInformation.Default;
        information = this.information;
        snapshot = situation.Copy();
        BattleInformation.Apply(snapshot, information);
        initial = AdvisorInputBuilder.Header(language, question, Prompts, true);
        initial["schema_version"] = 4;
        initial["mode"] = "tactical_battle";
        initial["interaction_mode"] = "on_demand";
        initial["consultation_id"] = Id;
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
                ["retained_report_count"] = Rows("published_reports").Count,
                ["friendly_ground_unit_count"] = GroundUnits().Count,
                ["friendly_base_count"] = Rows("friendly_bases").Count,
                ["hosted_air_unit_count"] = Rows("hosted_air_units").Count
            },
            ["coverage"] = new JObject
            {
                ["available_on_request"] = new JArray("friendly_units", "unit_capabilities", "observed_contacts", "public_class_threat_references", "mission_briefing", "published_reports"),
                ["not_included"] = new JArray("current_attack_feasibility", "terrain",
                    "contact_observation_times", "contact_error_bounds", "bearing_only_track_geometry", "hidden_triggers"),
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
        var overview = (JObject)initial["situation"]!["overview"]!;
        if (!information.Allows(InformationKind.BattleUnits)) overview.Remove("friendly_unit_count");
        if (!information.Allows(InformationKind.BattleGroundUnits)) overview.Remove("friendly_ground_unit_count");
        if (!information.Allows(InformationKind.BattleBases)) overview.Remove("friendly_base_count");
        if (!information.Allows(InformationKind.BattleAviation)) overview.Remove("hosted_air_unit_count");
        if (!information.Allows(InformationKind.BattleContacts)) overview.Remove("observed_contact_count");
        if (!information.Allows(InformationKind.BattleReports)) overview.Remove("retained_report_count");
        initial["situation"]!["coverage"]!["available_on_request"] = new JArray(Catalogue().Properties().Select(p => p.Name));
        information.AddCoverage(initial, true);
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
        if (!information.BattleQuery(name)) throw Error("information_disabled", "This data category is disabled by the user. Do not request it through another query.");
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
            case "list_ground_units":
                Allowed(query, "query", "offset", "limit"); return Page(query, GroundUnits());
            case "get_ground_units":
                Allowed(query, "query", "ids"); return SelectRows(query, GroundUnits());
            case "list_bases":
                Allowed(query, "query", "offset", "limit"); return Page(query, Rows("friendly_bases"));
            case "get_bases":
                Allowed(query, "query", "ids"); return SelectRows(query, "friendly_bases");
            case "list_air_units":
                Allowed(query, "query", "offset", "limit", "host_ids");
                var airUnits = Rows("hosted_air_units").OfType<JObject>();
                if (query["host_ids"] != null)
                {
                    var hostIds = SelectRows(new JObject { ["ids"] = query["host_ids"]!.DeepClone() }, "friendly_units").Select(r => (string)r["id"]!).ToHashSet(StringComparer.Ordinal);
                    airUnits = airUnits.Where(r => hostIds.Contains((string)r["host_unit_id"]!));
                }
                return Page(query, new JArray(airUnits.Select(r => new JObject
                {
                    ["id"] = r["id"]!.DeepClone(), ["display_name"] = r["display_name"]!.DeepClone(),
                    ["kind"] = r["kind"]!.DeepClone(), ["host_unit_id"] = r["host_unit_id"]!.DeepClone(),
                    ["host_display_name"] = r["host_display_name"]!.DeepClone(),
                    ["unassigned_aircraft_count"] = r["unassigned_aircraft_count"]!.DeepClone()
                })));
            case "get_air_units":
                Allowed(query, "query", "ids"); return AirUnits(query);
            case "list_air_tasks":
                Allowed(query, "query", "offset", "limit", "host_ids", "air_unit_ids"); return AirTasks(query);
            case "list_contacts":
                Allowed(query, "query", "offset", "limit", "kind");
                var contacts = (JArray)Rows("observed_contacts").DeepClone();
                if (!information.Allows(InformationKind.BattleThreats))
                    foreach (var contact in contacts.OfType<JObject>()) contact.Remove("public_class_reference_available");
                if (query["kind"] != null)
                {
                    if (query["kind"]!.Type != JTokenType.String || !contactKinds.Contains((string)query["kind"]!))
                        throw Error("invalid_arguments", "Use a displayed contact-kind code from the tool schema.");
                    contacts = new JArray(contacts.OfType<JObject>().Where(c => (string?)c["kind"] == (string)query["kind"]!).Select(c => c.DeepClone()));
                }
                return Page(query, contacts);
            case "list_reports":
                Allowed(query, "query", "offset", "limit");
                return Page(query, new JArray(Rows("published_reports").OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"]!.DeepClone(), ["published_at_utc"] = r["published_at_utc"]!.DeepClone() })));
            case "get_unit_capabilities":
                Allowed(query, "query", "ids", "sections"); return Capabilities(query);
            case "get_contact_threats":
                Allowed(query, "query", "ids");
                var references = SelectRows(query, "contact_threat_references");
                return Bundle("contacts", references, references.OfType<JObject>()
                    .SelectMany(r => (r["possible_weapon_reference_ids"] as JArray ?? new JArray()).Values<string>()));
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

    private JArray GroundUnits() => new(Rows("friendly_units").OfType<JObject>().Where(u => (string?)u["unit_category"] == "ground_unit").Select(u => u.DeepClone()));
    private JArray SelectRows(JObject query, string collection) => SelectRows(query, Rows(collection));
    private static JArray SelectRows(JObject query, JArray rows)
    {
        if (!(query["ids"] is JArray ids) || ids.Count < 1 || ids.Count > MaxIdsPerQuery ||
            ids.Any(i => i.Type != JTokenType.String) || ids.Values<string>().Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw Error("invalid_arguments", "Supply a bounded list of unique IDs.");
        return new JArray(ids.Select(id => rows.OfType<JObject>().SingleOrDefault(r => (string?)r["id"] == (string?)id)?.DeepClone()
            ?? throw Error("unknown_id", "An ID is unavailable for this query in this consultation.")));
    }

    private JObject AirUnits(JObject query)
    {
        var units = SelectRows(query, "hosted_air_units");
        var hostIds = units.Select(u => (string)u["host_unit_id"]!).ToHashSet(StringComparer.Ordinal);
        var references = units.SelectMany(u => (u["loadouts"] as JArray ?? new JArray()).SelectMany(l =>
            (l["ammunition"] as JArray ?? new JArray()).Select(a => (string?)a["reference_id"])));
        var result = Bundle("air_units", units, references);
        result["hosts"] = new JArray(Rows("aviation_hosts").OfType<JObject>().Where(h => hostIds.Contains((string)h["host_unit_id"]!)).Select(h =>
        {
            var host = (JObject)h.DeepClone();
            host["displayed_task_count"] = ((JArray)host["tasks"]!).Count;
            host.Remove("tasks");
            return host;
        }));
        return result;
    }

    private JObject AirTasks(JObject query)
    {
        var tasks = Rows("aviation_hosts").OfType<JObject>().SelectMany(h => ((JArray)h["tasks"]!).OfType<JObject>());
        if (query["host_ids"] != null)
        {
            var ids = SelectRows(new JObject { ["ids"] = query["host_ids"]!.DeepClone() }, "friendly_units").Select(h => (string)h["id"]!).ToHashSet(StringComparer.Ordinal);
            tasks = tasks.Where(t => ids.Contains((string)t["host_unit_id"]!));
        }
        if (query["air_unit_ids"] != null)
        {
            var ids = SelectRows(new JObject { ["ids"] = query["air_unit_ids"]!.DeepClone() }, "hosted_air_units").Select(u => (string)u["id"]!).ToHashSet(StringComparer.Ordinal);
            tasks = tasks.Where(t => t["air_unit_id"]!.Type == JTokenType.String && ids.Contains((string)t["air_unit_id"]!));
        }
        return Page(query, new JArray(tasks.Select(t => t.DeepClone())));
    }

    private JObject Capabilities(JObject query)
    {
        var supported = new[] { "fuel", "damage", "weapons" };
        var sections = information.BattleSections;
        if (query["sections"] != null)
        {
            if (!(query["sections"] is JArray selected) || selected.Count < 1 || selected.Count > 3 ||
                selected.Any(s => s.Type != JTokenType.String || !supported.Contains((string)s!)) ||
                selected.Values<string>().Distinct(StringComparer.Ordinal).Count() != selected.Count)
                throw Error("invalid_arguments", "Sections must be unique fuel, damage or weapons values.");
            if (selected.Values<string>().Any(s => !sections.Contains(s)))
                throw Error("information_disabled", "One or more capability sections are disabled by the user.");
            sections = selected.Values<string>().ToArray()!;
        }
        var rows = new JArray();
        var references = new System.Collections.Generic.List<string>();
        foreach (var original in SelectRows(query, "unit_capabilities").OfType<JObject>())
        {
            var unit = new JObject { ["id"] = original["id"]!.DeepClone(), ["display_name"] = original["display_name"]!.DeepClone() };
            foreach (var section in sections) unit[section] = original[section]!.DeepClone();
            if (sections.Contains("weapons"))
                references.AddRange(((JArray)original["weapons"]!["ammunition"]!).OfType<JObject>().Select(w => (string)w["reference_id"]!));
            rows.Add(unit);
        }
        return Bundle("units", rows, references);
    }

    private JObject Bundle(string name, JArray rows, System.Collections.Generic.IEnumerable<string?> references)
    {
        var ids = references.ToHashSet(StringComparer.Ordinal);
        return new JObject
        {
            [name] = rows,
            ["weapon_references"] = new JArray(Rows("weapon_references").OfType<JObject>()
                .Where(w => ids.Contains((string?)w["id"])).Select(w => w.DeepClone()))
        };
    }

    private JArray Rows(string name) => (JArray)snapshot[name]!;
    private JObject Catalogue()
    {
        var result = RawCatalogue();
        foreach (var property in result.Properties().Where(p => !information.BattleCategory(p.Name)).ToArray()) property.Remove();
        if (result["unit_capabilities"] is JObject capabilities) capabilities["available_sections"] = new JArray(information.BattleSections);
        return result;
    }
    private JObject RawCatalogue() => new()
    {
        ["friendly_units"] = new JObject { ["item_count"] = Rows("friendly_units").Count, ["list_query"] = "list_units", ["detail_query"] = "get_units", ["description"] = "Own on-map units, including user-enabled ground units and bases. Names, kind, positions, heading and speed; get_unit_capabilities supplies enabled status sections." },
        ["friendly_ground_units"] = new JObject { ["item_count"] = GroundUnits().Count, ["list_query"] = "list_ground_units", ["detail_query"] = "get_ground_units", ["description"] = "Own known ground combat units, SAM/AAA, radar and missile sites. IDs also work with get_unit_capabilities for enabled ammunition/damage sections." },
        ["friendly_bases"] = new JObject { ["item_count"] = Rows("friendly_bases").Count, ["list_query"] = "list_bases", ["detail_query"] = "get_bases", ["description"] = "Own known airbases, ports and installations: map positions and function. For observed enemy/neutral bases use list_contacts with kind. A known base does not reveal hidden aircraft or future spawns." },
        ["hosted_air_units"] = new JObject { ["item_count"] = Rows("hosted_air_units").Count, ["list_query"] = "list_air_units", ["detail_query"] = "get_air_units", ["description"] = "Own known base/carrier/ship aviation: displayed unassigned inventory, squadrons, enabled loadouts and flight-deck tasks. list_air_units accepts host_ids. Hosts omitted by unit-category settings are excluded. Use list_air_tasks for paginated preparation/recovery tasks. Read details before recommending aircraft. Inventory/stock/preparation times do not prove immediate launch feasibility. Host task/status text is preserved, not language-parsed." },
        ["aviation_tasks"] = new JObject { ["item_count"] = Rows("aviation_hosts").Sum(h => ((JArray)h["tasks"]!).Count), ["list_query"] = "list_air_tasks", ["description"] = "Displayed own flight-deck preparation, launch, recovery and cooldown tasks, paginated. Optional host_ids and/or air_unit_ids filter to relevant known hosts/types. Status text is preserved and is not a guarantee of sortie feasibility." },
        ["unit_capabilities"] = new JObject { ["item_count"] = Rows("unit_capabilities").Count, ["query"] = "get_unit_capabilities", ["description"] = "Batch current own-unit status and referenced weapon ranges. Supply current own-unit IDs and only available_sections; omit sections for all enabled sections. References are deduplicated within the result. Counts do not prove weapons are ready to fire." },
        ["public_class_threat_references"] = new JObject { ["query"] = "get_contact_threats", ["description"] = "Batch current contact IDs to obtain public encyclopedia armament candidates and ranges for identified classes. No actual enemy equipment, ammunition, readiness or damage. Use reference evidence for conditional threat estimates, cite names and retain uncertainty." },
        ["observed_contacts"] = new JObject { ["item_count"] = Rows("observed_contacts").Count, ["list_query"] = "list_contacts", ["description"] = "Player-map contacts permitted by user settings, including ground/base symbols and unknown posture. Neutral posture is included only when enabled. Optional kind filters e.g. airbase/port/sam/radar. Sensor estimates may be stale; no actual enemy state." },
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
    private JObject Tool()
    {
        var tool = RawTool();
        var properties = (JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        properties["query"]!["enum"] = new JArray(names.Where(information.BattleQuery));
        if (information.BattleSections.Length == 0) properties.Remove("sections");
        else
        {
            properties["sections"]!["items"]!["enum"] = new JArray(information.BattleSections);
            properties["sections"]!["description"] = "get_unit_capabilities only. Omit for all user-enabled sections; disabled sections cannot be requested.";
        }
        if (!information.Allows(InformationKind.BattleAviation)) { properties.Remove("host_ids"); properties.Remove("air_unit_ids"); }
        if (!information.Allows(InformationKind.BattleContacts)) properties.Remove("kind");
        return tool;
    }
    private static readonly string[] contactKinds = { "air", "helicopter", "surface", "submarine", "airbase", "installation", "mobileunit", "aaa", "mobileaaa", "sam", "radar", "port", "bridge", "missile", "torpedo", "biologic", "invisible", "airdecoy", "subdecoy", "activesonobouy", "passivesonobouy" };
    private static JObject RawTool() => new()
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
                            ["ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = "get_* queries: relevant snapshot-local IDs already supplied in this consultation." },
                            ["host_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = "list_air_units/list_air_tasks only: known own host-unit IDs. Omit for all permitted hosts." },
                            ["air_unit_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = "list_air_tasks only: hosted-air-unit IDs from list_air_units. Omit for all types." },
                            ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray(contactKinds), ["description"] = "list_contacts only: filter by the displayed map-symbol classification. airbase and port identify disclosed base symbols; no hidden locations are queried." },
                            ["sections"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 3, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray("fuel", "damage", "weapons") }, ["description"] = "get_unit_capabilities only. Omit for all three sections." },
                            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Zero-based offset for list queries only." },
                            ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxPageSize, ["description"] = "Page size for list queries only." }
                        }
                    }
                }
            }
        }
    };
}
