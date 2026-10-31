using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Crew;

internal static class CrewWeaponTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Crew separates weapon contacts and groups active weapons without reading their motion", () =>
        {
            var own = new Platform("California"); var enemy = new HiddenWeapon();
            var first = new OwnWeapon("missile", own, enemy); var second = new OwnWeapon("missile", own, enemy);
            var map = new[] { Map(1, own, "vessel", "friendly"), Map(7017, enemy, "vessel", "enemy"),
                Map(801, new HiddenWeapon { Target = own }, "missile", "enemy"), Map(802, new HiddenWeapon { Target = own }, "missile", "enemy"),
                Map(803, new HiddenWeapon(), "torpedo", "unknown") };
            var result = CrewContactReader.Capture(map, new object[] { own, first, second, new OwnWeapon("torpedo", own, enemy),
                new OwnWeapon("missile", own, enemy) { Launched = false }, new OwnWeapon("missile", own, enemy) { IsDestroyed = true },
                new OwnWeapon("missile", own, enemy) { _flightStage = "debris" } });
            Require(result.Contacts.Count == 1 && (int?)result.Contacts[0]!["track_number"] == 7017, "Weapons were duplicated in ordinary contacts");
            var friendly = result.LaunchedWeapons.Single(w => (string?)w["posture"] == "friendly" && (string?)w["kind"] == "missile");
            Require((int?)friendly["count"] == 2 && (string?)friendly["launch_platform"]!["display_name"] == "California" &&
                (string?)friendly["tracked_target"]!["id"] == "unit:7017", "Own weapons lost their count, source or known target");
            Require(result.LaunchedWeapons.Count == 4 && (int?)result.LaunchedWeapons.Single(w => (string?)w["posture"] == "enemy")["count"] == 2,
                "Grouping merged allegiance or weapon kinds, or retained inactive weapons");
            own.Name.Value = "Changed"; first.Target = null;
            Require((string?)friendly["launch_platform"]!["display_name"] == "California" && (string?)friendly["tracked_target"]!["id"] == "unit:7017",
                "Native changes altered already captured facts");
        });
        check("Crew weapon tracking respects map disclosure and never uses hidden enemy orders", () =>
        {
            var own = new Platform("Own"); var enemy = new HiddenWeapon { Target = own };
            var weapon = Map(801, enemy, "missile", "enemy");
            var map = new[] { Map(1, own, "vessel", "friendly"), weapon };
            JToken Capture() => CrewContactReader.Capture(map, new[] { own }).LaunchedWeapons.Single();
            Require((string?)Capture()["tracked_target"]!["id"] == "unit:1", "Visible tracking was not supplied");
            weapon.ObjectViewModel!.LineToTargetColor.A = 0; enemy.ForbidEcho = true;
            Require(Capture()["tracked_target"]!.Type == JTokenType.Null, "A hidden line disclosed targeting");
            weapon.ObjectViewModel.LineToTargetColor.A = 255;
            weapon.ObjectViewModel.LineToTarget = new object[] { new Geo(1, 2), new Geo(1, 362) };
            Require(Capture()["tracked_target"]!.Type == JTokenType.Null, "A zero-length line disclosed targeting after longitude wrapping");
            weapon.ObjectViewModel.LineToTarget = new object[] { new Geo(1, 2), new Geo(3, 4) }; enemy.Reportable = false;
            Require(Capture()["tracked_target"]!.Type == JTokenType.Null, "A non-reportable line disclosed targeting");
            enemy.Reportable = true; enemy.ForbidEcho = false; enemy.Target = new object();
            Require(Capture()["tracked_target"]!.Type == JTokenType.Null, "An unknown target received an invented identity");
            weapon.Unit.IsDormant.Value = true;
            Require(CrewContactReader.Capture(map, new[] { own }).LaunchedWeapons.Count == 0, "A dormant contact became an active weapon");
            weapon.Unit.IsDormant.Value = false; weapon.Unit.BaseObject.IsDestroyed = true;
            Require(CrewContactReader.Capture(map, new[] { own }).LaunchedWeapons.Count == 0, "A destroyed weapon was retained");
        });
        check("Crew weapon and ordinary-contact choices are independent and survive saving", () =>
        {
            var own = new Platform("Own"); var enemy = new HiddenWeapon();
            var captured = CrewContactReader.Capture(new[] { Map(1, own, "vessel", "friendly"), Map(7017, enemy, "vessel", "enemy"),
                Map(801, new HiddenWeapon(), "missile", "enemy") }, new[] { own });
            var snapshot = new JObject { ["units"] = new JArray(), ["contacts"] = captured.Contacts, ["launched_weapons"] = captured.LaunchedWeapons };
            foreach (var contacts in new[] { false, true })
            foreach (var weapons in new[] { false, true })
            {
                var selection = CrewInformation.Default.With("contacts", contacts).With("launched_weapons", weapons);
                var saved = CrewSettings.Parse(new CrewSettings(information: selection).Json().ToString()).Information;
                var filtered = saved.Filter(snapshot);
                Require((filtered["contacts"] != null) == contacts && (filtered["launched_weapons"] != null) == weapons &&
                    snapshot["contacts"]!.Count() == 1 && snapshot["launched_weapons"]!.Count() == 1,
                    "Saving or filtering confused contacts and weapons, or mutated the snapshot");
            }
        });
        check("An unknown own launcher or inactive seeker is not replaced by a commanded target", () =>
        {
            var own = new Platform("Own"); var weapon = new OwnWeapon("missile", new object(), new object());
            var map = new[] { Map(1, own, "vessel", "friendly") };
            var first = CrewContactReader.Capture(map, new object[] { own, weapon }).LaunchedWeapons.Single();
            Require(first["launch_platform"]!.Type == JTokenType.Null && first["tracked_target"]!.Type == JTokenType.Null, "An unknown relationship became a known unit");
            weapon.Target = own; weapon._seeker.Active = false;
            Require(CrewContactReader.Capture(map, new object[] { own, weapon }).LaunchedWeapons.Single()["tracked_target"]!.Type == JTokenType.Null,
                "An inactive seeker produced a tracking claim");
        });
    }

    private static MapUnit Map(int id, object identity, string kind, string posture) => new(id, identity, kind, posture);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Box<T> { public T Value { get; set; } public Box(T value) => Value = value; }
    private sealed class Platform
    {
        public string _type => "vessel";
        public Box<string> Name { get; }
        internal Platform(string name) => Name = new(name);
    }
    private sealed class OwnWeapon
    {
        public string _type { get; }
        public bool Launched { get; set; } = true;
        public bool IsDestroyed { get; set; }
        public string _flightStage { get; set; } = "flight";
        public object _launchPlatform { get; }
        public object? Target { get; set; }
        public Seeker _seeker { get; } = new();
        public object? CurrentStrongestTargetEcho => Target == null ? null : new Echo(Target);
        public object CurrentIntendedTargetObject => throw new InvalidOperationException("Crew read a commanded target instead of tracking");
        public object Position => throw new InvalidOperationException("Crew read weapon coordinates");
        public double _velocityInKnots => throw new InvalidOperationException("Crew read weapon speed");
        public double Heading => throw new InvalidOperationException("Crew read weapon heading");
        public bool isLaunched() => Launched;
        internal OwnWeapon(string kind, object launcher, object? target) { _type = kind; _launchPlatform = launcher; Target = target; }
    }
    private sealed class HiddenWeapon
    {
        public object? Target { get; set; }
        public bool Reportable { get; set; } = true;
        public bool ForbidEcho { get; set; }
        public object? CurrentStrongestTargetEcho => ForbidEcho ? throw new InvalidOperationException("Read a hidden echo") : Target == null ? null : new Echo(Target);
        public bool TargetedAtUnitWhichCanReportDetection() => Reportable;
        public object CurrentIntendedTargetObject => throw new InvalidOperationException("Read an enemy order");
        public object _launchPlatform => throw new InvalidOperationException("Read an enemy launcher");
        public object Position => throw new InvalidOperationException("Read enemy coordinates");
        public object Name => throw new InvalidOperationException("Read an enemy name");
    }
    private sealed class Seeker { public bool Active { get; set; } = true; public bool isActive() => Active; }
    private sealed class Echo { public object _objectBase { get; } public Echo(object target) => _objectBase = target; }
    private sealed class MapUnit
    {
        public Track Unit { get; }
        public string VisualType { get; }
        public string VisualPosture { get; }
        public WeaponModel? ObjectViewModel { get; }
        internal MapUnit(int id, object identity, string kind, string posture)
        { Unit = new(id, identity); VisualType = kind; VisualPosture = posture; ObjectViewModel = kind is "missile" or "torpedo" ? new(identity) : null; }
    }
    private sealed class Track
    {
        public int Id { get; }
        public object Object { get; }
        public Status BaseObject { get; } = new();
        public Box<bool> IsDormant { get; } = new(false);
        public Box<bool> Identified { get; } = new(false);
        public object? Class => null;
        public object? PositionEstimate => null;
        public object? UnityVelocityVector => null;
        internal Track(int id, object identity) { Id = id; Object = identity; }
    }
    private sealed class Status { public bool IsDestroyed { get; set; } }
    private sealed class WeaponModel
    {
        public object WeaponBase { get; }
        public Color LineToTargetColor { get; } = new();
        public object[] LineToTarget { get; set; } = { new Geo(1, 2), new Geo(3, 4) };
        internal WeaponModel(object weapon) => WeaponBase = weapon;
    }
    private sealed class Color { public int A { get; set; } = 255; }
    private sealed class Geo
    {
        public double Latitude { get; }
        public double Longitude { get; }
        internal Geo(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; }
    }
}
