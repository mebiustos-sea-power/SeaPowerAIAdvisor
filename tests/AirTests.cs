using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SituationTests;

internal static class AirTests
{
    internal static void Run(string path, Action<string, Action> check)
    {
        var assembly = Assembly.LoadFrom(path);
        check("Own land aircraft preserve displayed types, every readiness state and immutable counts", () =>
        {
            var f = new Fixture(assembly);
            var squadron = Land(f, 10, "BLUE", "own-port", "第1航空隊", "own-plane");
            foreach (var status in new[] { "Ready", "Arming", "Armed", "Airborne", "Maintenance" }) Frame(f, squadron, "own-plane", status);
            f.Sim.Catalog.AircraftSpecs.Add("own-plane", new OwnAircraftSpec("画面の機種名"));
            var s = new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName);
            var unit = s.Aviation.Units.Single(); var a = unit.Aircraft.Single();
            Require(unit.Name == "第1航空隊" && unit.BaseIndex == 0 && a.Name == "画面の機種名", "Own display identity was lost");
            Require(a.Counts["total"] == 5 && new[] { "ready_unarmed", "arming", "armed", "airborne", "maintenance" }.All(k => a.Counts[k] == 1),
                "Aircraft states were collapsed into a misleading ready count");
            Set(Sequence(squadron, "Airframes")[0]!, "Status", Enum.ToObject(Sequence(squadron, "Airframes")[0]!.GetType().GetField("Status")!.FieldType, 4));
            Require(a.Counts["ready_unarmed"] == 1, "Snapshot retained live aircraft state");
        });
        check("Own aviation ignores enemy squadrons and flights before resolving aircraft data", () =>
        {
            var f = new Fixture(assembly);
            var enemy = Land(f, 99, "RED", "enemy-port", "SECRET", "secret");
            Set(enemy, "Airframes", null); Set(enemy, "Name", null);
            var flight = f.New("Core.State.Flight"); Set(flight, "SideId", "RED"); Set(flight, "Name", null);
            Set(flight, "Airframes", null); Set(flight, "UnitIni", "SECRET AIRCRAFT"); Sequence(f.State, "Flights").Add(flight);
            var before = AdvisorInputBuilder.Build(new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName), "ja", "");
            Set(enemy, "BaseId", null); Set(flight, "Position", null); Set(flight, "CarrierHullId", 987654);
            Require(AdvisorInputBuilder.Build(new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName), "ja", "") == before,
                "Hidden enemy aviation affected player input");
        });
        check("Runway closures and unassigned basing are explicit, not inferred from a ready aircraft", () =>
        {
            var f = new Fixture(assembly);
            var q = Land(f, 10, "BLUE", "own-port", "基地航空隊", "plane");
            Frame(f, q, "plane", "Ready"); f.Sim.Catalog.AircraftSpecs.Add("plane", new OwnAircraftSpec("機体"));
            Dictionary(f.State, "RunwayDamagedUntil").Add("own-port", f.Now.AddHours(2));
            var a = new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName).Aviation.Units.Single();
            Require(a.LaunchBlocked == true && a.RunwayOutUntil == f.Now.AddHours(2), "Closed runway was shown as launchable");
            Set(q, "BaseId", null);
            var unknown = new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName).Aviation.Units.Single();
            Require(unknown.BaseIndex == null && unknown.LaunchBlocked == null, "Unknown base became a known-open runway");
        });
        check("Carrier data uses actual campaign wing calculations and leaves source state unchanged", () =>
        {
            var c = new CarrierFixture(assembly);
            var before = JsonConvert.SerializeObject(c.F.State);
            var s = c.Capture(); var wing = s.Aviation.Units.Single(u => u.Kind == "carrier_air_wing");
            var aew = wing.Aircraft.Single(a => a.Name == "E-2C テスト表示");
            var strike = wing.Aircraft.Single(a => a.Name == "A-6 テスト表示");
            Require(aew.CampaignLaunchSupported && aew.Counts["aboard"] == 3 && aew.Counts["available_for_preparation"] == 2 &&
                aew.Counts["cooling"] == 1 && aew.Counts["arming"] == 1 && aew.Counts["airborne"] == 2 && aew.Counts["lost"] == 2,
                "Carrier readiness differs from actual campaign calculations");
            Require(!strike.CampaignLaunchSupported && strike.Counts["aboard"] == 8 && strike.Counts["airborne"] == null &&
                strike.Counts["available_for_preparation"] == null && strike.LoadoutNames.Count == 0,
                "Aboard-only aircraft were presented as usable campaign sorties");
            Require(wing.LaunchBlocked == true && wing.LaunchReason != null && wing.DeckCycleRemaining >= 0, "In-port deck constraint was lost");
            Require(before == JsonConvert.SerializeObject(c.F.State), "Reading carrier data mutated the campaign");
            Set(c.Hull, "Wing", null); c.Capture();
            Require(c.Hull.GetType().GetField("Wing")!.GetValue(c.Hull) == null, "Reading an uninitialized wing created game state");
        });
        check("Own flights link to their current wing without double-counting or exposing internal IDs", () =>
        {
            var c = new CarrierFixture(assembly);
            var s = c.Capture(); var flight = s.Aviation.Flights.Single();
            Require(flight.Count == 2 && flight.Name == "飛行中テスト" && flight.AircraftName == "E-2C テスト表示" &&
                flight.SourceUnitIndex == 0 && flight.Returning, "Own flight identity or source link was lost");
            var input = JObject.Parse(AdvisorInputBuilder.Build(s, "ja", ""));
            var serializedFlight = input["situation"]!["friendly_air_flights"]![0]!;
            Require((string?)serializedFlight["source_air_unit_id"] == "own_air_unit_1" &&
                serializedFlight["CarrierHullId"] == null && serializedFlight["SquadronId"] == null, "Flight exposed game IDs");
            var before = input.ToString(Formatting.None);
            Set(c.Hull, "Id", 7654321); Set(c.Pending, "CarrierHullId", 7654321); Set(c.Flight, "CarrierHullId", 7654321);
            var changed = JObject.Parse(AdvisorInputBuilder.Build(c.Capture(), "ja", "")).ToString(Formatting.None);
            Require(before == changed, "Opaque own IDs changed AI references");
        });
        check("Aviation queries support direct details, filters and paging without initially sending aircraft lists", () =>
        {
            var c = new CarrierFixture(assembly);
            var own = Land(c.F, 33, "BLUE", "own-port", "基地隊", "aew");
            Frame(c.F, own, "aew", "Ready");
            var session = new AdvisorQuerySession(c.Capture(), "ja", "艦載機は？");
            Require(session.InitialInput()["situation"]!["friendly_air_units"] == null, "Aircraft details leaked into initial payload");
            JObject Run(params JObject[] queries) => session.Execute(new JObject { ["consultation_id"] = session.Id, ["requests"] = new JArray(queries) });
            var page = Run(new JObject { ["query"] = "list_air_units", ["limit"] = 1 });
            var data = page["results"]![0]!["data"]!;
            Require((int)data["total_count"]! == 2 && (bool)data["has_more"]! && data["items"]![0]!["aircraft"] == null, "Air paging or summary boundary failed");
            var carrier = Run(new JObject { ["query"] = "list_air_units", ["force_ids"] = new JArray("own_force_1") });
            var row = carrier["results"]![0]!["data"]!["items"]!.Single();
            var details = Run(new JObject { ["query"] = "get_air_units", ["ids"] = new JArray(row["id"]!.ToString()) },
                new JObject { ["query"] = "list_air_units", ["base_ids"] = new JArray("victory_base_1") },
                new JObject { ["query"] = "list_air_flights" });
            Require(details["results"]![0]!["data"]![0]!["aircraft"]!.Count() == 2 &&
                details["results"]![1]!["data"]!["items"]!.Count() == 1 && details["results"]![2]!["data"]!["items"]!.Count() == 1,
                "Aviation details or host filtering failed");
            Require((string?)Run(new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("own_ship_1_1") })["error"]!["code"] == "unknown_id",
                "Non-air IDs were accepted as air units");
            Require((string?)Run(new JObject { ["query"] = "list_air_units", ["base_ids"] = new JArray("victory_base_1"), ["force_ids"] = new JArray("own_force_1") })["error"]!["code"] == "invalid_arguments",
                "Ambiguous air filtering was silently accepted");
        });
        check("Unknown aircraft states and missing specifications fail visibly instead of returning an empty force", () =>
        {
            var f = new Fixture(assembly);
            var q = Land(f, 10, "BLUE", "own-port", "航空隊", "plane"); var frame = Frame(f, q, "plane", "Ready");
            f.Sim.Catalog.AircraftSpecs.Add("plane", new OwnAircraftSpec("機体"));
            Set(frame, "Status", Enum.ToObject(frame.GetType().GetField("Status")!.FieldType, 99));
            Throws<InvalidOperationException>(() => new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName));
            SetEnum(frame, "Status", "Ready"); f.Sim.Catalog.AircraftSpecs.Clear();
            Throws<TargetInvocationException>(() => new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName));
        });
    }

    internal static object Land(Fixture f, int id, string side, string? baseId, string name, string aircraft)
    {
        var q = f.New("Core.State.Squadron"); Set(q, "Id", id); Set(q, "SideId", side); Set(q, "BaseId", baseId); Set(q, "Name", name);
        Sequence(f.State, "Squadrons").Add(q); return q;
    }
    internal static object Frame(Fixture f, object container, string type, string status)
    {
        var a = f.New("Core.State.Airframe"); Set(a, "UnitIni", type); SetEnum(a, "Status", status);
        Sequence(container, "Airframes").Add(a); return a;
    }

    internal sealed class CarrierFixture
    {
        internal readonly Fixture F;
        internal readonly object Hull, Pending, Flight;
        internal readonly AirSim Sim;
        internal CarrierFixture(Assembly assembly)
        {
            F = new Fixture(assembly);
            var specs = new Dictionary<string, object>(); var aircraft = new Dictionary<string, object>();
            var group = Sequence(F.State, "TaskForces")[0]!;
            foreach (var hull in Sequence(group, "Ships").Cast<object>())
            {
                var spec = F.New("Core.Data.ShipSpec"); Set(spec, "TypeAbbr", "DD");
                specs.Add((string)Get(hull, "UnitIni"), spec);
            }
            Hull = Sequence(group, "Ships")[0]!;
            var carrier = specs[(string)Get(Hull, "UnitIni")]; Sequence(carrier, "Roles").Add("Carrier");
            void AddWing(string type, int full)
            {
                var list = Sequence(carrier, "AirGroup");
                list.Add(Activator.CreateInstance(list.GetType().GenericTypeArguments[0], type, full)!);
            }
            AddWing("aew", 8); AddWing("strike", 8);
            foreach (var type in new[] { "aew", "strike" })
            {
                var a = F.New("Core.Data.AircraftSpec"); Set(a, "IniName", type); Set(a, "DisplayName", type == "aew" ? "E-2C テスト表示" : "A-6 テスト表示");
                if (type == "aew") Sequence(a, "Roles").Add("AEW");
                var loadout = F.New("Core.Data.LoadoutSpec"); Set(loadout, "Name", "テスト兵装");
                Sequence(a, "Loadouts").Add(loadout); aircraft.Add(type, a);
            }
            var wing = F.New("Core.State.CarrierWing"); Dictionary(wing, "Aircraft").Add("aew", 3); Dictionary(wing, "Aircraft").Add("strike", 8); Set(Hull, "Wing", wing);
            var cooling = F.New("Core.State.DeckRecovery"); Set(cooling, "Type", "aew"); Set(cooling, "Count", 1); Set(cooling, "Until", F.Now.AddHours(1));
            Sequence(Hull, "Recovering").Add(cooling);
            Pending = F.New("Core.State.PendingFlight"); Set(Pending, "CarrierHullId", (int)Get(Hull, "Id")); Set(Pending, "UnitIni", "aew");
            Frame(F, Pending, "aew", "Arming"); Sequence(F.State, "PendingFlights").Add(Pending);
            Flight = F.New("Core.State.Flight"); Set(Flight, "CarrierHullId", (int)Get(Hull, "Id")); Set(Flight, "UnitIni", "aew"); Set(Flight, "SideId", "BLUE");
            Set(Flight, "Name", "飛行中テスト"); Set(Flight, "LoadoutName", "テスト兵装"); Set(Flight, "Position", F.Point(31, 131)); Set(Flight, "Returning", true);
            Frame(F, Flight, "aew", "Airborne"); Frame(F, Flight, "aew", "Airborne"); Sequence(F.State, "Flights").Add(Flight);
            var proxy = DispatchProxy.Create(assembly.GetType("DynamicCampaignMod.Core.Data.IUnitCatalog", true)!, typeof(AirCatalogProxy));
            ((AirCatalogProxy)proxy).Ships = specs; ((AirCatalogProxy)proxy).Aircraft = aircraft;
            var air = Activator.CreateInstance(assembly.GetType("DynamicCampaignMod.Core.Sim.AirOperations", true)!, F.State, proxy, null)!;
            var deck = Activator.CreateInstance(assembly.GetType("DynamicCampaignMod.Core.Sim.CarrierAir", true)!, F.State, proxy, air)!;
            Sim = new AirSim(F.State, proxy, F.Sim.Logistics, deck);
        }
        internal SituationSnapshot Capture() => new CampaignSituationReader().Capture(Sim, F.DisplayShipName);
    }
    internal sealed class AirSim
    {
        public object State, Catalog, Logistics, Deck;
        internal AirSim(object state, object catalog, object logistics, object deck) { State = state; Catalog = catalog; Logistics = logistics; Deck = deck; }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

// Public, non-sealed base is required by DispatchProxy; every unexpected game-service call fails.
public class AirCatalogProxy : DispatchProxy
{
    internal Dictionary<string, object> Ships = new(), Aircraft = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var key = (string)args![0]!;
        return targetMethod!.Name switch
        {
            "Ship" => Ships[key], "Aircraft" => Aircraft[key],
            "DisplayName" => SituationTests.Get(Aircraft[key], "DisplayName"),
            _ => throw new InvalidOperationException("Unexpected aircraft catalog call: " + targetMethod.Name)
        };
    }
}
