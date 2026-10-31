using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SituationTests;

internal static class QueryTests
{
    internal static void Run(string campaignPath, Action<string, Action> check)
    {
        var assembly = Assembly.LoadFrom(campaignPath);
        check("Initial on-demand input keeps objectives and language while withholding ship and report details", () =>
        {
            var f = new Fixture(assembly);
            var session = Session(f);
            var initial = session.InitialInput();
            Require((string?)initial["reply_language"] == "ja" && (string?)initial["question"] == "護衛隊は？", "Question or game language changed");
            Require(initial["situation"]!["victory_conditions"]!["sides"]!.Count() == 2 &&
                (int)initial["situation"]!["overview"]!["friendly_ship_count"]! == 3, "Mandatory objectives or overview missing");
            Require(initial["situation"]!["friendly_task_forces"] == null && initial["situation"]!["recent_player_reports"] == null,
                "Initial input eagerly contains unrequested details");
            Require(!initial.ToString().Contains("自軍艦0", StringComparison.Ordinal) &&
                !initial.ToString().Contains("公開された報告", StringComparison.Ordinal), "Full ship or report content was sent initially");
            initial["situation"]!["victory_conditions"]!["bases"]![0]!["display_name"] = "外部変更";
            Require(!session.InitialInput().ToString().Contains("外部変更", StringComparison.Ordinal), "A caller can mutate the initial input");
            Require(session.InitialInput()["knowledge_catalog"]!.All(c => (int)c["item_count"]! >= 0), "Catalog counts invalid");
        });
        check("Campaign read variants match runtime identifiers, report filters and mutually exclusive aviation filters", () =>
        {
            foreach (var (query, valid) in new[] {
                (Q("catalog"), true), (Q("list_forces"), true),
                (Q("get_forces", "ids", new JArray("own_force_1")), true),
                (Q("list_air_units", "force_ids", new JArray("own_force_1")), true),
                (Q("get_forces"), false),
                (Q("catalog", "kind", "air"), false),
                (new JObject { ["query"] = "list_air_units", ["force_ids"] = new JArray("own_force_1"), ["base_ids"] = new JArray("victory_base_1") }, false),
                (Q("list_contacts", "kind", "air"), false)
            })
            {
                var session = Session(new Fixture(assembly));
                var tool = session.InitialInput()["query_tool"]!;
                Require(RequestSchemaFixture.Accepts(tool["parameters"]!, new JObject { ["requests"] = new JArray(query) }) == valid,
                    "Campaign schema accepts a request the runtime cannot support: " + query);
                Require(((string?)session.Execute(Request(session, query))["status"] == "ok") == valid,
                    "Campaign dispatch disagrees with its request definition: " + query);
            }
        });
        check("A consultation can drill from catalog to forces to ship list to ship readiness", () =>
        {
            var session = Session(new Fixture(assembly));
            Require(Data(Call(session, Q("catalog"))).Any(c => (string?)c["category"] == "friendly_task_forces"), "No force entry point");
            var force = Data(Call(session, Q("list_forces")))["items"]![0]!;
            var list = Data(Call(session, Q("list_ships", "force_ids", new JArray((string)force["id"]!))))["items"]!;
            Require(list.Count() == 3 && list[0]!["fuel"] == null, "Ship list is not a compact intermediate step");
            var ship = Data(Call(session, Q("get_ships", "ids", new JArray((string)list[0]!["id"]!))))[0]!;
            Require((string?)ship["display_name"] == "自軍艦0" && (double)ship["fuel"]!["remaining_fraction"]! == .6 &&
                (double)ship["ammunition"]!["remaining_fraction"]! == .4, "Drilling did not reach the selected own ship");
        });
        check("Direct and batched retrieval preserve the same identities and requested detail", () =>
        {
            var session = Session(new Fixture(assembly));
            var response = Call(session,
                Q("get_ships", "ids", new JArray("own_ship_1_3", "own_ship_1_1")),
                Q("get_forces", "ids", new JArray("own_force_1")),
                new JObject { ["query"] = "get_forces", ["ids"] = new JArray("own_force_1"), ["include_ships"] = true });
            var results = response["results"]!;
            Require((string?)results[0]!["data"]![0]!["id"] == "own_ship_1_3" &&
                (string?)results[0]!["data"]![0]!["ammunition"]!["status"] == "unarmed", "Batch order or unarmed meaning lost");
            Require(results[1]!["data"]![0]!["ships"] == null && results[2]!["data"]![0]!["ships"]!.Count() == 3,
                "Force detail ignored include_ships");
            Require(JToken.DeepEquals(results[0]!["data"]![1]!["fuel"], results[2]!["data"]![0]!["ships"]![0]!["fuel"]),
                "Direct and nested ship facts disagree");
        });
        check("List pagination visits every ship without leaking detail or losing boundary entries", () =>
        {
            var f = new Fixture(assembly); var baseline = Capture(f);
            var force = baseline.Forces[0];
            var ships = Enumerable.Range(0, 25).Select(i => new FriendlyShip("艦" + i, "DD", .5, 0, false, false, false, .8));
            var snapshot = Replace(baseline, forces: new[] { new FriendlyForce(force.Name, force.Position, force.SpeedKnots,
                force.Port, force.Mission, force.IsConvoy, ships) });
            var session = new AdvisorQuerySession(snapshot, "ja", "");
            var first = Data(Call(session, new JObject { ["query"] = "list_ships", ["limit"] = 20 }));
            var second = Data(Call(session, new JObject { ["query"] = "list_ships", ["limit"] = 20, ["offset"] = first["next_offset"]!.DeepClone() }));
            Require(first["items"]!.Count() == 20 && second["items"]!.Count() == 5 &&
                (bool)first["has_more"]! && !(bool)second["has_more"]! && second["next_offset"]!.Type == JTokenType.Null,
                "Page boundaries lost ships");
            Require(first["items"]!.Concat(second["items"]!).Select(s => (string)s["id"]!).Distinct().Count() == 25,
                "Pagination duplicated or skipped an identity");
        });
        check("Public base IDs resolve observations while orders reports and contacts keep disclosure semantics", () =>
        {
            var session = Session(new Fixture(assembly));
            var response = Call(session,
                Q("get_base_observations", "ids", new JArray("victory_base_2", "victory_base_1")),
                Q("list_contacts"), Q("list_orders"), Q("get_orders", "ids", new JArray("published_order_1")),
                Q("list_reports"), Q("get_reports", "ids", new JArray("report_1")), Q("list_bases"));
            var results = response["results"]!;
            var enemyBase = results[0]!["data"]![0]!;
            Require((int)enemyBase["ground_defenses"]!["batteries"]! == 2 &&
                enemyBase["ships_and_aircraft"]!["ships_in_port"]!.Type == JTokenType.Null, "Unobserved garrison strength leaked");
            Require((string?)results[0]!["data"]![1]!["observation_status"] == "not_in_snapshot", "Own base absence became zero strength");
            Require((double)results[1]!["data"]!["items"]![0]!["observed_position"]!["latitude_deg"]! == 31, "Contact used a live position");
            Require(results[2]!["data"]!["items"]![0]!["briefing_text"] == null &&
                results[3]!["data"]![0]!["briefing_text"] != null, "Order text was not retrieved on demand");
            Require(results[4]!["data"]!["items"]![0]!["text"] == null &&
                (string?)results[5]!["data"]![0]!["text"] == "公開された報告", "Report text was lost or sent in the listing");
            Require((string?)results[6]!["data"]!["items"]![1]!["id"] == "victory_base_2", "Base IDs disagree across queries and objectives");
        });
        check("Report metadata can be filtered by language-independent kind and is listed newest first", () =>
        {
            var f = new Fixture(assembly); Sequence(f.State, "Feed").Clear();
            var old = f.Message("BLUE", f.Now.AddHours(-2), "古い戦闘"); SetEnum(old, "Kind", "Battle");
            var latest = f.Message("BLUE", f.Now.AddMinutes(-1), "新しい戦闘"); SetEnum(latest, "Kind", "Battle");
            var supply = f.Message("BLUE", f.Now, "補給"); SetEnum(supply, "Kind", "Supply");
            foreach (var message in new[] { old, latest, supply }) Sequence(f.State, "Feed").Add(message);
            var session = Session(f);
            var list = Data(Call(session, Q("list_reports", "kind", new JValue("battle"))))["items"]!;
            Require(list.Count() == 2 && (string?)list[0]!["id"] == "report_2", "Report filtering or chronology changed");
            Require((string?)Data(Call(session, Q("get_reports", "ids", new JArray((string)list[0]!["id"]!))))[0]!["text"] == "新しい戦闘",
                "Filtered report IDs do not identify the same source text");
        });
        check("Hidden enemy changes cannot affect initial input or any disclosed query result", () =>
        {
            var f = new Fixture(assembly);
            var before = Session(f);
            f.HideEverythingDifferently();
            var after = Session(f);
            Require(JToken.DeepEquals(WithoutSession(before.InitialInput()), WithoutSession(after.InitialInput())), "Hidden state changed initial knowledge");
            var requests = new[] { Q("get_forces", "ids", new JArray("own_force_1")), Q("list_contacts"),
                Q("get_base_observations", "ids", new JArray("victory_base_2")), Q("list_orders"), Q("get_reports", "ids", new JArray("report_1")) };
            Require(JToken.DeepEquals(WithoutSession(Call(before, requests)), WithoutSession(Call(after, requests))), "Hidden state changed query results");
        });
        check("Queries keep one captured time and detached values while the live campaign advances", () =>
        {
            var f = new Fixture(assembly); var session = Session(f);
            Set(f.State, "Now", f.Now.AddDays(1));
            Set(f.Contact, "Position", f.Point(-50, -60));
            var own = Sequence(f.State, "TaskForces")[0]!;
            Set(Sequence(own, "Ships")[0]!, "Fuel", .01);
            var response = Call(session, Q("get_ships", "ids", new JArray("own_ship_1_1")), Q("list_contacts"));
            Require((string?)response["captured_at_utc"] == "1985-06-01T12:00:00.0000000Z" &&
                (double)response["results"]![0]!["data"]![0]!["fuel"]!["remaining_fraction"]! == .6 &&
                (double)response["results"]![1]!["data"]!["items"]![0]!["observed_position"]!["latitude_deg"]! == 31,
                "Live data entered an existing consultation");
        });
        check("Unsupported paths, wrong IDs and malformed arguments are rejected atomically", () =>
        {
            var snapshot = Capture(new Fixture(assembly));
            foreach (var request in new[] {
                Q("get_enemy_positions"), Q("get_ships", "ids", new JArray("900")),
                Q("get_ships", "ids", new JArray("victory_base_1")), Q("get_ships", "ids", new JArray("own_ship_1_1", "own_ship_1_1")),
                Q("get_ships", "ids", new JArray(Enumerable.Range(0, 11).Select(i => "id" + i))),
                new JObject { ["query"] = "get_ships", ["ids"] = new JArray("own_ship_1_1"), ["path"] = "State.TaskForces" },
                new JObject { ["query"] = "list_forces", ["offset"] = -1 }, new JObject { ["query"] = "list_forces", ["limit"] = 21 },
                new JObject { ["query"] = "list_forces", ["limit"] = "2" }, new JObject { ["query"] = "list_forces", ["limit"] = JToken.Parse("999999999999999999999999999999999999999") }
            })
            {
                var session = new AdvisorQuerySession(snapshot, "ja", "");
                var response = session.Execute(Request(session, Q("get_ships", "ids", new JArray("own_ship_1_1")), request));
                Require((string?)response["status"] == "error" && response["results"] == null &&
                    !response.ToString().Contains("自軍艦0", StringComparison.Ordinal), "Rejected batch exposed partial data");
            }
            var current = new AdvisorQuerySession(snapshot, "ja", "");
            var other = new AdvisorQuerySession(snapshot, "ja", "");
            Require((string?)current.Execute(Request(other, Q("list_forces")))["error"]!["code"] == "wrong_consultation",
                "IDs were accepted with a stale consultation scope");
        });
        check("Standard campaign reads retain successful data beside an invalid individual query", () =>
        {
            var session = Session(new Fixture(assembly));
            var read = AdvisorContext.Create(session, Array.Empty<ChatExchange>()).Reads;
            var result = read.ExecuteAsync(new JObject { ["requests"] = new JArray(
                new JObject { ["source"] = "situation", ["query"] = "catalog" },
                new JObject { ["source"] = "situation", ["query"] = "unknown" },
                new JObject { ["source"] = "situation", ["query"] = "list_forces" }) },
                System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            Require((string?)result["results"]?[0]?["status"] == "ok" &&
                (string?)result["results"]?[1]?["error"]?["code"] == "unknown_query" &&
                result["results"]?[1]?["data"] == null && (int?)result["results"]?[2]?["index"] == 2 &&
                result["results"]?[2]?["data"]?["items"]?.Count() == 1 && session.RoundsUsed == 1,
                "A campaign item error discarded other results or consumed extra rounds");
        });
        check("Every attempted request consumes a round and the query limit stops further results", () =>
        {
            var session = Session(new Fixture(assembly));
            for (var i = 0; i < AdvisorQueryLimits.DefaultMaxRounds; i++)
                Require((string?)session.Execute(Request(session, Q("unknown")))["error"]!["code"] == "unknown_query", "Expected rejection changed");
            Require((string?)session.Execute(Request(session, Q("list_forces")))["error"]!["code"] == "round_limit" &&
                session.RoundsUsed == AdvisorQueryLimits.DefaultMaxRounds, "Repeated errors bypassed the round limit");
        });
        check("Request and response size limits count UTF-8 bytes without truncating a selected report", () =>
        {
            var f = new Fixture(assembly); var baseline = Capture(f);
            var session = new AdvisorQuerySession(Replace(baseline, reports: new[] {
                new PublishedReport(f.Now, "Other", new string('報', 20_000)) }), "ja", "");
            var response = session.Execute(Request(session, Q("get_reports", "ids", new JArray("report_1"))));
            Require((string?)response["error"]!["code"] == "response_too_large" && response["results"] == null,
                "A large UTF-8 report was partially sent");
            var huge = Q("get_reports", "ids", new JArray(new string('a', 17_000)));
            Require((string?)session.Execute(Request(session, huge))["error"]!["code"] == "request_too_large", "Oversized request accepted");
        });
        check("Repeated report reads exceed the former cumulative cap and still stop at the query-round limit", () =>
        {
            var f = new Fixture(assembly); var report = new string('報', 12_000);
            var session = new AdvisorQuerySession(Replace(Capture(f), reports: new[] {
                new PublishedReport(f.Now, "Other", report) }), "ja", "");
            for (var i = 0; i < AdvisorQueryLimits.DefaultMaxRounds; i++)
            {
                var response = session.Execute(Request(session, Q("get_reports", "ids", new JArray("report_1"))));
                Require((string?)response["status"] == "ok" && response["results"]![0]!["data"]!.ToString().Contains(report, StringComparison.Ordinal),
                    "Cumulative information size interrupted or truncated a valid report");
            }
            Require((string?)session.Execute(Request(session, Q("list_reports")))["error"]?["code"] == "round_limit" &&
                session.RoundsUsed == AdvisorQueryLimits.DefaultMaxRounds, "Large cumulative reads bypassed the query-round limit");
        });
        check("Unexpected source classification errors are surfaced rather than disguised as unavailable information", () =>
        {
            var f = new Fixture(assembly);
            var session = new AdvisorQuerySession(Replace(Capture(f), reports: new[] {
                new PublishedReport(f.Now, "UNSUPPORTED", "本文") }), "ja", "");
            try { session.Execute(Request(session, Q("get_reports", "ids", new JArray("report_1")))); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("A source error was hidden as normal query output");
        });
    }

    internal static SituationSnapshot Capture(Fixture f) => new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName);
    private static AdvisorQuerySession Session(Fixture f) => new(Capture(f), "ja", "護衛隊は？");
    private static SituationSnapshot Replace(SituationSnapshot original, FriendlyForce[]? forces = null, PublishedReport[]? reports = null) =>
        new(original.At, original.Side, forces ?? original.Forces, original.Contacts, original.Bases, original.Orders, reports ?? original.Reports, original.Victory, original.Aviation);
    internal static JObject Q(string name, string? key = null, JToken? value = null)
    {
        var q = new JObject { ["query"] = name }; if (key != null) q[key] = value; return q;
    }
    internal static JObject Request(AdvisorQuerySession session, params JObject[] queries) => new()
    { ["consultation_id"] = session.Id, ["requests"] = new JArray(queries.Select(q => q.DeepClone())) };
    internal static JObject Call(AdvisorQuerySession session, params JObject[] queries)
    {
        var result = session.Execute(Request(session, queries));
        Require((string?)result["status"] == "ok", result.ToString());
        return result;
    }
    private static JToken Data(JObject response) => response["results"]![0]!["data"]!;
    private static JObject WithoutSession(JObject source) { var copy = (JObject)source.DeepClone(); copy.Remove("consultation_id"); return copy; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
