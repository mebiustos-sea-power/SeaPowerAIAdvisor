using System;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAIAdvisor;

// Created only from the player's current taskforce; no map selection or enemy objects are required.
internal sealed class BattleReservationUnit : IAdvisorReservationUnit
{
    private readonly ObjectBase unit;
    internal BattleReservationUnit(ObjectBase unit) => this.unit = unit;
    public int Id => unit.Id;
    public string Name => unit.Name.Value;
    public string? UnavailableReason => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._userRoot == null ? "target_not_ready" : null;

    public JArray Reservations()
    {
        var result = new JArray();
        foreach (var view in unit._userRoot.TaskViewModels)
        {
            var task = view?.Task ?? throw new InvalidOperationException("A native reservation task is unavailable.");
            var row = new JObject { ["sequence"] = result.Count + 1, ["task_type"] = task.GetType().Name,
                ["kind"] = "other", ["position"] = JValue.CreateNull() };
            if (task is GoToWaypointTask waypoint)
            {
                row["kind"] = "waypoint";
                if (waypoint._setCourse.value || waypoint._setTurn.value)
                {
                    row["kind"] = waypoint._setTurn.value ? "turn" : "set_course";
                    row["course_deg"] = waypoint._course.value;
                }
                else if (waypoint._toCurrentPosition.value) row["kind"] = "to_unit_position";
                else row["position"] = Position(waypoint._waypointGeoPos.value);
            }
            else if (task is GoToRelativePoint) row["kind"] = "relative_waypoint";
            if (task is AttackAtWaypoint attack)
            {
                row["kind"] = attack._isSonobouyDrop ? "sonobuoy_drop" : "attack_at_waypoint";
                row["ammunition"] = attack._ammunitionName;
                row["count"] = attack._salvo;
                row["attack_position"] = Position(attack._targetGeoPosition);
            }
            result.Add(row);
        }
        return result;
    }
    // Use the game's complete removal path so nodes and their map/UI models are disposed together.
    public void ClearReservations() => unit.RemoveWaypoints();
    private static JObject Position(GeoPosition position) => AdvisorInputBuilder.Position(
        new MapPosition(position.Latitude, position.Longitude));
}
