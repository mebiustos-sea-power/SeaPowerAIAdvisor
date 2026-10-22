using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunMovement(Action<string, Action> check)
    {
        check("Movement queries distinguish shared formation routes from independent routes and freeze before consultation", () =>
        {
            var f = MovementFixture(out var follower, out var independent);
            var snapshot = f.Capture();
            var session = new BattleQuerySession(snapshot, "ja", "移動計画を比較", 20,
                information: AdvisorInformation.Default.With(InformationKind.BattleMovementPlans, true));
            ((GoToWaypointTask)((MovementRoot)f.Own._userRoot!).TaskViewModels[0].Task!).WaypointPosition = new Geo(0, 0);
            follower.ActsIndependentlyInFormation.Value = true;
            ((MovementRoot)independent._userRoot!).TaskViewModels.Clear();
            var initial = session.InitialInput();
            Require(initial["knowledge_catalog"]!["movement_plans"] != null && initial["situation"]!["movement_plans"] == null &&
                initial["situation"]!["movement_routes"] == null, "Routes were unavailable or sent before being requested");
            var data = MovementQuery(session, "own_unit_1", "own_unit_2", "own_unit_3");
            var units = (JArray)data["units"]!;
            Require((string?)units[0]!["movement_mode"] == "formation_leader" &&
                (string?)units[1]!["movement_mode"] == "following_formation" &&
                (string?)units[1]!["formation_leader_id"] == "own_unit_1" &&
                (string?)units[1]!["route_unit_id"] == "own_unit_1" &&
                (string?)units[2]!["movement_mode"] == "independent_in_formation" &&
                (string?)units[2]!["route_unit_id"] == "own_unit_3", "Formation following was mistaken for independent movement");
            var routes = (JArray)data["routes"]!;
            Require(routes.Count == 2 && (double?)routes[0]!["waypoints"]![0]!["position"]!["latitude_deg"] == 32 &&
                (double?)routes[0]!["waypoints"]![1]!["position"]!["latitude_deg"] == 33 &&
                (double?)routes[1]!["waypoints"]![0]!["position"]!["latitude_deg"] == 35,
                "Leader route was repeated, waypoint order was lost or the snapshot used changed live orders");
            Require(MovementQuery(session, "own_unit_2")["routes"]!.Count() == 0,
                "A follower re-delivered its leader's unchanged route");
            var fresh = new BattleQuerySession(snapshot, "en", "Follower only", information: AdvisorInformation.All);
            var followerOnly = MovementQuery(fresh, "own_unit_2");
            Require(followerOnly["units"]!.Count() == 1 && followerOnly["routes"]!.Count() == 1 &&
                (string?)followerOnly["routes"]![0]!["id"] == "own_unit_1", "A follower request required a separate leader round");
            var ownDetails = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray("own_unit_1") }));
            Require(ownDetails["results"]![0]!["data"]![0]!["movement_mode"] == null, "Basic unit retrieval bypassed optional movement retrieval");
        });
        check("Movement defaults to enabled for missing selections, saved choices persist and excluded units cannot leak routes", () =>
        {
            var kind = InformationKind.BattleMovementPlans;
            var oldSelections = AdvisorInformation.All.Data(); oldSelections.Remove("battle_movement_plans");
            Require(AdvisorInformation.Default.Allows(kind) && AdvisorInformation.ParseSelections(oldSelections).Allows(kind) &&
                AdvisorInformation.Parse(new JArray()).Allows(kind), "Missing movement selections did not use the enabled default");
            Require(!AdvisorInformation.Parse(new JArray("battle_movement_plans")).Allows(kind),
                "The enabled default overwrote an explicitly disabled legacy movement selection");
            foreach (var enabled in new[] { true, false })
                Require(AdvisorInformation.ParseSelections(AdvisorInformation.Default.With(kind, enabled).Data()).Allows(kind) == enabled,
                    "A saved movement preference was replaced by its default");
            var f = MovementFixture(out _, out var independent);
            f.Own._type = "LandUnit"; f.Own._obp._landUnitSubType = "Airbase";
            var session = new BattleQuerySession(f.Capture(), "en", "Movement", 20,
                information: AdvisorInformation.All.With(InformationKind.BattleBases, false));
            var data = MovementQuery(session, "own_unit_2", "own_unit_3");
            Require(data["units"]![0]!["formation_leader_id"]!.Type == JTokenType.Null &&
                data["units"]![0]!["route_unit_id"]!.Type == JTokenType.Null && data["routes"]!.Count() == 1,
                "An excluded leader's route or identity was disclosed through a follower");
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_1") }))["error"]!["code"] == "unknown_id",
                "A hidden unit remained addressable by a movement query");
            foreach (var policy in new[] { AdvisorInformation.All.With(kind, false), AdvisorInformation.All.With(InformationKind.BattleUnits, false) })
            {
                var off = new BattleQuerySession(f.Capture(), "en", "Movement", information: policy);
                Require(off.InitialInput()["knowledge_catalog"]!["movement_plans"] == null &&
                    !off.InitialInput().ToString().Contains("get_movement_plans", StringComparison.Ordinal) &&
                    !RequestSchemaFixture.Queries(off.InitialInput()["query_tool"]!).Contains("get_movement_plans") &&
                    (string?)off.Execute(Request(off, new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_3") }))["error"]!["code"] == "information_disabled",
                    "Movement or parent exclusions left another retrieval path available");
            }
        });
        check("Movement reads no unknown relative target and preserves course commands, empty plans and missing leaders", () =>
        {
            var f = new Fixture();
            var unknownRelative = new GoToRelativePoint { target = new Parameter<object?>(f.Secret), FailOnPosition = true };
            var ownRelative = new GoToRelativePoint { target = new Parameter<object?>(f.Own), WaypointPosition = new Geo(34, 134) };
            var course = new GoToWaypointTask { _setCourse = new Parameter<bool>(true), _course = new Parameter<double>(90), FailOnPosition = true };
            f.Own._userRoot = Root(unknownRelative, ownRelative, course, new object());
            var orphan = new OwnUnit { Formation = new MovementFormation(), _userRoot = new object() };
            var noOrders = new OwnUnit(); f.OwnUnits.Add(orphan); f.OwnUnits.Add(noOrders);
            var session = new BattleQuerySession(f.Capture(), "en", "Plans", information: AdvisorInformation.All);
            var data = MovementQuery(session, "own_unit_1", "own_unit_2", "own_unit_3");
            var points = data["routes"]![0]!["waypoints"]!;
            Require(points.Count() == 3 && points[0]!["position"]!.Type == JTokenType.Null &&
                points[0]!["relative_to_unit_id"]!.Type == JTokenType.Null &&
                (string?)points[1]!["relative_to_unit_id"] == "own_unit_1" &&
                (double?)points[1]!["position"]!["latitude_deg"] == 34 &&
                (string?)points[2]!["kind"] == "set_course" && (double?)points[2]!["course_deg"] == 90 &&
                points[2]!["position"]!.Type == JTokenType.Null, "Relative targets or deferred course commands became invented absolute destinations");
            Require(data["units"]![1]!["route_unit_id"]!.Type == JTokenType.Null &&
                (string?)data["units"]![2]!["movement_mode"] == "independent" &&
                data["routes"]![1]!["waypoints"]!.Count() == 0, "Missing leaders or no movement orders became spurious destinations");
        });
        check("Movement route sharing commits only complete valid query batches", () =>
        {
            var f = MovementFixture(out _, out _);
            var session = new BattleQuerySession(f.Capture(), "en", "Plans", 20, information: AdvisorInformation.All);
            var failed = session.Execute(Request(session,
                new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_2") },
                new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("observed_contact_1") }));
            Require((string?)failed["error"]!["code"] == "unknown_id", "An enemy route became queryable");
            var batch = session.Execute(Request(session,
                new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_2") },
                new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_1") }));
            Require(batch["results"]![0]!["data"]!["routes"]!.Count() == 1 &&
                batch["results"]![1]!["data"]!["routes"]!.Count() == 0, "A failed batch consumed route definitions or a successful batch repeated them");
        });
    }

    private static JObject MovementQuery(BattleQuerySession session, params string[] ids) =>
        (JObject)session.Execute(Request(session, new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray(ids) }))["results"]![0]!["data"]!;
    private static Fixture MovementFixture(out OwnUnit follower, out OwnUnit independent)
    {
        var f = new Fixture();
        var formation = new MovementFormation { Name = "自軍編隊", LeaderStation = new MovementStation { UnitObject = f.Own } };
        f.Own.Formation = formation;
        f.Own._userRoot = Root(new GoToWaypointTask { WaypointPosition = new Geo(32, 132) },
            new GoToWaypointTask { WaypointPosition = new Geo(33, 133) });
        // A following unit must not inspect its generated steering tasks at all.
        follower = new OwnUnit { Formation = formation, _userRoot = new object() };
        independent = new OwnUnit { Formation = formation,
            _userRoot = Root(new GoToWaypointTask { WaypointPosition = new Geo(35, 135) }) };
        independent.ActsIndependentlyInFormation.Value = true;
        f.OwnUnits.Add(follower); f.OwnUnits.Add(independent);
        return f;
    }
    private static MovementRoot Root(params object[] tasks) => new()
    { TaskViewModels = tasks.Select(t => new MovementView { Task = t }).ToList() };
    private sealed class MovementFormation
    {
        public string Name { get; set; } = "編隊";
        public MovementStation? LeaderStation { get; set; }
    }
    private sealed class MovementStation { public object? UnitObject { get; set; } }
    private sealed class MovementRoot { public List<MovementView> TaskViewModels { get; set; } = new(); }
    private sealed class MovementView { public object? Task { get; set; } }
    // NodeCanvas declares both BBParameter.value (object) and BBParameter<T>.value (T).
    private abstract class Parameter
    {
        public object? value { get => Boxed; set => Boxed = value; }
        protected abstract object? Boxed { get; set; }
    }
    private sealed class Parameter<T> : Parameter
    {
        public new T value { get; set; }
        protected override object? Boxed { get => value; set => this.value = (T)value!; }
        internal Parameter(T initial) { value = initial; }
    }
    private sealed class GoToWaypointTask
    {
        private Geo position = new(31, 131);
        internal bool FailOnPosition { get; set; }
        public Geo WaypointPosition { get => FailOnPosition ? throw new Exception("Placeholder position read") : position; set => position = value; }
        public bool _isAutogenerated => false;
        public Parameter<bool> _setCourse { get; set; } = new(false);
        public Parameter<bool> _setTurn { get; set; } = new(false);
        public Parameter<double> _course { get; set; } = new(0);
        public Parameter<bool> _toCurrentPosition { get; set; } = new(false);
        public Parameter<object?> _parentObject { get; set; } = new(null);
    }
    private sealed class GoToRelativePoint
    {
        private Geo position = new(31, 131);
        internal bool FailOnPosition { get; set; }
        public Geo WaypointPosition { get => FailOnPosition ? throw new Exception("Hidden relative parent read") : position; set => position = value; }
        public bool _isAutogenerated => false;
        public Parameter<object?> target { get; set; } = new(null);
        public Parameter<Geo?> targetPosition { get; set; } = new(null);
        public object? _patrolZone => null;
    }
}
