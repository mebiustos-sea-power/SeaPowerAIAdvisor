using System;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class BattleOperationUnitTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Several unmapped own objects cannot leak unit:-1 into command candidates or be addressed directly", () =>
        {
            var unit = new Vessel();
            var unmapped = new ObjectBase { Id = -1, _type = "landunit" };
            var other = new ObjectBase { Id = -1 };
            var weapon = new WeaponBase { Id = 99, _type = "landunit", _obp = null! };
            var game = new BattleOperationSupport(new ObjectBase[] { unit, unmapped, other, weapon });
            var candidates = game.GetSensorUnits().ToDictionary(u => u.Unit.Target);
            Require(candidates.Keys.SequenceEqual(new[] { "unit:42" }),
                "Objects without player-map identities entered the common command target set");
            unmapped._obp = null!;
            Require(game.GetSensorUnits().Count == 1, "An unmapped object's parameters were inspected");
            RejectUnavailable(() => game.SetSensorControls("unit:-1", Array.Empty<SensorControlChange>()));
            Require(unit.EmconRequests == 0 && other.EmconRequests == 0, "Candidate lookup or rejection mutated a unit");
        });
        check("Command target resolution rechecks the player-map identity before execution", () =>
        {
            var unit = new Vessel(); var radar = new SensorSystemRadar { _sectionName = "search" };
            unit._obp._sensorSystems.Add(radar); unit._obp._searchRadarSystems.Add(radar);
            var game = new BattleOperationSupport(new[] { unit });
            Require(game.GetSensorUnits().Single().Unit.Target == "unit:42", "A mapped unit was excluded");
            unit.Id = -1;
            Require(game.GetSensorUnits().Count == 0, "An unmapped unit remained a candidate");
            RejectUnavailable(() => game.SetSensorControls("unit:42", new[] { new SensorControlChange("search.power", "on") }));
            Require(radar.Executed.Count == 0, "A stale target still received an order");
            unit.Id = 43;
            Require(game.GetSensorUnits().Single().Unit.Target == "unit:43", "A newly mapped unit could not become a candidate");
        });
        check("Common command target selection retains own ground/base information permissions", () =>
        {
            var ship = new Vessel(); var ground = new ObjectBase { Id = 43, _type = "landunit" };
            var basis = new ObjectBase { Id = 44, _type = "landunit" }; basis._obp._landUnitSubType = "airbase";
            var units = new ObjectBase[] { ship, ground, basis };
            var information = AdvisorInformation.All.With(InformationKind.BattleGroundUnits, false);
            var game = new BattleOperationSupport(units, information);
            Require(game.GetSensorUnits().Select(u => u.Unit.Target).SequenceEqual(new[] { "unit:42", "unit:44" }),
                "Common target selection bypassed disabled ground information");
            game = new BattleOperationSupport(units, information.With(InformationKind.BattleBases, false));
            Require(game.GetSensorUnits().Single().Unit.Target == "unit:42", "Disabled base information remained a command target");
        });
    }
    private static void RejectUnavailable(Action action)
    { try { action(); } catch (OperationException e) when (e.Code == "target_unavailable") { return; } throw new InvalidOperationException("An unavailable target was accepted"); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
