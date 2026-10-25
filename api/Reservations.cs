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

public sealed class MovementUnitInfo
{
    public GameUnitInfo Unit { get; }
    public string Kind { get; }
    public string? LeaderTarget { get; }
    public string? LeaderName { get; }
    public bool IsFormationLeader { get; }
    public bool IsFollowingFormation { get; }
    public IReadOnlyList<string> Speeds { get; }
    public IReadOnlyList<double> AltitudesFeet { get; }
    public IReadOnlyList<string> Depths { get; }
    public MovementUnitInfo(GameUnitInfo unit, string kind, string? leaderTarget, string? leaderName,
        bool isFormationLeader, bool isFollowingFormation, IEnumerable<string> speeds, IEnumerable<double> altitudesFeet, IEnumerable<string> depths)
    { Unit = unit; Kind = kind; LeaderTarget = leaderTarget; LeaderName = leaderName;
        IsFormationLeader = isFormationLeader; IsFollowingFormation = isFollowingFormation;
        Speeds = new List<string>(speeds).AsReadOnly(); AltitudesFeet = new List<double>(altitudesFeet).AsReadOnly(); Depths = new List<string>(depths).AsReadOnly(); }
}

public sealed class MovementWaypoint
{
    public AdvisorMapCoordinate Position { get; }
    public string? Speed { get; }
    public double? AltitudeFeet { get; }
    public string? Depth { get; }
    public MovementWaypoint(AdvisorMapCoordinate position, string? speed = null, double? altitudeFeet = null, string? depth = null)
    {
        if (altitudeFeet.HasValue && (double.IsNaN(altitudeFeet.Value) || double.IsInfinity(altitudeFeet.Value) || altitudeFeet < 0))
            throw new ArgumentOutOfRangeException(nameof(altitudeFeet));
        if (altitudeFeet.HasValue && depth != null) throw new ArgumentException("Choose altitude or depth.");
        Position = position; Speed = speed; AltitudeFeet = altitudeFeet; Depth = depth;
    }
}
