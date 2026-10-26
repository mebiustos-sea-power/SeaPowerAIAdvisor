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
    private static string? MovementUnavailableReason(ObjectBase unit) => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._userRoot == null ? "target_not_ready"
        : unit.CurrentOrder.Value.OrderType != Order.Type.None ? "current_order_conflict"
        : unit is LandUnit ground && !ground._isMobile ? "unit_not_mobile"
        : unit is Aircraft aircraft && !aircraft._isInFlight || unit is Helicopter helicopter && !helicopter._isInFlight ? "unit_not_airborne"
        : unit._patrolZone != null ? "patrol_assignment_conflict"
        : unit is Vessel or Submarine or Aircraft or Helicopter or LandUnit ? null : "movement_not_supported";
    private static string Target(ObjectBase unit) => "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture);
    private static AdvisorMapCoordinate Coordinate(ObjectBase unit) => new(unit.Position.Value.Latitude, unit.Position.Value.Longitude);
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
            return new MovementUnitInfo(new GameUnitInfo(unit.Id, unit.Name.Value, Coordinate(unit), unit.IsAirUnit,
                unit is Aircraft plane ? plane._isInFlight : unit is Helicopter helicopter && helicopter._isInFlight, reason),
                kind, leader == null ? null : Target(leader), leader?.Name.Value, unit.IsFormationLeader,
                !unit.IsFormationLeader && unit.FollowingFormation,
                BattleMovementPresets.Speeds(unit), BattleMovementPresets.Altitudes(unit), BattleMovementPresets.Depths(unit));
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
        if (MovementUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        if (unit.FollowingFormation && !unit.IsFormationLeader) throw new OperationException(409, "following_formation");
        AddMovement(unit, waypoint, false);
    }
    private static void AddMovement(ObjectBase unit, MovementWaypoint waypoint, bool returnToFormation)
    {
        var position = new GeoPosition(waypoint.Position.Latitude, waypoint.Position.Longitude, 0);
        var heightState = WaypointData.WaypointHeightState.NoChange;
        if (waypoint.Altitude != null)
        {
            position._height = BattleMovementPresets.AltitudeValue(unit, waypoint.Altitude);
            heightState = WaypointData.WaypointHeightState.Number;
        }
        if (waypoint.Depth != null) heightState = BattleMovementPresets.DepthState(unit, waypoint.Depth);
        // Resolve every requested setting before creating or appending a task.
        var speedState = waypoint.Speed == null ? (ObjectSpeed.SpeedSettings?)null : BattleMovementPresets.SpeedState(unit, waypoint.Speed);
        var task = NodeCanvas.Framework.Task.Create<GoToWaypointTask>(unit._userRoot.graph);
        task._baseObject = unit; task._waypointGeoPos = position; task.wpName = "wp";
        task._heightState = heightState; task._isReachable = true; task._returnToFormation = returnToFormation;
        if (speedState.HasValue) { task._setSpeed = true; task._speedState = speedState.Value; }
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
