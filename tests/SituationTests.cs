using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Reflection;
using SeaPowerAIAdvisor;

// Use the installed campaign's real state classes, with own-ship services that reject enemy access.
internal static class SituationTests
{
    internal static void Run(string campaignPath, Action<string, Action> check, string[] samplePaths)
    {
        var assembly = Assembly.LoadFrom(campaignPath);
        check("Hidden enemy changes cannot affect the detached player situation", () =>
        {
            var f = new Fixture(assembly);
            var reader = new CampaignSituationReader();
            var snapshot = reader.Capture(f.Sim);
            var before = SituationFormatter.Format(snapshot);
            f.HideEverythingDifferently();
            Require(SituationFormatter.Format(reader.Capture(f.Sim)) == before, "Hidden data changed the preview");
            Require(SituationFormatter.Format(snapshot) == before, "A captured snapshot retained mutable source objects");
        });
        check("Only player forces reach ship catalog and ammunition services", () =>
        {
            var f = new Fixture(assembly);
            var s = new CampaignSituationReader().Capture(f.Sim);
            Require(s.Forces.Count == 1 && s.Forces[0].Name == "護衛隊", "Enemy forces were included");
            var ships = s.Forces[0].Ships;
            Require(ships.Count == 3 && ships[0].FuelShare == .6 && ships[0].DamageShare == .2 && ships[0].AmmoShare == .4 && ships[0].Type == "DD", "Own readiness was lost");
            Require(ships[1].Nuclear && ships[2].Unarmed && ships[2].AmmoShare == null, "Nuclear/unarmed treatment is incorrect");
            Require(s.Forces[0].Port == "味方港" && s.Forces[0].Mission == "Follow", "Own location/mission was lost");
            Require(f.Sim.Catalog.Calls == 3 && f.Sim.Logistics.Calls == 2, "Unexpected ship service access");
        });
        check("Enemy contacts use sightings even when identified or actual enemy is absent", () =>
        {
            var f = new Fixture(assembly);
            Set(f.Contact, "Identified", true);
            Sequence(f.State, "TaskForces").Remove(f.Enemy);
            var s = new CampaignSituationReader().Capture(f.Sim);
            var c = s.Contacts.Single();
            Require(c.Identified && c.Position.Latitude == 31 && c.Position.Longitude == 132 && c.Count == 2, "Contact resolved into live state");
            Require(c.SeenAt == f.Now.AddHours(-1) && c.UncertaintyNm == 25, "Observation time/uncertainty changed");
            var before = SituationFormatter.Format(s);
            Set(f.Contact, "Count", 3);
            Set(f.Contact, "Position", f.Point(32, 133));
            var changed = new CampaignSituationReader().Capture(f.Sim);
            Require(changed.Contacts.Single().Count == 3 && SituationFormatter.Format(changed) != before, "New observations did not update the preview");
            Require(s.Contacts.Single().Count == 2, "Snapshot contact changed after capture");
        });
        check("Opaque contact identifiers cannot change preview ordering or labels", () =>
        {
            var f = new Fixture(assembly);
            var intel = Dictionary(f.State, "Intel")["BLUE"]!;
            var contacts = Dictionary(intel, "Contacts");
            var other = f.New("Core.State.Contact");
            Set(other, "MoverId", 10); SetEnum(other, "Kind", "Surface"); Set(other, "Count", 4);
            Set(other, "LastSeen", f.Now.AddHours(-1)); Set(other, "Position", f.Point(33, 134));
            contacts.Add(10, other);
            var reader = new CampaignSituationReader();
            var before = SituationFormatter.Format(reader.Capture(f.Sim));
            contacts.Clear();
            Set(f.Contact, "MoverId", 1); Set(other, "MoverId", 999);
            contacts.Add(1, f.Contact); contacts.Add(999, other);
            Require(SituationFormatter.Format(reader.Capture(f.Sim)) == before, "Internal identifiers influenced the disclosed preview");
        });
        check("Contact aging grows uncertainty and expires only after twelve game hours", () =>
        {
            var f = new Fixture(assembly);
            Set(f.Contact, "LastSeen", f.Now.AddHours(-12));
            var reader = new CampaignSituationReader();
            Require(reader.Capture(f.Sim).Contacts.Single().UncertaintyNm == 300, "Twelve-hour boundary is wrong");
            Set(f.Contact, "LastSeen", f.Now.AddHours(-12).AddSeconds(-1));
            Require(reader.Capture(f.Sim).Contacts.Count == 0, "Forgotten contact was disclosed");
        });
        check("Unknown enemy base forces stay unknown while observed zero remains zero", () =>
        {
            var f = new Fixture(assembly);
            var reader = new CampaignSituationReader();
            var b = reader.Capture(f.Sim).Bases.Single();
            Require(b.LandSeenAt == f.Now.AddHours(-2) && b.Batteries == 2, "Observed defenses were lost");
            Require(b.ForcesSeenAt == null && b.Ships == null && b.Aircraft == null, "Unobserved base counts leaked");
            Set(f.Sighting, "ForcesAt", f.Now.AddMinutes(-15));
            Set(f.Sighting, "ShipsInPort", 0); Set(f.Sighting, "Aircraft", 0);
            b = reader.Capture(f.Sim).Bases.Single();
            Require(b.Ships == 0 && b.Aircraft == 0 && b.ForcesSeenAt.HasValue, "Observed zero became unknown");
            Set(f.Sighting, "Holder", "FORMER_OWNER");
            b = reader.Capture(f.Sim).Bases.Single();
            Require(b.LandSeenAt == null && b.ForcesSeenAt == null && b.Batteries == null, "A former owner's defense records were reused");
        });
        check("Colocated air base reuses only the matching port sighting", () =>
        {
            var f = new Fixture(assembly);
            var air = f.New("Core.Data.BaseDefinition");
            Set(air, "id", "enemy-air"); Set(air, "name", "敵飛行場"); SetEnum(air, "kind", "AirBase");
            Set(air, "lat", 35d); Set(air, "lon", 140d); Set(air, "port", "enemy-port"); Set(air, "colocated", true);
            Sequence(f.Definition, "bases").Add(air);
            Dictionary(f.State, "BaseOwner").Add("enemy-air", "RED");
            var b = new CampaignSituationReader().Capture(f.Sim).Bases.Single(x => x.Name == "敵飛行場");
            Require(b.Batteries == 2 && b.ForcesSeenAt == null && b.Kind == "AirBase", "Shared base sighting was incorrectly resolved");
        });
        check("Orders exclude enemy orders and undisclosed future phases", () =>
        {
            var f = new Fixture(assembly);
            var orders = new CampaignSituationReader().Capture(f.Sim).Orders;
            Require(orders.Count == 2 && orders.Any(x => x.Title == "開示作戦") && orders.Any(x => x.Title == "護衛命令"), "Published orders were not preserved");
            Require(orders.Single(x => x.Title == "護衛命令").Deadline == f.Now.AddHours(12), "Public deadline was lost");
        });
        check("Reports use only recent player feed and do not read global messages", () =>
        {
            var f = new Fixture(assembly);
            var feed = Sequence(f.State, "Feed");
            feed.Clear();
            for (var i = 0; i < 35; i++) feed.Add(f.Message("BLUE", f.Now.AddMinutes(i - 40), "報告" + i));
            feed.Add(f.Message("RED", f.Now.AddMinutes(-1), "秘密報告"));
            feed.Add(f.Message("BLUE", f.Now.AddDays(1), "未来報告"));
            Set(f.State, "Messages", null);
            var reports = new CampaignSituationReader().Capture(f.Sim).Reports;
            Require(reports.Count == 30 && reports[0].Text == "報告5" && reports[29].Text == "報告34", "Feed filtering or retention is wrong");
        });
        check("Fog disabled refuses capture and missing player intel fails without creating it", () =>
        {
            var f = new Fixture(assembly);
            var reader = new CampaignSituationReader();
            Set(f.Definition, "fogOfWar", false);
            Throws<SituationUnavailableException>(() => reader.Capture(f.Sim));
            Set(f.Definition, "fogOfWar", true);
            Dictionary(f.State, "Intel").Remove("BLUE");
            var count = Dictionary(f.State, "Intel").Count;
            Throws<InvalidOperationException>(() => reader.Capture(f.Sim));
            Require(Dictionary(f.State, "Intel").Count == count, "Capture created an intelligence record");
        });
        check("Impossible observation dates and unknown order statuses surface as errors", () =>
        {
            var f = new Fixture(assembly);
            Set(f.Contact, "LastSeen", f.Now.AddSeconds(1));
            Throws<InvalidOperationException>(() => new CampaignSituationReader().Capture(f.Sim));
            Set(f.Contact, "LastSeen", f.Now);
            var field = f.Order.GetType().GetField("Status")!;
            field.SetValue(f.Order, Enum.ToObject(field.FieldType, 99));
            Throws<InvalidOperationException>(() => new CampaignSituationReader().Capture(f.Sim));
        });
        if (samplePaths.Length == 2)
            check("Real saved campaign captures without changing source state", () => CheckSave(assembly, samplePaths[0], samplePaths[1]));
    }

