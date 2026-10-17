using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Queries operate only on detached player knowledge. This type has no game or provider access.
internal sealed class AdvisorQuerySession : IAdvisorQuerySession
{
    private readonly SituationSnapshot snapshot;
    private readonly AdvisorInformation information;
    private readonly JObject initial;
    private readonly Dictionary<string, int> forces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Force, int Ship)> ships = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ObservedBase> observations;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int RoundsUsed { get; private set; }
    public int MaxRounds { get; }
    public AdvisorPrompts Prompts { get; }
    internal int ReturnedBytes { get; private set; }

    internal AdvisorQuerySession(SituationSnapshot snapshot, string language, string? question, int maxRounds = DefaultMaxRounds, AdvisorInformation? information = null, AdvisorPrompts? prompts = null)
    {
        if (maxRounds < 1 || maxRounds > MaximumRounds) throw new ArgumentOutOfRangeException(nameof(maxRounds));
        MaxRounds = maxRounds;
        Prompts = prompts ?? AdvisorPrompts.Current;
        this.information = information ?? AdvisorInformation.Default;
        information = this.information;
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        for (var f = 0; f < snapshot.Forces.Count; f++)
        {
            forces.Add(SnapshotIds.Force(f), f);
            for (var s = 0; s < snapshot.Forces[f].Ships.Count; s++) ships.Add(SnapshotIds.Ship(f, s), (f, s));
        }
        observations = snapshot.Bases.ToDictionary(b => b.PublicBaseIndex);
        foreach (var b in snapshot.Bases)
        {
            if (b.PublicBaseIndex < 0 || b.PublicBaseIndex >= snapshot.Victory.Bases.Count ||
                snapshot.Victory.Bases[b.PublicBaseIndex].Name != b.Name)
                throw new InvalidOperationException("A base observation has no matching public base identity.");
        }
        foreach (var unit in snapshot.Aviation.Units)
        {
            if (unit.BaseIndex.HasValue && (unit.BaseIndex.Value < 0 || unit.BaseIndex.Value >= snapshot.Victory.Bases.Count))
                throw new InvalidOperationException("An own air unit has no matching public base.");
            if (unit.ForceIndex.HasValue && (unit.ForceIndex.Value < 0 || unit.ForceIndex.Value >= snapshot.Forces.Count ||
                !unit.ShipIndex.HasValue || unit.ShipIndex.Value < 0 || unit.ShipIndex.Value >= snapshot.Forces[unit.ForceIndex.Value].Ships.Count))
                throw new InvalidOperationException("An own air wing has no matching ship.");
        }
        if (snapshot.Aviation.Flights.Any(f => f.SourceUnitIndex.HasValue &&
            (f.SourceUnitIndex.Value < 0 || f.SourceUnitIndex.Value >= snapshot.Aviation.Units.Count)))
            throw new InvalidOperationException("An own flight has no matching air unit.");
        initial = AdvisorInputBuilder.Header(language, question, Prompts, false);
        initial["schema_version"] = 3;
        initial["interaction_mode"] = "on_demand";
        initial["consultation_id"] = Id;
        initial["situation"] = new JObject
        {
            ["captured_at_utc"] = AdvisorInputBuilder.Time(snapshot.At),
            ["player_side_id"] = snapshot.Side,
            ["coverage"] = new JObject
            {
                ["included"] = new JArray("victory_conditions", "overview"),
                ["available_on_request"] = new JArray("friendly_task_forces", "friendly_ships", "friendly_air_units", "friendly_air_flights", "observed_contacts", "public_bases", "base_observations", "published_orders", "recent_player_reports"),
                ["not_included"] = new JArray("detailed_air_missions", "air_sortie_feasibility", "detailed_routes", "detailed_weapon_capabilities"),
                ["report_limit"] = CampaignSituationReader.ReportLimit,
                ["reference_scope"] = "this_consultation"
            },
            ["victory_conditions"] = AdvisorInputBuilder.Victory(snapshot.Victory, snapshot.Side, Prompts),
            ["overview"] = new JObject
            {
                ["friendly_force_count"] = snapshot.Forces.Count,
                ["friendly_base_detail_count"] = snapshot.Ground.Bases().Count,
                ["friendly_ground_unit_count"] = snapshot.Ground.Units().Count,
                ["friendly_ship_count"] = ships.Count,
                ["convoy_count"] = snapshot.Forces.Count(f => f.IsConvoy),
                ["friendly_air_unit_count"] = snapshot.Aviation.Units.Count,
                ["friendly_air_flight_count"] = snapshot.Aviation.Flights.Count,
                ["observed_contact_count"] = snapshot.Contacts.Count,
                ["published_order_count"] = snapshot.Orders.Count,
                ["retained_report_count"] = snapshot.Reports.Count
            }
        };
        initial["aviation_semantics"] = Prompts.AviationSemantics;
        initial["knowledge_catalog"] = AdvisorQueryCatalog.Describe(snapshot, information, Prompts);
        initial["query_tool"] = AdvisorQueryCatalog.Tool(information, Prompts);
        initial["query_limits"] = new JObject
        {
            ["max_rounds"] = MaxRounds, ["max_queries_per_round"] = MaxQueriesPerRound,
            ["max_ids_per_query"] = MaxIdsPerQuery, ["default_page_size"] = DefaultPageSize,
            ["max_page_size"] = MaxPageSize, ["max_request_utf8_bytes"] = MaxRequestBytes,
            ["max_response_utf8_bytes"] = MaxResponseBytes, ["max_total_data_utf8_bytes"] = MaxTotalBytes,
            ["byte_counting"] = Prompts.Text("campaign.json", "byte_counting")
        };
        var overview = (JObject)initial["situation"]!["overview"]!;
        if (!information.Allows(InformationKind.CampaignForces)) { overview.Remove("friendly_force_count"); overview.Remove("convoy_count"); }
        if (!information.Allows(InformationKind.CampaignShips)) overview.Remove("friendly_ship_count");
        if (!information.Allows(InformationKind.CampaignAviation))
        {
            overview.Remove("friendly_air_unit_count"); overview.Remove("friendly_air_flight_count"); initial.Remove("aviation_semantics");
        }
        if (!information.Allows(InformationKind.CampaignOwnBases)) overview.Remove("friendly_base_detail_count");
        if (!information.Allows(InformationKind.CampaignGroundUnits)) overview.Remove("friendly_ground_unit_count");
        if (!information.Allows(InformationKind.CampaignContacts)) overview.Remove("observed_contact_count");
        if (!information.Allows(InformationKind.CampaignOrders)) overview.Remove("published_order_count");
        if (!information.Allows(InformationKind.CampaignReports)) overview.Remove("retained_report_count");
        initial["situation"]!["coverage"]!["available_on_request"] = new JArray(
            ((JArray)initial["knowledge_catalog"]!).OfType<JObject>().Select(r => (string)r["category"]!));
        information.AddCoverage(initial, false);
        ReturnedBytes = Bytes(initial);
        if (ReturnedBytes > MaxTotalBytes)
            throw new InvalidOperationException("Mandatory initial input exceeds the consultation payload limit.");
    }

    public JObject InitialInput() => (JObject)initial.DeepClone();

    public JObject Execute(JObject request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        try
        {
            if (RoundsUsed >= MaxRounds) throw Invalid("round_limit", "round_limit");
            // Every attempt consumes a round, including rejected requests.
            RoundsUsed++;
            Allowed(request, "consultation_id", "requests");
            if (String(request, "consultation_id") != Id) throw Invalid("wrong_consultation", "wrong_consultation");
            if (Bytes(request) > MaxRequestBytes) throw Invalid("request_too_large", "request_too_large");
            if (!(request["requests"] is JArray requests) || requests.Count < 1 || requests.Count > MaxQueriesPerRound)
                throw Invalid("invalid_arguments", "requests_count", MaxQueriesPerRound);
            var results = new JArray();
            foreach (var item in requests)
            {
                if (!(item is JObject query)) throw Invalid("invalid_arguments", "query_object_invalid");
                results.Add(new JObject { ["query"] = String(query, "query"), ["data"] = Dispatch(query) });
            }
            var response = Envelope("ok");
            response["results"] = results;
            var bytes = Bytes(response);
            if (bytes > MaxResponseBytes) throw Invalid("response_too_large", "response_too_large");
            if (ReturnedBytes + bytes > MaxTotalBytes) throw Invalid("payload_limit", "payload_limit");
            ReturnedBytes += bytes;
            return response;
        }
        catch (AdvisorQueryException error)
        {
            var response = Envelope("error");
            response["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message };
            // Responses to malformed calls contain no source details.
            return response;
        }
    }

    private JToken Dispatch(JObject query)
    {
        var name = String(query, "query");
        if (!information.CampaignQuery(name)) throw Invalid("information_disabled", "information_disabled");
        switch (name)
        {
            case "catalog":
                Allowed(query, "query");
                return AdvisorQueryCatalog.Describe(snapshot, information, Prompts);
            case "list_forces":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Forces.Select((f, i) => CampaignInformation.Force(new JObject
                {
                    ["id"] = SnapshotIds.Force(i), ["display_name"] = f.Name,
                    ["position"] = AdvisorInputBuilder.Position(f.Position), ["ship_count"] = f.Ships.Count, ["is_convoy"] = f.IsConvoy
                }, information)));
            case "get_forces":
                Allowed(query, "query", "ids", "include_ships");
                var includeShips = Boolean(query, "include_ships", false);
                if (includeShips && !information.Allows(InformationKind.CampaignShips))
                    throw Invalid("information_disabled", "ships_disabled");
                return new JArray(Ids(query, "ids").Select(id =>
                {
                    var i = Resolve(forces, id);
                    var result = AdvisorInputBuilder.Force(snapshot.Forces[i], i);
                    result["ship_count"] = snapshot.Forces[i].Ships.Count;
                    if (!includeShips) result.Remove("ships");
                    return CampaignInformation.Force(result, information);
                }));
            case "list_ships":
                Allowed(query, "query", "offset", "limit", "force_ids");
                var selected = query["force_ids"] == null ? null : new HashSet<int>(Ids(query, "force_ids").Select(id => Resolve(forces, id)));
                return Page(query, snapshot.Forces.SelectMany((f, fi) => f.Ships.Select((s, si) => (f, fi, s, si)))
                    .Where(x => selected == null || selected.Contains(x.fi)).Select(x => new JObject
                    {
                        ["id"] = SnapshotIds.Ship(x.fi, x.si), ["force_id"] = SnapshotIds.Force(x.fi),
                        ["display_name"] = x.s.Name, ["type_code"] = x.s.Type, ["is_merchant"] = x.s.Merchant
                    }));
            case "get_ships":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id =>
                {
                    if (!ships.TryGetValue(id, out var index)) throw Invalid("unknown_id", "unknown_ship");
                    var result = AdvisorInputBuilder.Ship(snapshot.Forces[index.Force].Ships[index.Ship], index.Force, index.Ship);
                    result["force_id"] = SnapshotIds.Force(index.Force);
                    return CampaignInformation.Ship(result, information);
                }));
            case "list_air_units":
                Allowed(query, "query", "offset", "limit", "force_ids", "base_ids");
                if (query["force_ids"] != null && !information.Allows(InformationKind.CampaignForces))
                    throw Invalid("information_disabled", "force_filter_disabled");
                if (query["force_ids"] != null && query["base_ids"] != null)
                    throw Invalid("invalid_arguments", "air_filters_conflict");
                var airForces = query["force_ids"] == null ? null : new HashSet<int>(Ids(query, "force_ids").Select(id => Resolve(forces, id)));
                var airBases = query["base_ids"] == null ? null : new HashSet<int>(Ids(query, "base_ids").Select(id => Index(id, snapshot.Victory.Bases.Count, SnapshotIds.Base)));
                return Page(query, snapshot.Aviation.Units.Select((u, i) => (u, i))
                    .Where(x => (airForces == null || x.u.ForceIndex.HasValue && airForces.Contains(x.u.ForceIndex.Value)) &&
                        (airBases == null || x.u.BaseIndex.HasValue && airBases.Contains(x.u.BaseIndex.Value)))
                    .Select(x => CampaignInformation.Aviation(AdvisorInputBuilder.AirUnit(x.u, x.i, false), information)));
            case "get_air_units":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id =>
                {
                    var i = Index(id, snapshot.Aviation.Units.Count, SnapshotIds.AirUnit);
                    return CampaignInformation.Aviation(AdvisorInputBuilder.AirUnit(snapshot.Aviation.Units[i], i), information);
                }));
            case "list_air_flights":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Aviation.Flights.Select(AdvisorInputBuilder.AirFlight));
            case "list_contacts":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Contacts.Select((c, i) => AdvisorInputBuilder.Contact(c, i, snapshot.At)));
            case "list_bases":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Victory.Bases.Select((b, i) => CampaignInformation.PublicBase(new JObject
                {
                    ["id"] = SnapshotIds.Base(i), ["display_name"] = b.Name, ["kind"] = AdvisorInputBuilder.BaseKind(b.Kind),
                    ["position"] = AdvisorInputBuilder.Position(b.Position),
                    ["ownership_status"] = b.OwnerSide == null ? "unknown" : "known",
                    ["current_owner_side_id"] = b.OwnerSide == null ? JValue.CreateNull() : new JValue(b.OwnerSide),
                    ["observation_status"] = observations.ContainsKey(i) ? "in_snapshot" : "not_in_snapshot"
                }, information)));
            case "get_bases":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id =>
                {
                    var row = snapshot.Ground.Bases().OfType<JObject>().SingleOrDefault(b => (string?)b["id"] == id)
                        ?? throw Invalid("unknown_id", "unknown_base");
                    return CampaignInformation.Base(row, information);
                }));
            case "list_ground_units":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Ground.Units().OfType<JObject>().Select(row =>
                {
                    row.Remove("strength"); row.Remove("maximum_strength"); row.Remove("strength_meaning");
                    return CampaignInformation.Ground(row, information);
                }));
            case "get_ground_units":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id => CampaignInformation.Ground(
                    snapshot.Ground.Units().OfType<JObject>().SingleOrDefault(u => (string?)u["id"] == id)
                        ?? throw Invalid("unknown_id", "unknown_ground_unit"), information)));
            case "get_base_observations":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id =>
                {
                    var index = Index(id, snapshot.Victory.Bases.Count, SnapshotIds.Base);
                    if (!observations.TryGetValue(index, out var observation))
                        return new JObject { ["id"] = id, ["observation_status"] = "not_in_snapshot" };
                    var result = AdvisorInputBuilder.Base(observation, index);
                    result["id"] = id; result.Remove("public_base_id");
                    result["observation_status"] = "in_snapshot";
                    return result;
                }));
            case "list_orders":
                Allowed(query, "query", "offset", "limit");
                return Page(query, snapshot.Orders.Select((o, i) =>
                {
                    var result = AdvisorInputBuilder.Order(o, i); result.Remove("briefing_text"); return result;
                }));
            case "get_orders":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id => {
                    var i = Index(id, snapshot.Orders.Count, SnapshotIds.Order);
                    return AdvisorInputBuilder.Order(snapshot.Orders[i], i);
                }));
            case "list_reports":
                Allowed(query, "query", "offset", "limit", "kind");
                var kind = query["kind"] == null ? null : String(query, "kind");
                if (kind != null && !new[] { "contact", "battle", "landing", "fleet", "air", "supply", "production", "admiralty", "economy", "other" }.Contains(kind))
                    throw Invalid("invalid_arguments", "report_kind_invalid");
                return Page(query, snapshot.Reports.Select((r, i) => (r, i)).OrderByDescending(x => x.r.At)
                    .Select(x =>
                    {
                        var result = AdvisorInputBuilder.Report(x.r); result.Remove("text");
                        result["id"] = SnapshotIds.Report(x.i); result["text_length_utf16"] = x.r.Text.Length; return result;
                    }).Where(r => kind == null || (string?)r["kind"] == kind));
            case "get_reports":
                Allowed(query, "query", "ids");
                return new JArray(Ids(query, "ids").Select(id => {
                    var i = Index(id, snapshot.Reports.Count, SnapshotIds.Report);
                    var result = AdvisorInputBuilder.Report(snapshot.Reports[i]); result["id"] = id; return result;
                }));
            default: throw Invalid("unknown_query", "unknown_query");
        }
    }

    private JObject Envelope(string status) => new()
    {
        ["consultation_id"] = Id, ["captured_at_utc"] = AdvisorInputBuilder.Time(snapshot.At),
        ["status"] = status, ["round"] = RoundsUsed, ["remaining_rounds"] = Math.Max(0, MaxRounds - RoundsUsed)
    };
    private JObject Page(JObject query, IEnumerable<JObject> source)
    {
        var offset = Integer(query, "offset", 0, 0, int.MaxValue);
        var limit = Integer(query, "limit", DefaultPageSize, 1, MaxPageSize);
        var rows = source.ToArray();
        var items = rows.Skip(offset).Take(limit).ToArray();
        return new JObject
        {
            ["items"] = new JArray(items), ["total_count"] = rows.Length,
            ["offset"] = offset, ["has_more"] = offset < rows.Length - items.Length,
            ["next_offset"] = offset < rows.Length - items.Length ? new JValue(offset + items.Length) : JValue.CreateNull()
        };
    }
    private int Resolve(Dictionary<string, int> map, string id) => map.TryGetValue(id, out var index) ? index
        : throw Invalid("unknown_id", "unknown_force");
    private int Index(string id, int count, Func<int, string> name)
    {
        for (var i = 0; i < count; i++) if (name(i) == id) return i;
        throw Invalid("unknown_id", "unknown_id");
    }
    private string[] Ids(JObject query, string name)
    {
        if (!(query[name] is JArray values) || values.Count < 1 || values.Count > MaxIdsPerQuery ||
            values.Any(v => v.Type != JTokenType.String || string.IsNullOrEmpty((string?)v)))
            throw Invalid("invalid_arguments", "ids_count", name, MaxIdsPerQuery);
        var ids = values.Values<string>().Select(s => s!).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw Invalid("invalid_arguments", "duplicate_ids");
        return ids;
    }
    private int Integer(JObject query, string name, int fallback, int min, int max)
    {
        if (query[name] == null) return fallback;
        if (query[name]!.Type != JTokenType.Integer) throw Invalid("invalid_arguments", "integer_type", name);
        if (!long.TryParse(query[name]!.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < min || value > max) throw Invalid("invalid_arguments", "integer_range", name);
        return (int)value;
    }
    private bool Boolean(JObject query, string name, bool fallback)
    {
        if (query[name] == null) return fallback;
        if (query[name]!.Type != JTokenType.Boolean) throw Invalid("invalid_arguments", "boolean_type", name);
        return (bool)query[name]!;
    }
    private string String(JObject query, string name) => query[name]?.Type == JTokenType.String && !string.IsNullOrEmpty((string?)query[name])
        ? (string)query[name]! : throw Invalid("invalid_arguments", "string_type", name);
    private void Allowed(JObject query, params string[] names)
    {
        if (query.Properties().Any(p => !names.Contains(p.Name))) throw Invalid("invalid_arguments", "unsupported_argument");
    }
    private AdvisorQueryException Invalid(string code, string key, params object[] arguments) =>
        new(code, Prompts.Format("campaign.json", "error_" + key, arguments));
}
