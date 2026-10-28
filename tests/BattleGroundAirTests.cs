using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunGroundAir(Action<string, Action> check)
    {
        check("Known ground/base positions and own flight-deck evidence are frozen without enemy inventory reads", () =>
        {
            var f = GroundFixture(out var deck);
            f.ContactKind = "Airbase";
            deck.Type.Loadouts.Add(new DeckLoadout { DisplayName = "FUTURE_UNSELECTABLE", ServiceEntryYear = 2030 });
            deck.Types.Add(new DeckType { DisplayName = "EMPTY_TYPE_NOT_DISPLAYED", Numbers = 0 });
            var snapshot = f.Capture();
            var before = snapshot.Copy().ToString();
            Require(!before.Contains("FUTURE_UNSELECTABLE", StringComparison.Ordinal) && !before.Contains("EMPTY_TYPE_NOT_DISPLAYED", StringComparison.Ordinal),
                "Aircraft selector filters leaked an unshown aircraft type or future loadout");
            deck.Type.Numbers = 0;
            deck.Type.Loadouts[0].Available.Value = 0;
            deck.Tasks[0].Info = "Changed after capture";
            f.OwnUnits[1].Position.Value = new Geo(-20, -30);
            f.Secret.Value = "Changed hidden enemy base and aircraft inventory";
            Require(snapshot.Copy().ToString() == before, "Copied ground/aviation evidence retained live state");
            var session = new BattleQuerySession(snapshot, "ja", "基地と地上目標を確認して");
            var reply = session.Execute(Request(session,
                new JObject { ["query"] = "list_bases" }, new JObject { ["query"] = "list_ground_units" },
                new JObject { ["query"] = "list_contacts", ["kind"] = "airbase" },
                new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") },
                new JObject { ["query"] = "list_air_tasks", ["air_unit_ids"] = new JArray("hosted_air_unit_1") }));
            var results = reply["results"]!;
            Require((string?)results[0]!["data"]!["items"]![0]!["display_name"] == "味方航空基地" &&
                (double?)results[0]!["data"]!["items"]![0]!["position"]!["latitude_deg"] == 31 &&
                (string?)results[1]!["data"]!["items"]![0]!["ground_kind"] == "sam" &&
                (string?)results[2]!["data"]!["items"]![0]!["position_source"] == "extrapolated_estimate",
                "Own ground classification or observed-only enemy base position was lost");
            var air = results[3]!["data"]!;
            Require((int?)air["air_units"]![0]!["unassigned_aircraft_count"] == 4 &&
                (int?)air["air_units"]![0]!["squadrons"]![0]!["unassigned_aircraft_count"] == 4 &&
                (int?)air["air_units"]![0]!["loadouts"]![0]!["displayed_stock_availability"] == 3 &&
                (int?)results[4]!["data"]!["items"]![0]!["assigned_aircraft_count"] == 2 &&
                (string?)results[4]!["data"]!["items"]![0]!["displayed_status"] == "発進準備中" &&
                (bool?)air["hosts"]![0]!["current_sortie_feasibility_known"] == false,
                "Inventory, preparation observations or readiness uncertainty changed");
            Require(((JArray)air["weapon_references"]!).Count == 1 &&
                (double?)air["weapon_references"]![0]!["maximum_launch_range_nm"] == 70,
                "Hosted-aircraft armament lost its public reference evidence");
        });
        check("Ground/base/aviation permissions gate alternate unit routes, capability IDs and host filters", () =>
        {
            var f = GroundFixture(out _);
            var selection = AdvisorInformation.All.With(InformationKind.BattleGroundUnits, false).With(InformationKind.BattleBases, false);
            var session = new BattleQuerySession(f.Capture(), "en", "Status", 20, selection);
            var initial = session.InitialInput();
            Require((int?)initial["situation"]!["overview"]!["friendly_unit_count"] == 1 &&
                initial["situation"]!["overview"]!["friendly_base_count"] == null &&
                initial["situation"]!["overview"]!["friendly_ground_unit_count"] == null &&
                (int?)initial["situation"]!["overview"]!["hosted_air_unit_count"] == 0, "Disabled entity counts leaked");
            var listing = session.Execute(Request(session, new JObject { ["query"] = "list_units" }));
            Require(((JArray)listing["results"]![0]!["data"]!["items"]!).Count == 1, "Disabled ground/base appeared in generic unit listing");
            foreach (var query in new[] { "get_units", "get_unit_capabilities" })
                Require((string?)session.Execute(Request(session, new JObject { ["query"] = query, ["ids"] = new JArray("own_unit_2") }))["error"]?["code"] == "unknown_id",
                    "A generic lookup bypassed base/ground exclusion");
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") }))["error"]?["code"] == "unknown_id",
                "Aviation exposed an excluded host");
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "list_air_units", ["host_ids"] = new JArray("own_unit_2") }))["error"]?["code"] == "unknown_id",
                "A host filter resolved an excluded base");
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "list_ground_units" }))["error"]?["code"] == "information_disabled",
                "The dedicated ground route bypassed its checkbox");
        });
        check("Aviation and armament exclusions do not leak through base details, loadouts or shared references", () =>
        {
            var f = GroundFixture(out _);
            var session = new BattleQuerySession(f.Capture(), "en", "Air", information: AdvisorInformation.All.With(InformationKind.BattleWeapons, false));
            var air = session.Execute(Request(session, new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") }))["results"]![0]!["data"]!;
            Require(air["air_units"]![0]!["loadouts"] == null && !air["weapon_references"]!.HasValues &&
                (int?)air["hosts"]![0]!["displayed_task_count"] == 1, "Disabling weapons leaked loadouts or removed independent preparation observations");
            var noAir = new BattleQuerySession(f.Capture(), "en", "Bases", information: AdvisorInformation.All.With(InformationKind.BattleAviation, false));
            var b = noAir.Execute(Request(noAir, new JObject { ["query"] = "get_bases", ["ids"] = new JArray("own_unit_2") }))["results"]![0]!["data"]![0]!;
            Require(b["has_aviation_facility"] == null &&
                (string?)noAir.Execute(Request(noAir, new JObject { ["query"] = "list_air_units" }))["error"]?["code"] == "information_disabled",
                "Base metadata or direct retrieval bypassed aviation exclusion");
        });
        check("Aviation queries are paginated and bounded and large details are retrieved only when requested", () =>
        {
            var f = GroundFixture(out var deck);
            deck.Types.Add(new DeckType { DisplayName = "哨戒機" });
            var session = new BattleQuerySession(f.Capture(), "ja", "航空機の運用", 20);
            Require(!session.InitialInput().ToString().Contains("発進準備中", StringComparison.Ordinal) &&
                !session.InitialInput().ToString().Contains("艦上戦闘機", StringComparison.Ordinal), "Detailed aviation evidence was eagerly sent");
            var page = session.Execute(Request(session, new JObject { ["query"] = "list_air_units", ["host_ids"] = new JArray("own_unit_2"), ["limit"] = 1 }))["results"]![0]!["data"]!;
            Require((int?)page["total_count"] == 2 && (bool?)page["has_more"] == true && (int?)page["next_offset"] == 1, "Aviation pagination lost inventory entries");
            var detail = session.Execute(Request(session, new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1", "hosted_air_unit_2") }))["results"]![0]!["data"]!;
            Require(((JArray)detail["air_units"]!).Count == 2 && ((JArray)detail["hosts"]!).Count == 1 &&
                ((JArray)detail["weapon_references"]!).Count == 1, "Batch results duplicated host or weapon evidence");
            foreach (var query in new[] {
                new JObject { ["query"] = "get_air_units", ["ids"] = new JArray(Enumerable.Range(1,11).Select(i => "hosted_air_unit_" + i)) },
                new JObject { ["query"] = "list_air_units", ["host_ids"] = new JArray("own_unit_2", "own_unit_2") },
                new JObject { ["query"] = "list_air_units", ["limit"] = 21 } })
                Require((string?)session.Execute(Request(session, query))["error"]?["code"] == "invalid_arguments", "New retrieval bypassed existing request limits");
        });
        check("Large preparation queues remain paginated without making aircraft inventory details too large", () =>
        {
            var f = GroundFixture(out var deck);
            for (var i = 1; i < 240; i++) deck.Tasks.Add(new PendingLaunchTask(deck.Type) { Info = new string('準', 100) });
            var session = new BattleQuerySession(f.Capture(), "ja", "準備状況", 20);
            var inventory = session.Execute(Request(session, new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") }));
            Require((string?)inventory["status"] == "ok" && (int?)inventory["results"]![0]!["data"]!["hosts"]![0]!["displayed_task_count"] == 240 &&
                inventory["results"]![0]!["data"]!["hosts"]![0]!["tasks"] == null, "Large queue made the smallest inventory request impossible");
            var first = session.Execute(Request(session, new JObject { ["query"] = "list_air_tasks", ["host_ids"] = new JArray("own_unit_2"), ["limit"] = 20 }));
            var last = session.Execute(Request(session, new JObject { ["query"] = "list_air_tasks", ["air_unit_ids"] = new JArray("hosted_air_unit_1"), ["offset"] = 220, ["limit"] = 20 }));
            Require((string?)first["status"] == "ok" && (int?)first["results"]![0]!["data"]!["total_count"] == 240 &&
                (bool?)first["results"]![0]!["data"]!["has_more"] == true && (string?)last["status"] == "ok" &&
                (bool?)last["results"]![0]!["data"]!["has_more"] == false, "Task pagination truncated or overfilled a large displayed queue");
        });
        check("Gemini can batch ground/base and aircraft inventory retrieval through the common provider", () =>
        {
            var transport = new GroundAirTransport();
            var result = new GeminiFixture(transport).ConsultAsync(new BattleQuerySession(GroundFixture(out _).Capture(), "ja", "航空基地から何を出すべき？"),
                AdvisorSettings.Parse("{\"api_key\":\"mock-ground-air-key\"}"), Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 2 && result.QueryRounds == 1 && result.Queries.Contains("get_air_units") && transport.Calls == 2,
                "The actual provider path could not complete a ground/aviation batch");
        });
    }
    private static Fixture GroundFixture(out Deck deck)
    {
        var f = new Fixture(); deck = new Deck();
        var b = new OwnUnit { _type = "LandUnit" }; b.Name.Value = "味方航空基地"; b._obp._landUnitSubType = "AirBase"; b._obp._flightDeck = deck;
        var ground = new OwnUnit { _type = "LandUnit" }; ground.Name.Value = "防空ミサイル部隊"; ground._obp._landUnitSubType = "SAM";
        f.OwnUnits.Add(b); f.OwnUnits.Add(ground); return f;
    }
    private sealed class Deck
    {
        internal readonly DeckType Type = new();
        internal readonly List<DeckType> Types;
        internal readonly List<PendingLaunchTask> Tasks;
        internal Deck() { Types = new() { Type }; Tasks = new() { new PendingLaunchTask(Type) }; }
        public object[] _vehiclesOnBoard => Types.Cast<object>().ToArray();
        public object[] FlightDeckTasks => Tasks.Cast<object>().ToArray();
        public bool LaunchInProgress => false;
        public bool LandingInProgress => true;
        public int AircraftCurrentlyOnBoard => 6;
        public int MaxAircraftOnBoard => 20;
        public int GroundCrewCount => 4;
        public int AssignedGroundCrew => 2;
        public int DeckParkSlots => 8;
        public int AssignedDeckParkSlots => 2;
    }
    private sealed class DeckType
    {
        public string DisplayName { get; set; } = "艦上戦闘機";
        public string _objectType => "Aircraft";
        public int Numbers { get; set; } = 4;
        public object[] Squadrons => new object[] { new { DisplayName = "第1飛行隊", Numbers } };
        public List<DeckLoadout> Loadouts { get; } = new() { new DeckLoadout() };
    }
    private sealed class DeckLoadout
    {
        public string DisplayName { get; set; } = "迎撃装備";
        public int ServiceEntryYear { get; set; } = 1970;
        public double _readyUpDuration => 180;
        public Reactive<int> Available { get; } = new(3);
        public Dictionary<string,int> AmmunitionAmounts { get; } = new() { ["test_missile"] = 2 };
    }
    private sealed class PendingLaunchTask
    {
        internal PendingLaunchTask(DeckType type) { VehicleType = type; }
        public DeckType VehicleType { get; }
        public string FlightDeckTaskLabel => "発進準備";
        public string AircraftType => "艦上戦闘機";
        public string Squadron => "第1飛行隊";
        public string Info { get; set; } = "発進準備中";
        public int LaunchCount => 2;
    }
    private sealed class GroundAirTransport : IGeminiTransport
    {
        internal int Calls;
        public Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken cancellation)
        {
            Calls++;
            JObject part;
            if (Calls == 1) part = new JObject { ["functionCall"] = new JObject { ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(
                new JObject { ["query"] = "get_bases", ["ids"] = new JArray("own_unit_2") },
                new JObject { ["query"] = "get_ground_units", ["ids"] = new JArray("own_unit_3") },
                new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") }) } } };
            else
            {
                var response = InteractionFixture.Result(body);
                Require((string?)response["status"] == "ok" && ((JArray)response["results"]!).Count == 3 &&
                    (int?)response["results"]![2]!["data"]!["air_units"]![0]!["unassigned_aircraft_count"] == 4,
                    "Actual function response lost permitted ground/aviation evidence");
                part = new JObject { ["text"] = "格納機と準備状況を確認して運用してください。" };
            }
            return Task.FromResult(InteractionFixture.Reply(part));
        }
    }
}
