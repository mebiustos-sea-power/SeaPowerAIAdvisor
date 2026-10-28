using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;
using static SeaPowerAIAdvisor.Shared.JsonPayload;

namespace SeaPowerAIAdvisor;

// Queries operate exclusively on the detached player view captured at consultation start.
internal sealed partial class BattleQuerySession : IAdvisorQuerySession, IAdvisorUnitReferenceSource, IAdvisorOperationReferenceSource, IAdvisorSnapshotSource
{
    private static readonly AdvisorRequestShape[] requestShapes = {
        new(new[] { "catalog", "get_briefing" }, Array.Empty<string>()),
        new(new[] { "list_units", "list_reports", "list_ground_units", "list_bases", "list_launched_weapons", BattleContactAssessment.Query }, Array.Empty<string>(), "offset", "limit"),
        new(new[] { "get_units", "get_reports", "get_contact_threats", "get_ground_units", "get_bases", "get_air_units", "get_movement_plans" }, new[] { "ids" }),
        new(new[] { "get_unit_capabilities" }, new[] { "ids" }, "sections"),
        new(new[] { "list_contacts" }, Array.Empty<string>(), "offset", "limit", "kind"),
        new(new[] { "list_air_units" }, Array.Empty<string>(), "offset", "limit", "host_ids"),
        new(new[] { "list_air_tasks" }, Array.Empty<string>(), "offset", "limit", "host_ids", "air_unit_ids")
    };
    private readonly JObject snapshot;
    private readonly AdvisorInformation information;
    private readonly JObject initial;
    private readonly BattleContactAssessment? assessment;
    private IEnumerable<string> QueryNames => requestShapes.SelectMany(s => s.Names).Where(name => assessment != null || name != BattleContactAssessment.Query);
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int RoundsUsed { get; private set; }
    public int MaxRounds { get; }
    public AdvisorPrompts Prompts { get; }
    public AdvisorUnitReferences UnitReferences { get; }
    public AdvisorOperationReferences OperationReferences { get; }
    private int returnedBytes;
    private readonly HashSet<string> deliveredWeaponReferences = new(StringComparer.Ordinal);
    private readonly HashSet<string> deliveredSensorReferences = new(StringComparer.Ordinal);
    private readonly HashSet<string> deliveredMovementRoutes = new(StringComparer.Ordinal);

