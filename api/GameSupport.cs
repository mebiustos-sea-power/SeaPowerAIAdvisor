using System.Collections.Generic;
using System.Globalization;

namespace SeaPowerAIAdvisor.Api;

// Current, player-known facts. Every method runs on the owning game thread and expires with its battle.
// Information categories use the same IDs as Data for AI (battle_units, battle_weapons, battle_movement_plans).
public interface IAdvisorGameSupport
{
    bool IsInformationEnabled(string category);
    IReadOnlyList<GameUnitInfo> GetUnits();
    IReadOnlyList<AmmunitionInfo> GetAmmunition(string target);
    ReservationRoute? GetReservationRoute(string target);
    string GetSonobuoyDepth(string target);
    void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position);
}

// Optional reservation control; existing game-support implementations need not provide it.
public interface IAdvisorReservationSupport
{
    void ClearReservations(string target);
}

// Detached values: commands decide applicability; the host does not filter this list by command or aircraft type.
public sealed class GameUnitInfo
{
    public int Id { get; }
    public string Target => "unit:" + Id.ToString(CultureInfo.InvariantCulture);
    public string Name { get; }
    public AdvisorMapCoordinate? Position { get; }
    public bool IsAircraft { get; }
    public bool IsAirborne { get; }
    public string? OrderUnavailableReason { get; }
    public GameUnitInfo(int id, string name, AdvisorMapCoordinate? position, bool isAircraft, bool isAirborne, string? orderUnavailableReason)
    { Id = id; Name = name; Position = position; IsAircraft = isAircraft; IsAirborne = isAirborne; OrderUnavailableReason = orderUnavailableReason; }
}

public sealed class AmmunitionInfo
{
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Subtype { get; }
    public int Remaining { get; }
    public int Available { get; }
    public bool SingleRoundSupported { get; }
    public int AvailableAfterClearingReservations { get; }
    public AmmunitionInfo(string id, string name, string type, string subtype, int remaining, int available, bool singleRoundSupported)
        : this(id, name, type, subtype, remaining, available, singleRoundSupported, available) { }
    public AmmunitionInfo(string id, string name, string type, string subtype, int remaining, int available,
        bool singleRoundSupported, int availableAfterClearingReservations)
    { Id = id; Name = name; Type = type; Subtype = subtype; Remaining = remaining; Available = available;
        SingleRoundSupported = singleRoundSupported; AvailableAfterClearingReservations = availableAfterClearingReservations; }
}

public sealed class ReservationRoute
{
    public AdvisorMapCoordinate CurrentPosition { get; }
    public AdvisorMapCoordinate? EndPosition { get; }
    public ReservationRoute(AdvisorMapCoordinate currentPosition, AdvisorMapCoordinate? endPosition)
    { CurrentPosition = currentPosition; EndPosition = endPosition; }
}
