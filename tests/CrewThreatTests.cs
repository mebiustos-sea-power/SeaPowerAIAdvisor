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
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Unit
    {
        public Missile? ThreateningMissile { get; set; }
        public Torpedo? ThreatingTorpedo { get; set; }
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
}