    internal BattleQuerySession(BattleSnapshot situation, string language, string? question, int maxRounds = DefaultMaxRounds, AdvisorInformation? information = null, AdvisorPrompts? prompts = null, JObject? context = null)
    {
        if (maxRounds < 1 || maxRounds > MaximumRounds) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        MaxRounds = maxRounds;
        Prompts = prompts ?? AdvisorPrompts.Current;
        this.information = information ?? AdvisorInformation.Default;
        information = this.information;
        snapshot = situation.Copy();
        snapshot["friendly_launched_weapons"] ??= new JArray();
        snapshot["sensor_references"] ??= new JArray();
        snapshot["movement_plans"] ??= new JArray();
        snapshot["movement_routes"] ??= new JArray();
        BattleInformation.Apply(snapshot, information);
        UnitReferences = situation.UnitReferences.Only(Rows("friendly_units").Select(row => (string)row["id"]!));
        var weaponReferences = information.Allows(InformationKind.BattleWeapons)
            ? new AdvisorWeaponReferences(Rows("unit_capabilities").SelectMany(row => row["weapons"]?["ammunition"] as JArray ?? new JArray())
                .Join(Rows("weapon_references"), ammo => (string?)ammo["reference_id"], reference => (string?)reference["id"],
                    (ammo, reference) => new[] { ((string)reference["id"]!, (string)reference["reference_key"]!, (string)ammo["display_name"]!),
                        ((string)reference["id"]!, (string)reference["reference_key"]!, (string?)reference["display_name"] ?? (string)ammo["display_name"]!) }).SelectMany(rows => rows))
            : AdvisorWeaponReferences.Empty;
        OperationReferences = new AdvisorOperationReferences(UnitReferences, weaponReferences);
        if (context != null) assessment = new BattleContactAssessment(snapshot, context, information);
        initial = AdvisorInputBuilder.Header(language, question, Prompts, true);
        initial["schema_version"] = 5;
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
                ["objectives"] = snapshot["public_objectives"]!.DeepClone()
            },
            ["mission_map_areas"] = snapshot["mission_map_areas"]!.DeepClone(),
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
                    "contact_observation_times", "contact_error_bounds", "bearing_only_track_geometry"),
                ["reference_scope"] = "this_consultation",
                ["report_limit"] = 10
            }
        };
        var mentions = AdvisorUnitMentions.Bind(question ?? string.Empty, situation.UnitReferences,
            Rows("friendly_units").Concat(Rows("observed_contacts")).Concat(Rows("friendly_launched_weapons")).OfType<JObject>());
        if (mentions.Count > 0) initial["referenced_units"] = mentions;
        if (assessment != null) initial["contact_assessment"] = assessment.Focus.DeepClone();
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
        if (information.Allows(InformationKind.BattleWeapons) || information.Allows(InformationKind.BattleThreats))
            initial["situation"]!["coverage"]!["weapon_reference_delivery"] =
                Prompts.Text("battle.json", "weapon_reference_delivery");
        if (information.Allows(InformationKind.BattleSensorPerformance))
            initial["situation"]!["coverage"]!["sensor_reference_delivery"] = Prompts.Text("battle.json", "sensor_reference_delivery");
        returnedBytes = Bytes(initial);
        if (returnedBytes > MaxTotalBytes) throw new InvalidOperationException("Mandatory battle input exceeds the consultation payload limit.");
    }

    public JObject InitialInput() => (JObject)initial.DeepClone();

    public JObject Execute(JObject request)
    {
        try
        {
            if (RoundsUsed >= MaxRounds) throw Error("round_limit", "round_limit");
            RoundsUsed++;
            Allowed(request, "consultation_id", "requests", "partial_results", "response_limit_bytes", "remaining_data_bytes");
            var partial = AdvisorQueryBatch.Partial(request);
            var responseLimit = AdvisorQueryBatch.Budget(request, "response_limit_bytes", MaxResponseBytes);
            var remainingData = AdvisorQueryBatch.Budget(request, "remaining_data_bytes", MaxTotalBytes);
            if ((string?)request["consultation_id"] != Id) throw Error("wrong_consultation", "wrong_consultation");
            if (Bytes(request) > MaxRequestBytes) throw Error("request_too_large", "request_too_large");
            if (!(request["requests"] is JArray batch) || batch.Count < 1 || batch.Count > MaxQueriesPerRound)
                throw Error("invalid_arguments", "requests_invalid");
            var results = AdvisorQueryBatch.Execute(batch, partial, item =>
            {
                if (!(item is JObject query) || query["query"]?.Type != JTokenType.String)
                    throw Error("invalid_arguments", "query_name_invalid");
                return new JObject { ["query"] = query["query"]!.DeepClone(), ["data"] = Dispatch(query) };
            });
            var delivered = DeduplicateReferences(results, "weapon_references", deliveredWeaponReferences);
            var sensorDelivered = DeduplicateReferences(results, "sensor_references", deliveredSensorReferences);
            var movementDelivered = DeduplicateReferences(results, "routes", deliveredMovementRoutes);
            var response = Envelope("ok");
            response["results"] = results;
            var bytes = Bytes(response);
            if (bytes > responseLimit) throw Error("response_too_large", "response_too_large");
            if (returnedBytes + bytes > MaxTotalBytes || bytes > remainingData) throw Error("payload_limit", "payload_limit");
            returnedBytes += bytes;
            deliveredWeaponReferences.UnionWith(delivered);
            deliveredSensorReferences.UnionWith(sensorDelivered);
            deliveredMovementRoutes.UnionWith(movementDelivered);
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
        if (!information.BattleQuery(name)) throw Error("information_disabled", "information_disabled");
        if (requestShapes.Any(s => s.Matches(name)) && !requestShapes.Any(s => s.Matches(name) && s.AllowsFields(query, "query")))
            throw Error("invalid_arguments", "unsupported_argument");
        if (requestShapes.Any(s => s.Matches(name)) && !requestShapes.Any(s => s.Matches(name) && s.HasRequired(query)))
            throw Error("invalid_arguments", "ids_invalid");
        switch (name)
        {
            case "catalog":
                return Catalogue();
            case "get_briefing":
                if ((bool?)snapshot["mission_briefing_unavailable"] == true)
                    throw Error("briefing_unavailable", "briefing_unavailable");
                var briefing = new JObject { ["text"] = snapshot["mission_briefing"]!.DeepClone() };
                if (snapshot["mission_briefing_source"] is JValue source)
                {
                    briefing["source"] = "mission_briefing";
                    if ((string?)source == "base_mission_xml") briefing["note"] = Prompts.Text("battle.json", "base_briefing_note");
                }
                return briefing;
            case "list_units":
                return Page(query, new JArray(Rows("friendly_units").OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"]!.DeepClone(), ["display_name"] = r["display_name"]!.DeepClone(), ["kind"] = r["kind"]!.DeepClone() })));
            case "get_movement_plans":
                var plans = SelectRows(query, "movement_plans");
                var routeIds = plans.Select(p => (string?)p["route_unit_id"]).ToHashSet(StringComparer.Ordinal);
                return new JObject
                {
                    ["units"] = plans,
                    ["routes"] = new JArray(Rows("movement_routes").OfType<JObject>()
                        .Where(r => routeIds.Contains((string)r["id"]!)).Select(r => r.DeepClone()))
                };
            case "list_launched_weapons":
                return Page(query, Rows("friendly_launched_weapons"));
            case "list_ground_units":
                return Page(query, GroundUnits());
            case "get_ground_units":
                return SelectRows(query, GroundUnits());
            case "list_bases":
                return Page(query, Rows("friendly_bases"));
            case "get_bases":
                return SelectRows(query, "friendly_bases");
            case "list_air_units":
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
                return AirUnits(query);
            case "list_air_tasks":
                return AirTasks(query);
            case BattleContactAssessment.Query:
                if (assessment == null) throw Error("unknown_query", "unknown_query");
                return Page(query, assessment.Candidates);
            case "list_contacts":
                var contacts = (JArray)Rows("observed_contacts").DeepClone();
                if (!information.Allows(InformationKind.BattleThreats))
                    foreach (var contact in contacts.OfType<JObject>()) contact.Remove("public_class_reference_available");
                if (query["kind"] != null)
                {
                    if (query["kind"]!.Type != JTokenType.String || !contactKinds.Contains((string)query["kind"]!))
                        throw Error("invalid_arguments", "contact_kind_invalid");
                    if (BattleWeaponReader.IsKind((string)query["kind"]!) && !information.Allows(InformationKind.BattleWeaponContacts))
                        throw Error("information_disabled", "information_disabled");
                    contacts = new JArray(contacts.OfType<JObject>().Where(c => (string?)c["kind"] == (string)query["kind"]!).Select(c => c.DeepClone()));
                }
                return Page(query, contacts);
            case "list_reports":
                return Page(query, new JArray(Rows("published_reports").OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"]!.DeepClone(), ["published_at_utc"] = r["published_at_utc"]!.DeepClone() })));
            case "get_unit_capabilities":
                return Capabilities(query);
            case "get_contact_threats":
                var references = SelectRows(query, "contact_threat_references");
                return Bundle("contacts", references, references.OfType<JObject>()
                    .SelectMany(r => (r["possible_weapon_reference_ids"] as JArray ?? new JArray()).Values<string>()));
            case "get_units":
            case "get_reports":
                if (!(query["ids"] is JArray ids) || ids.Count < 1 || ids.Count > MaxIdsPerQuery ||
                    ids.Any(i => i.Type != JTokenType.String) || ids.Values<string>().Distinct(StringComparer.Ordinal).Count() != ids.Count)
                    throw Error("invalid_arguments", "ids_invalid");
                var rows = Rows(name == "get_units" ? "friendly_units" : "published_reports");
                var found = new JArray();
                foreach (var id in ids)
                {
                    var row = rows.OfType<JObject>().SingleOrDefault(r => (string?)r["id"] == (string?)id)
                        ?? throw Error("unknown_id", "unknown_id");
                    found.Add(row.DeepClone());
                }
                return found;
            default: throw Error("unknown_query", "unknown_query");
        }
    }

    private JArray GroundUnits() => new(Rows("friendly_units").OfType<JObject>().Where(u => (string?)u["unit_category"] == "ground_unit").Select(u => u.DeepClone()));
    private JArray SelectRows(JObject query, string collection) => SelectRows(query, Rows(collection));
    private JArray SelectRows(JObject query, JArray rows)
    {
        if (!(query["ids"] is JArray ids) || ids.Count < 1 || ids.Count > MaxIdsPerQuery ||
            ids.Any(i => i.Type != JTokenType.String) || ids.Values<string>().Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw Error("invalid_arguments", "ids_invalid");
        return new JArray(ids.Select(id => rows.OfType<JObject>().SingleOrDefault(r => (string?)r["id"] == (string?)id)?.DeepClone()
            ?? throw Error("unknown_id", "unknown_id")));
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
            host["count_semantics"] = Prompts.Text("battle.json", "host_count_semantics");
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
        var supported = AdvisorInformation.BattleCapabilitySections;
        var sections = information.BattleSections;
        if (query["sections"] != null)
        {
            if (!(query["sections"] is JArray selected) || selected.Count < 1 || selected.Count > supported.Length ||
                selected.Any(s => s.Type != JTokenType.String || !supported.Contains((string)s!)) ||
                selected.Values<string>().Distinct(StringComparer.Ordinal).Count() != selected.Count)
                throw Error("invalid_arguments", "sections_invalid");
            if (selected.Values<string>().Any(s => !sections.Contains(s)))
                throw Error("information_disabled", "sections_disabled");
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
        var result = Bundle("units", rows, references);
        if (sections.Contains("sensor_performance"))
        {
            var sensorIds = rows.OfType<JObject>().SelectMany(u => (JArray)u["sensor_performance"]!)
                .Select(s => (string)s["reference_id"]!).ToHashSet(StringComparer.Ordinal);
            result["sensor_references"] = new JArray(Rows("sensor_references").OfType<JObject>()
                .Where(s => sensorIds.Contains((string)s["id"]!)).Select(s => s.DeepClone()));
        }
        return result;
    }

    // Commit first-delivery IDs only after the whole response passes the payload limits.
    private static HashSet<string> DeduplicateReferences(JArray results, string collection, HashSet<string> previous)
    {
        var delivered = new HashSet<string>(previous, StringComparer.Ordinal);
        foreach (var result in results.OfType<JObject>())
        {
            if (result["data"] is not JObject bundle || bundle[collection] is not JArray definitions) continue;
            foreach (var definition in definitions.OfType<JObject>().ToArray())
            {
                var id = (string?)definition["id"] ?? throw new InvalidOperationException("A reference ID is missing.");
                if (!delivered.Add(id)) definition.Remove();
            }
        }
        return delivered;
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
        if (assessment != null) result["contact_identification_candidates"] = new JObject
        {
            ["item_count"] = assessment.Candidates.Count, ["list_query"] = BattleContactAssessment.Query,
            ["description"] = Prompts.Text("battle.json", "catalog_identification_candidates")
        };
        if (result["unit_capabilities"] is JObject capabilities) capabilities["available_sections"] = new JArray(information.BattleSections);
        return result;
    }
    private JObject RawCatalogue() => new()
    {
        ["friendly_units"] = new JObject { ["item_count"] = Rows("friendly_units").Count, ["list_query"] = "list_units", ["detail_query"] = "get_units", ["description"] = Prompts.Text("battle.json", "catalog_friendly_units") },
        ["movement_plans"] = new JObject { ["item_count"] = Rows("movement_plans").Count, ["query"] = "get_movement_plans", ["description"] = Prompts.Text("battle.json", "catalog_movement_plans") },
        ["friendly_launched_weapons"] = new JObject { ["item_count"] = Rows("friendly_launched_weapons").Count, ["list_query"] = "list_launched_weapons", ["description"] = Prompts.Text("battle.json", "catalog_friendly_launched_weapons") },
        ["friendly_ground_units"] = new JObject { ["item_count"] = GroundUnits().Count, ["list_query"] = "list_ground_units", ["detail_query"] = "get_ground_units", ["description"] = Prompts.Text("battle.json", "catalog_friendly_ground_units") },
        ["friendly_bases"] = new JObject { ["item_count"] = Rows("friendly_bases").Count, ["list_query"] = "list_bases", ["detail_query"] = "get_bases", ["description"] = Prompts.Text("battle.json", "catalog_friendly_bases") },
        ["hosted_air_units"] = new JObject { ["item_count"] = Rows("hosted_air_units").Count, ["list_query"] = "list_air_units", ["detail_query"] = "get_air_units", ["description"] = Prompts.Text("battle.json", "catalog_hosted_air_units") },
        ["aviation_tasks"] = new JObject { ["item_count"] = Rows("aviation_hosts").Sum(h => ((JArray)h["tasks"]!).Count), ["list_query"] = "list_air_tasks", ["description"] = Prompts.Text("battle.json", "catalog_aviation_tasks") },
        ["unit_capabilities"] = new JObject { ["item_count"] = Rows("unit_capabilities").Count, ["query"] = "get_unit_capabilities", ["description"] = Prompts.Text("battle.json", "catalog_unit_capabilities") },
        ["public_class_threat_references"] = new JObject { ["query"] = "get_contact_threats", ["description"] = Prompts.Text("battle.json", "catalog_public_class_threat_references") },
        ["observed_contacts"] = new JObject { ["item_count"] = Rows("observed_contacts").Count, ["list_query"] = "list_contacts", ["description"] = Prompts.Text("battle.json", "catalog_observed_contacts") },
        ["mission_briefing"] = new JObject { ["query"] = "get_briefing", ["description"] = Prompts.Text("battle.json", "catalog_mission_briefing") },
        ["published_reports"] = new JObject { ["item_count"] = Rows("published_reports").Count, ["list_query"] = "list_reports", ["detail_query"] = "get_reports", ["description"] = Prompts.Text("battle.json", "catalog_published_reports") }
    };

    private JObject Envelope(string status) => new()
    {
        ["consultation_id"] = Id, ["status"] = status, ["round"] = RoundsUsed, ["remaining_rounds"] = Math.Max(0, MaxRounds - RoundsUsed)
    };
    private JObject Page(JObject query, JArray rows)
    {
        var offset = Integer(query, "offset", 0, 0, int.MaxValue);
        var limit = Integer(query, "limit", DefaultPageSize, 1, MaxPageSize);
        var items = rows.Skip(offset).Take(limit).Select(r => r.DeepClone()).ToArray();
        var more = offset < rows.Count - items.Length;
        return new JObject { ["items"] = new JArray(items), ["total_count"] = rows.Count, ["offset"] = offset,
            ["has_more"] = more, ["next_offset"] = more ? new JValue(offset + items.Length) : JValue.CreateNull() };
    }
    private int Integer(JObject query, string key, int fallback, int min, int max)
    {
        if (query[key] == null) return fallback;
        if (query[key]!.Type != JTokenType.Integer || !int.TryParse(query[key]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw Error("invalid_arguments", "pagination_invalid");
        return value;
    }
    private void Allowed(JObject query, params string[] keys)
    {
        if (query.Properties().Any(p => !keys.Contains(p.Name))) throw Error("invalid_arguments", "unsupported_argument");
    }
    private AdvisorQueryException Error(string code, string key, params object[] arguments) =>
        new(code, Prompts.Format("battle.json", "error_" + key, arguments));
    private JObject Tool()
    {
        var tool = RawTool();
        var properties = (JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        properties["query"]!["enum"] = new JArray(QueryNames.Where(information.BattleQuery));
        if (information.BattleSections.Length == 0) properties.Remove("sections");
        else
        {
            properties["sections"]!["items"]!["enum"] = new JArray(information.BattleSections);
            properties["sections"]!["description"] = Prompts.Text("battle.json", "tool_enabled_sections");
        }
        if (!information.Allows(InformationKind.BattleAviation)) { properties.Remove("host_ids"); properties.Remove("air_unit_ids"); }
        if (!information.Allows(InformationKind.BattleContacts)) properties.Remove("kind");
        else if (!information.Allows(InformationKind.BattleWeaponContacts))
            properties["kind"]!["enum"] = new JArray(contactKinds.Where(k => !BattleWeaponReader.IsKind(k)));
        tool["parameters"]!["properties"]!["requests"]!["items"] =
            AdvisorRequestShape.Union("query", requestShapes, properties, QueryNames.Where(information.BattleQuery));
        return tool;
    }
    private static readonly string[] contactKinds = { "air", "helicopter", "surface", "submarine", "airbase", "installation", "mobileunit", "aaa", "mobileaaa", "sam", "radar", "port", "bridge", "missile", "torpedo", "biologic", "invisible", "airdecoy", "subdecoy", "activesonobouy", "passivesonobouy" };
    private JObject RawTool() => new()
    {
        ["name"] = "query_situation",
        ["description"] = Prompts.Text("battle.json", "tool_description"),
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
                            ["query"] = new JObject { ["type"] = "string", ["enum"] = new JArray(QueryNames) },
                            ["ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = Prompts.Text("battle.json", "tool_ids") },
                            ["host_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = Prompts.Text("battle.json", "tool_host_ids") },
                            ["air_unit_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxIdsPerQuery, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = Prompts.Text("battle.json", "tool_air_unit_ids") },
                            ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray(contactKinds), ["description"] = Prompts.Text("battle.json", "tool_kind") },
                            ["sections"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = AdvisorInformation.BattleCapabilitySections.Length, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(AdvisorInformation.BattleCapabilitySections) }, ["description"] = Prompts.Text("battle.json", "tool_sections") },
                            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue, ["description"] = Prompts.Text("battle.json", "tool_offset") },
                            ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxPageSize, ["description"] = Prompts.Text("battle.json", "tool_limit") }
                        }
                    }
                }
            }
        }
    };
}
