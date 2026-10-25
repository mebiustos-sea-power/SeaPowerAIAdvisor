using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NodeCanvas.Framework;
using SeaPower;
using SeaPowerAIAdvisor.Api;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport : IAdvisorReservationSequenceSupport, IAdvisorMovementSupport
{
    private static readonly string[] ShipSpeeds = { "ahead_one_third", "ahead_two_thirds", "ahead_standard", "ahead_full", "ahead_flank" };
    private static readonly string[] PlaneSpeeds = { "loiter", "cruise", "max" };
    private static readonly string[] SubDepths = { "surface", "periscope", "shallow", "above_layer", "below_layer", "deep", "very_deep" };
    private const double FeetPerUnity = 220.4727;
    private static string? MovementUnavailableReason(ObjectBase unit) => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._userRoot == null ? "target_not_ready"
        : unit.CurrentOrder.Value.OrderType != Order.Type.None ? "current_order_conflict"
        : unit is LandUnit ground && !ground._isMobile ? "unit_not_mobile"
        : unit is Aircraft aircraft && !aircraft._isInFlight || unit is Helicopter helicopter && !helicopter._isInFlight ? "unit_not_airborne"
        : unit._patrolZone != null ? "patrol_assignment_conflict" : null;
    private static string Target(ObjectBase unit) => "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture);
    private static AdvisorMapCoordinate Coordinate(ObjectBase unit) => new(unit.Position.Value.Latitude, unit.Position.Value.Longitude);
    private static float[] Altitudes(ObjectBase unit) => unit is Aircraft plane ? plane.Ap._presetAltitudes
        : unit is Helicopter helicopter ? helicopter._hp._presetAltitudes : Array.Empty<float>();
    public IReadOnlyList<MovementUnitInfo> GetMovementUnits()
    {
        Require("battle_units"); Require("battle_movement_plans");
        var visible = VisibleUnits().ToArray();
        return visible.Select(unit =>
        {
            var leader = unit.Formation?.LeaderStation?.UnitObject;
            if (leader != null && !visible.Contains(leader)) leader = null;
            var kind = unit is Submarine ? "submarine" : unit is Vessel ? "vessel" : unit is Aircraft ? "aircraft"
                : unit is Helicopter ? "helicopter" : unit is LandUnit ? "ground_unit" : "other";
            var reason = MovementUnavailableReason(unit);
            if (kind == "other") reason ??= "movement_not_supported";
            return new MovementUnitInfo(new GameUnitInfo(unit.Id, unit.Name.Value, Coordinate(unit), unit.IsAirUnit,
                unit is Aircraft plane ? plane._isInFlight : unit is Helicopter helicopter && helicopter._isInFlight, reason),
                kind, leader == null ? null : Target(leader), leader?.Name.Value, unit.IsFormationLeader,
                !unit.IsFormationLeader && unit.FollowingFormation,
                unit is Vessel || unit is Submarine ? ShipSpeeds : unit is Aircraft ? PlaneSpeeds : Array.Empty<string>(),
                Altitudes(unit).Select(a => Math.Round(a * FeetPerUnity)), unit is Submarine ? SubDepths : Array.Empty<string>());
        }).ToArray();
    }
    // Only a native, non-reachable relative follower task is discarded implicitly. Mixed/unknown queues require explicit replacement.
    private static bool IsOnlyFormationFollow(ObjectBase unit)
    {
        var leader = unit.Formation?.LeaderStation?.UnitObject;
        return leader != null && unit._userRoot.TaskViewModels.Count > 0 && unit._userRoot.TaskViewModels.All(view =>
            view?.Task is GoToRelativePoint relative && relative.target.value == leader && !relative._isReachable);
    }
    public ReservationQueueState InspectReservations(string target, ReservationPlacement placement)
    {
        var unit = Unit(target); var reason = MovementUnavailableReason(unit);
        var follows = unit.FollowingFormation && !unit.IsFormationLeader;
        if (reason == null && follows && placement == ReservationPlacement.Append && !IsOnlyFormationFollow(unit))
            reason = "formation_reservations_not_separable";
        if (reason == null && !follows && placement == ReservationPlacement.Append)
        {
            var tasks = unit._userRoot.TaskViewModels.Select(v => v.Task).OfType<VisualActionTask>().ToArray();
            if (tasks.Any(t => t._returnToFormation)) reason = "reservation_after_terminal_operation";
            else if (tasks.Any(t => t._waypointsData.Count > 0)) reason = "reservation_loop_conflict";
        }
        var current = Coordinate(unit);
        var end = !follows && information().Allows(AdvisorInformation.Items.Single(i => i.Id == "battle_movement_plans").Kind)
            ? BattleReservationReader.ReadRoute(unit)?.EndPosition : null;
        return new ReservationQueueState(current, end, follows, reason);
    }
    public bool BeginReservations(string target, ReservationPlacement placement)
    {
        var state = InspectReservations(target, placement);
        if (state.UnavailableReason is string reason) throw new OperationException(409, reason);
        var unit = Unit(target); var changed = false;
        try
        {
            if (placement == ReservationPlacement.Replace || state.BecomesIndependent)
            { unit.RemoveWaypoints(); changed = true; }
            if (state.BecomesIndependent)
            { unit.Formation.MakeUnitActIndependently(unit); changed = true; }
        }
        catch (Exception error) when (changed)
        { throw new InvalidOperationException("Reservation queue preparation failed after changes.", error); }
        return changed;
    }
    public void AppendMovement(string target, MovementWaypoint waypoint)
    {
        Require("battle_movement_plans"); var unit = Unit(target);
        var info = GetMovementUnits().Single(u => u.Unit.Target == target);
        if (info.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        if (unit.FollowingFormation && !unit.IsFormationLeader) throw new OperationException(409, "following_formation");
        if (waypoint.Speed != null && !info.Speeds.Contains(waypoint.Speed) ||
            waypoint.AltitudeFeet.HasValue && !info.AltitudesFeet.Contains(waypoint.AltitudeFeet.Value) ||
            waypoint.Depth != null && !info.Depths.Contains(waypoint.Depth)) throw new OperationException(400, "movement_setting_not_supported");
        AddMovement(unit, waypoint, false);
    }
    private static void AddMovement(ObjectBase unit, MovementWaypoint waypoint, bool returnToFormation)
    {
        var position = new GeoPosition(waypoint.Position.Latitude, waypoint.Position.Longitude, 0);
        var heightState = WaypointData.WaypointHeightState.NoChange;
        if (waypoint.AltitudeFeet.HasValue)
        {
            position._height = Altitudes(unit).Single(a => Math.Round(a * FeetPerUnity) == waypoint.AltitudeFeet.Value);
            heightState = WaypointData.WaypointHeightState.Number;
        }
        if (waypoint.Depth != null)
            heightState = waypoint.Depth switch
            {
                "surface" => WaypointData.WaypointHeightState.Surface, "periscope" => WaypointData.WaypointHeightState.Periscope,
                "shallow" => WaypointData.WaypointHeightState.VeryLow, "above_layer" => WaypointData.WaypointHeightState.Low,
                "below_layer" => WaypointData.WaypointHeightState.Medium, "deep" => WaypointData.WaypointHeightState.High,
                "very_deep" => WaypointData.WaypointHeightState.VeryHigh,
                _ => throw new OperationException(400, "movement_setting_not_supported")
            };
        var task = NodeCanvas.Framework.Task.Create<GoToWaypointTask>(unit._userRoot.graph);
        task._baseObject = unit; task._waypointGeoPos = position; task.wpName = "wp";
        task._heightState = heightState; task._isReachable = true; task._returnToFormation = returnToFormation;
        if (waypoint.Speed != null)
        {
            var index = Array.IndexOf(unit is Aircraft ? PlaneSpeeds : ShipSpeeds, waypoint.Speed);
            task._setSpeed = true; task._speedState = (ObjectSpeed.SpeedSettings)(unit is Aircraft ? index + 2 : index + 1);
        }
        // Append explicitly: the standard factory inserts after the UI-selected waypoint instead.
        unit._userRoot.AddTask(task);
        if (!unit._userRoot.TaskViewModels.Any(v => ReferenceEquals(v?.Task, task)))
            throw new InvalidOperationException("The movement reservation was not added to the native queue.");
    }
    public void AppendFormationReturn(string target)
    {
        Require("battle_movement_plans"); var unit = Unit(target);
        if (MovementUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        var leader = unit.Formation?.LeaderStation?.UnitObject;
        if (leader == null || leader == unit || !VisibleUnits().Contains(leader)) throw new OperationException(409, "formation_return_unavailable");
        var route = BattleReservationReader.ReadRoute(unit) ?? throw new OperationException(409, "target_not_ready");
        // A final native waypoint triggers the return only after preceding travel/drop tasks finish; no polling or AI callback.
        AddMovement(unit, new MovementWaypoint(route.EndPosition ?? route.CurrentPosition), true);
    }
}
