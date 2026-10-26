using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

public enum ReservationPlacement { Append, Replace }

// Optional extension/handler contracts for commands that append to the same native reservation queue.
public interface IAdvisorReservationExtension { bool EndsReservationSequence { get; } }
public interface IAdvisorReservationOperation
{
    void ValidateReservations(IReadOnlyList<ReservationOrder> orders);
    string DescribeReservation(ReservationOrder order, string language);
    ReservationPreview PreviewReservation(ReservationOrder order, string language);
    string ExecuteReservation(ReservationOrder order);
}

public sealed class ReservationOrder
{
    public OperationOrder Order { get; }
    public ReservationPlacement Placement { get; }
    public ReservationQueueState Queue { get; }
    // Preview calls receive the preceding planned endpoint; validation/execution receive the original queue start.
    public AdvisorMapCoordinate? StartPosition { get; }
    public int Index { get; }
    public ReservationOrder(OperationOrder order, ReservationPlacement placement, ReservationQueueState queue,
        AdvisorMapCoordinate? startPosition, int index)
    { Order = order; Placement = placement; Queue = queue; StartPosition = startPosition; Index = index; }
}

public sealed class ReservationPreview
{
    public AdvisorMapPreview Preview { get; }
    public AdvisorMapCoordinate? EndPosition { get; }
    public ReservationPreview(AdvisorMapPreview preview, AdvisorMapCoordinate? endPosition)
    { Preview = preview; EndPosition = endPosition; }
}

public sealed class ReservationQueueState
{
    public AdvisorMapCoordinate? CurrentPosition { get; }
    public AdvisorMapCoordinate? EndPosition { get; }
    public bool BecomesIndependent { get; }
    public string? UnavailableReason { get; }
    public ReservationQueueState(AdvisorMapCoordinate? currentPosition, AdvisorMapCoordinate? endPosition,
        bool becomesIndependent, string? unavailableReason = null)
    { CurrentPosition = currentPosition; EndPosition = endPosition; BecomesIndependent = becomesIndependent; UnavailableReason = unavailableReason; }
}

public interface IAdvisorReservationSequenceSupport
{
    ReservationQueueState InspectReservations(string target, ReservationPlacement placement);
    // Called once, immediately before the target's first registration. Returns whether the queue/control changed.
    bool BeginReservations(string target, ReservationPlacement placement);
}

public interface IAdvisorMovementSupport
{
    IReadOnlyList<MovementUnitInfo> GetMovementUnits();
    void AppendMovement(string target, MovementWaypoint waypoint);
    void AppendFormationReturn(string target);
}

// A detached, unit-specific preset. Value is a target/reference in Unit, resolved under current conditions.
// Select by Id: relative settings (for example below the thermocline) retain their native meaning at activation.
public sealed class MovementPreset
{
    public string Id { get; }
    public string Name { get; }
    public double Value { get; }
    public string Unit { get; }
    public MovementPreset(string id, string name, double value, string unit)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("A movement preset needs an ID, name and unit.");
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Id = id; Name = name; Value = value; Unit = unit;
    }
}

public sealed class MovementUnitInfo
{
    public GameUnitInfo Unit { get; }
    public string Kind { get; }
    public string? LeaderTarget { get; }
    public string? LeaderName { get; }
    public bool IsFormationLeader { get; }
    public bool IsFollowingFormation { get; }
    public IReadOnlyList<MovementPreset> Speeds { get; }
    public IReadOnlyList<MovementPreset> Altitudes { get; }
    public IReadOnlyList<MovementPreset> Depths { get; }
    public MovementUnitInfo(GameUnitInfo unit, string kind, string? leaderTarget, string? leaderName,
        bool isFormationLeader, bool isFollowingFormation, IEnumerable<MovementPreset> speeds, IEnumerable<MovementPreset> altitudes, IEnumerable<MovementPreset> depths)
    { Unit = unit; Kind = kind; LeaderTarget = leaderTarget; LeaderName = leaderName;
        IsFormationLeader = isFormationLeader; IsFollowingFormation = isFollowingFormation;
        Speeds = new List<MovementPreset>(speeds).AsReadOnly(); Altitudes = new List<MovementPreset>(altitudes).AsReadOnly(); Depths = new List<MovementPreset>(depths).AsReadOnly(); }
}

public sealed class MovementWaypoint
{
    public AdvisorMapCoordinate Position { get; }
    public string? Speed { get; }
    public string? Altitude { get; }
    public string? Depth { get; }
    public MovementWaypoint(AdvisorMapCoordinate position, string? speed = null, string? altitude = null, string? depth = null)
    {
        if (altitude != null && depth != null) throw new ArgumentException("Choose altitude or depth.");
        Position = position; Speed = speed; Altitude = altitude; Depth = depth;
    }
}
