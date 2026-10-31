using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

internal static class CrewThreatTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Crew current warning data retains missile and torpedo types independently", () =>
        {
            var unit = new Unit();
            Require(UnitThreatFacts.ActiveAlerts(unit).Count == 0, "An unthreatened unit received a warning");
            unit.ThreateningMissile = new Missile { CurrentTarget = unit };
            Require(UnitThreatFacts.ActiveAlerts(unit).Values<string>().SequenceEqual(new[] { "missile" }), "The missile warning was omitted");
            unit.ThreatingTorpedo = new Torpedo();
            var captured = UnitThreatFacts.ActiveAlerts(unit);
            Require(captured.Values<string>().SequenceEqual(new[] { "missile", "torpedo" }), "Warning types were collapsed or one suppressed the other");
            unit.ThreateningMissile = null;
            Require(UnitThreatFacts.ActiveAlerts(unit).Values<string>().SequenceEqual(new[] { "torpedo" }), "Clearing one warning also cleared the other");
            unit.ThreatingTorpedo = null;
            Require(UnitThreatFacts.ActiveAlerts(unit).Count == 0 && captured.Count == 2, "Old warnings persisted or a native change mutated the snapshot");
        });
        check("Crew missile warnings follow the game's target condition and ignore destroyed threats", () =>
        {
            var unit = new Unit { ThreateningMissile = new Missile { CurrentTarget = new object() } };
            Require(UnitThreatFacts.ActiveAlerts(unit).Count == 0, "A missile aimed at another unit became a warning for this unit");
            unit.ThreateningMissile.CurrentTarget = unit;
            Require(UnitThreatFacts.ActiveAlerts(unit).Count == 1, "The current target did not receive its warning");
            unit.ThreateningMissile.IsDestroyed = true; unit.ThreatingTorpedo = new Torpedo { IsDestroyed = true };
            Require(UnitThreatFacts.ActiveAlerts(unit).Count == 0, "Destroyed weapons remained active warnings");
        });
        check("Crew illumination and jamming warnings use current UI state and preserve other warning types", () =>
        {
            var unit = new Unit { _rwrBearings = new[] { 30f, 80f },
                _ECMSystemsTryingToJamMe = new[] { new Jammer("Offensive"), new Jammer("Universal") } };
            unit.ThreateningMissile = new Missile { CurrentTarget = unit }; unit.ThreatingTorpedo = new Torpedo();
            var captured = UnitThreatFacts.ActiveAlerts(unit);
            Require(captured.Values<string>().SequenceEqual(new[] { "missile", "torpedo", "radar_illumination", "jamming" }),
                "Current warning types were lost, duplicated or collapsed");
            unit._rwrBearings = Array.Empty<float>(); unit._ECMSystemsTryingToJamMe = Array.Empty<Jammer>();
            Require(UnitThreatFacts.ActiveAlerts(unit).Values<string>().SequenceEqual(new[] { "missile", "torpedo" }) && captured.Count == 4,
                "Cleared warning state persisted or changed the captured snapshot");
        });
        check("Crew jamming detection excludes defensive, destroyed, ownerless and inoperable sources", () =>
        {
            var excluded = new[] { new Jammer("Defensive"), new Jammer("Offensive") { IsDestroyed = true },
                new Jammer("Universal") { _baseObject = null }, new Jammer("Offensive") { Inoperable = new Box<bool>(true) } };
            foreach (var jammer in excluded)
                Require(UnitThreatFacts.ActiveAlerts(new Unit { _ECMSystemsTryingToJamMe = new[] { jammer } }).Count == 0,
                    "A source excluded by the game's warning condition produced a warning");
            var unit = new Unit { _ECMSystemsTryingToJamMe = excluded.Concat(new[] { new Jammer("Universal") }).ToArray() };
            Require(UnitThreatFacts.ActiveAlerts(unit).Values<string>().SequenceEqual(new[] { "jamming" }), "An active universal jammer was not recognized");
        });
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Unit
    {
        public Missile? ThreateningMissile { get; set; }
        public Torpedo? ThreatingTorpedo { get; set; }
        public float[] _rwrBearings { get; set; } = Array.Empty<float>();
        public Jammer[] _ECMSystemsTryingToJamMe { get; set; } = Array.Empty<Jammer>();
    }
    private sealed class Missile
    {
        public object? CurrentTarget { get; set; }
        public bool IsDestroyed { get; set; }
        public object CurrentIntendedTargetObject => throw new InvalidOperationException("Warning capture read a command instead of the game's alert state");
    }
    private sealed class Torpedo
    {
        public bool IsDestroyed { get; set; }
        public object CurrentTarget => throw new InvalidOperationException("Torpedo warning incorrectly required a target lock");
    }
    private sealed class Jammer
    {
        public object? _baseObject { get; set; } = new object();
        public bool IsDestroyed { get; set; }
        public Box<bool> Inoperable { get; set; } = new(false);
        public Ecm _ecm { get; }
        public Jammer(string kind) => _ecm = new Ecm(kind);
    }
    private sealed class Ecm
    {
        public Parameters _ep { get; }
        public Ecm(string kind) => _ep = new Parameters(kind);
    }
    private sealed class Parameters
    {
        public string _type { get; }
        public Parameters(string kind) => _type = kind;
    }
    private sealed class Box<T>
    {
        public T Value { get; }
        public Box(T value) => Value = value;
    }
}
