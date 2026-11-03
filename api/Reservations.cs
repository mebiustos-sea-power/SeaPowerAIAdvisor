using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

public enum ReservationPlacement { Append, Replace }

// Optional queue-composition contracts for commands declaring OperationExecutionMode.Reservation.
public interface IAdvisorReservationExtension
{
    bool EndsReservationSequence { get; }
    bool SupportsLoop { get; }
}

// A loop-capable segment reports one coordinate per native reservation it appends, in the same order.
// The host selects the repeat suffix without reading command-specific argument JSON.
public interface IAdvisorLoopableReservationOperation
{
    IReadOnlyList<AdvisorMapCoordinate> GetLoopPoints(ReservationOrder order);
}
public interface IAdvisorReservationOperation
{
    void ValidateReservations(IReadOnlyList<ReservationOrder> orders);
    ReservationPresentation PreparePresentation(ReservationOrder order);
    string ExecuteReservation(ReservationOrder order);
}

public sealed class ReservationOrder
{
    public OperationOrder Order { get; }
    public ReservationPlacement Placement { get; }
    public ReservationQueueState Queue { get; }
    // Validation, execution and presentation capture receive the original queue start; the preview delegate receives its live start separately.
    public AdvisorMapCoordinate? StartPosition { get; }
    public int Index { get; }
    public ReservationOrder(OperationOrder order, ReservationPlacement placement, ReservationQueueState queue,
        AdvisorMapCoordinate? startPosition, int index)
    { Order = order; Placement = placement; Queue = queue; StartPosition = startPosition; Index = index; }
}

// The host supplies the live route start separately from captured labels and proposed destinations.
public sealed class ReservationPresentation
{
    private readonly Func<string, OperationReview> describe;
    private readonly Func<AdvisorMapCoordinate?, string, ReservationPreview> preview;
    public ReservationPresentation(Func<string, OperationReview> describe, Func<AdvisorMapCoordinate?, string, ReservationPreview> preview)
    { this.describe = describe ?? throw new ArgumentNullException(nameof(describe)); this.preview = preview ?? throw new ArgumentNullException(nameof(preview)); }
    public OperationReview Describe(string language) => describe(language) ?? throw new InvalidOperationException("The reservation description is missing.");
    public ReservationPreview Preview(AdvisorMapCoordinate? start, string language) => preview(start, language) ?? throw new InvalidOperationException("The reservation preview is missing.");
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
    // Missing or ineligible targets return UnavailableReason; absent positions stay null.
    ReservationQueueState InspectReservations(string target, ReservationPlacement placement);
    // Called once, immediately before the target's first registration. Returns whether the queue/control changed.
    bool BeginReservations(string target, ReservationPlacement placement);
    // Called after all of this target's reservations were registered. Only this exact new suffix repeats.
    void SetReservationLoop(string target, IReadOnlyList<AdvisorMapCoordinate> points);
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
    // Applied on arrival using the game's formation-control rules, not when travel to this point starts.
    public RulesOfEngagement? RulesOfEngagementOnArrival { get; }
    public MovementWaypoint(AdvisorMapCoordinate position, string? speed = null, string? altitude = null, string? depth = null,
        RulesOfEngagement? rulesOfEngagementOnArrival = null)
    {
        if (altitude != null && depth != null) throw new ArgumentException("Choose altitude or depth.");
        if (rulesOfEngagementOnArrival.HasValue && !Enum.IsDefined(typeof(RulesOfEngagement), rulesOfEngagementOnArrival.Value))
            throw new ArgumentOutOfRangeException(nameof(rulesOfEngagementOnArrival));
        Position = position; Speed = speed; Altitude = altitude; Depth = depth;
        RulesOfEngagementOnArrival = rulesOfEngagementOnArrival;
    }
}