    internal static void CheckSave(Assembly assembly, string savePath, string definitionPath)
    {
        // A real save checks field/null/date compatibility; catalog readiness is covered separately.
        var saved = JObject.Parse(File.ReadAllText(savePath));
        JsonSerializer Serializer() => (JsonSerializer)assembly.GetType("DynamicCampaignMod.Core.State.CampaignSave", true)!
            .GetMethod("Serializer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var state = saved["State"]!.ToObject(assembly.GetType("DynamicCampaignMod.Core.State.CampaignState", true)!, Serializer())!;
        var definition = JsonConvert.DeserializeObject(File.ReadAllText(definitionPath),
            assembly.GetType("DynamicCampaignMod.Core.Data.CampaignDefinition", true)!)!;
        Set(state, "Definition", definition);
        var sim = new StubSim(state);
        var side = (string)Get(state, "PlayerSideId");
        var own = Sequence(state, "TaskForces").Cast<object>().Where(x => (string)Get(x, "SideId") == side).ToArray();
        foreach (var hull in own.SelectMany(x => Sequence(x, "Ships").Cast<object>()))
        {
            sim.Catalog.Specs[(string)Get(hull, "UnitIni")] = new OwnSpec(false, false);
            sim.Logistics.Own.Add(hull);
        }
        var before = JToken.FromObject(state, Serializer()).ToString(Formatting.None);
        var snapshot = new CampaignSituationReader().Capture(sim);
        Require(snapshot.Forces.Count == own.Length && snapshot.Side == side, "Saved player force filter changed");
        Require(SituationFormatter.Format(snapshot).Length > 0, "Saved situation is empty");
        var after = JToken.FromObject(state, Serializer()).ToString(Formatting.None);
        Require(before == after, "Capture modified the loaded state");
        Console.WriteLine("Sample counts: own fleets=" + snapshot.Forces.Count + ", contacts=" + snapshot.Contacts.Count +
            ", opposing bases=" + snapshot.Bases.Count + ", reports=" + snapshot.Reports.Count);
    }

    private static object Get(object target, string field) => target.GetType().GetField(field)!.GetValue(target)!;
    private static void Set(object target, string field, object? value) => target.GetType().GetField(field)!.SetValue(target, value);
    private static void SetEnum(object target, string field, string value) => Set(target, field, Enum.Parse(target.GetType().GetField(field)!.FieldType, value));
    private static IList Sequence(object target, string field) => (IList)Get(target, field);
    private static IDictionary Dictionary(object target, string field) => (IDictionary)Get(target, field);
    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Fixture
    {
        private readonly Assembly assembly;
        internal readonly DateTime Now = new(1985, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        internal readonly object State, Definition, Enemy, Contact, Sighting, Order, Pending, EnemyIntel, OwnMission;
        internal readonly StubSim Sim;
        internal Fixture(Assembly assembly)
        {
            this.assembly = assembly;
            State = New("Core.State.CampaignState"); Definition = New("Core.Data.CampaignDefinition");
            Set(State, "Definition", Definition); Set(State, "Now", Now); Set(State, "PlayerSideId", "BLUE"); Set(Definition, "fogOfWar", true);
            var intel = New("Core.State.SideIntel"); Set(intel, "SideId", "BLUE");
            Dictionary(State, "Intel").Add("BLUE", intel);
            EnemyIntel = New("Core.State.SideIntel"); Set(EnemyIntel, "SideId", "RED");
            Dictionary(State, "Intel").Add("RED", EnemyIntel);
            Contact = New("Core.State.Contact");
            Set(Contact, "MoverId", 900); SetEnum(Contact, "Kind", "Surface"); Set(Contact, "Count", 2);
            Set(Contact, "Position", Point(31, 132)); Set(Contact, "LastSeen", Now.AddHours(-1));
            Dictionary(intel, "Contacts").Add(900, Contact);
            var own = New("Core.State.TaskForce");
            Set(own, "Id", 100); Set(own, "Name", "護衛隊"); Set(own, "SideId", "BLUE");
            Set(own, "Position", Point(30, 130)); Set(own, "SpeedKnots", 15d); Set(own, "PortId", "own-port");
            OwnMission = New("Core.State.GroupMission"); SetEnum(OwnMission, "Kind", "Follow"); Set(OwnMission, "TargetId", 900); Set(own, "Mission", OwnMission);
            Sequence(State, "TaskForces").Add(own);
            Enemy = New("Core.State.TaskForce"); Set(Enemy, "Id", 900); Set(Enemy, "SideId", "RED"); Set(Enemy, "Name", "SECRET ENEMY");
            Set(Enemy, "Position", Point(50, 150)); Sequence(State, "TaskForces").Add(Enemy);
            Sim = new StubSim(State);
            for (var i = 0; i < 3; i++)
            {
                var hull = New("Core.State.Hull");
                Set(hull, "Name", "自軍艦" + i); Set(hull, "UnitIni", "own" + i);
                Set(hull, "Fuel", .6); Set(hull, "Damage", .2);
                Sequence(own, "Ships").Add(hull);
                Sim.Catalog.Specs.Add("own" + i, new OwnSpec(i == 1, i == 2)); Sim.Logistics.Own.Add(hull);
            }
            foreach (var id in new[] { "own-port", "enemy-port" })
            {
                var b = New("Core.Data.BaseDefinition"); Set(b, "id", id); Set(b, "name", id == "own-port" ? "味方港" : "敵港");
                Set(b, "lat", 35d); Set(b, "lon", 140d); SetEnum(b, "kind", "NavalBase");
                Sequence(Definition, "bases").Add(b); Dictionary(State, "BaseOwner").Add(id, id == "own-port" ? "BLUE" : "RED");
            }
            Sighting = New("Core.State.BaseSighting"); Set(Sighting, "Holder", "RED"); Set(Sighting, "At", Now.AddHours(-2));
            Set(Sighting, "Batteries", 2); Set(Sighting, "SamSites", 1); Set(Sighting, "GroundUnits", 3);
            Set(Sighting, "ShipsInPort", 567); Set(Sighting, "Aircraft", 876);
            Dictionary(intel, "Bases").Add("enemy-port", Sighting);
            var op = New("Core.State.AdmiraltyOperation"); Set(op, "Id", 200); Set(op, "SideId", "BLUE"); Set(op, "Name", "開示作戦");
            Set(op, "Summary", "船団を護衛する"); SetEnum(op, "Status", "Active"); Set(op, "Offered", Now.AddHours(-4));
            Sequence(State, "Operations").Add(op);
            Order = MakeOrder("BLUE", "Active", "護衛命令", Now.AddHours(-3)); Set(Order, "OperationId", 200);
            Pending = MakeOrder("BLUE", "Pending", "SECRET NEXT PHASE", Now.AddHours(-3)); Set(Pending, "OperationId", 200);
            MakeOrder("RED", "Active", "SECRET ENEMY ORDER", Now.AddHours(-3));
            MakeOrder("BLUE", "Offered", "SECRET FUTURE ORDER", Now.AddDays(1));
            var orphan = MakeOrder("BLUE", "Active", "SECRET UNDISCLOSED OPERATION", Now.AddHours(-3)); Set(orphan, "OperationId", 999);
            Sequence(State, "Feed").Add(Message("BLUE", Now.AddMinutes(-1), "公開された報告"));
            Sequence(State, "Messages").Add(Message("RED", Now, "SECRET GLOBAL MESSAGE"));
        }
        internal object New(string name) => Activator.CreateInstance(assembly.GetType("DynamicCampaignMod." + name, true)!)!;
        internal object Point(double lat, double lon) => Activator.CreateInstance(assembly.GetType("DynamicCampaignMod.Core.Geo.GeoPoint", true)!, lat, lon)!;
        internal object Message(string side, DateTime at, string text)
        {
            var message = New("Core.State.SideMessage"); Set(message, "SideId", side); Set(message, "At", at); Set(message, "Text", text); return message;
        }
        private object MakeOrder(string side, string status, string title, DateTime offered)
        {
            var order = New("Core.State.AdmiraltyOrder"); Set(order, "SideId", side); SetEnum(order, "Status", status);
            Set(order, "Title", title); Set(order, "Briefing", title + "の説明"); Set(order, "Offered", offered); Set(order, "Deadline", Now.AddHours(12));
            Sequence(State, "AdmiraltyOrders").Add(order); return order;
        }
        internal void HideEverythingDifferently()
        {
            Set(Enemy, "Name", null); Set(Enemy, "Position", Point(-70, -80)); Set(Enemy, "Ships", null); Set(Enemy, "Mission", null);
            var extra = New("Core.State.TaskForce"); Set(extra, "SideId", "RED"); Set(extra, "Name", null); Set(extra, "Ships", null);
            Sequence(State, "TaskForces").Add(extra);
            Dictionary(State, "Intel").Remove("RED");
            Set(Contact, "MoverId", 999); Set(Contact, "Source", "CHANGED SECRET SOURCE"); Set(Contact, "CourseDeg", 279d); Set(Contact, "SpeedKnots", 99d);
            Set(Contact, "FixPosition", Point(-12, -13)); Set(Contact, "FixTime", Now);
            Set(OwnMission, "Point", Point(-23, -24)); Set(OwnMission, "AimedAt", Point(-25, -26)); Set(OwnMission, "TargetId", 999);
            Set(Pending, "Title", "CHANGED HIDDEN PHASE"); Set(Pending, "Briefing", null);
            Set(Order, "TargetId", 999); Sequence(Order, "TargetHulls").Add(12345);
            Set(Sighting, "ShipsInPort", 999); Set(Sighting, "Aircraft", 998);
            var enemyBase = Sequence(Definition, "bases").Cast<object>().Single(b => (string)Get(b, "id") == "enemy-port");
            Set(enemyBase, "aircraftCapacity", 987); Set(enemyBase, "convoys", 987); Set(enemyBase, "production", 987d);
            Dictionary(enemyBase, "stores").Clear(); Dictionary(enemyBase, "stores").Add("secret-store", 999);
            Dictionary(State, "Stocks").Add("enemy-port", New("Core.State.BaseStock"));
            Set(State, "Messages", null);
        }
    }

    internal sealed class StubSim
    {
        public object State;
        public OwnCatalog Catalog = new();
        public OwnLogistics Logistics = new();
        internal StubSim(object state) { State = state; }
    }
    internal sealed class OwnSpec
    {
        public string TypeAbbr = "DD";
        public bool Nuclear { get; }
        public bool Unarmed { get; }
        internal OwnSpec(bool nuclear, bool unarmed) { Nuclear = nuclear; Unarmed = unarmed; }
    }
    internal sealed class OwnCatalog
    {
        internal readonly Dictionary<string, OwnSpec> Specs = new();
        internal int Calls;
        public OwnSpec Ship(string ini) { Calls++; return Specs[ini]; }
    }
    internal sealed class OwnLogistics
    {
        internal readonly HashSet<object> Own = new();
        internal int Calls;
        public double AmmoLevel(object hull)
        {
            Require(Own.Contains(hull), "Enemy hull was passed to ammunition service"); Calls++; return .4;
        }
    }
}
