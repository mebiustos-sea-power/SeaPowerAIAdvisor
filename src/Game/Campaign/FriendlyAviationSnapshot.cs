using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Aircraft assigned to a squadron/wing and active flights are overlapping views, not additive totals.
internal sealed class FriendlyAviationSnapshot
{
    internal IReadOnlyList<FriendlyAirUnit> Units { get; }
    internal IReadOnlyList<FriendlyAirFlight> Flights { get; }
    internal FriendlyAviationSnapshot(IEnumerable<FriendlyAirUnit> units, IEnumerable<FriendlyAirFlight> flights)
    { Units = Array.AsReadOnly(units.ToArray()); Flights = Array.AsReadOnly(flights.ToArray()); }
}

internal sealed class FriendlyAirUnit
{
    internal string Kind { get; }
    internal string Name { get; }
    internal int? BaseIndex { get; }
    internal int? ForceIndex { get; }
    internal int? ShipIndex { get; }
    internal bool AirliftDetachment { get; }
    internal bool? LaunchBlocked { get; }
    internal string? LaunchReason { get; }
    internal DateTime? RunwayOutUntil { get; }
    internal int? DeckCycleRemaining { get; }
    internal IReadOnlyList<FriendlyAircraftType> Aircraft { get; }
    internal FriendlyAirUnit(string kind, string name, int? baseIndex, int? forceIndex, int? shipIndex,
        bool airlift, bool? launchBlocked, string? reason, DateTime? runwayOutUntil, int? deckCycleRemaining,
        IEnumerable<FriendlyAircraftType> aircraft)
    {
        Kind = kind; Name = name; BaseIndex = baseIndex; ForceIndex = forceIndex; ShipIndex = shipIndex;
        AirliftDetachment = airlift; LaunchBlocked = launchBlocked; LaunchReason = reason;
        RunwayOutUntil = runwayOutUntil; DeckCycleRemaining = deckCycleRemaining;
        Aircraft = Array.AsReadOnly(aircraft.ToArray());
    }
}

internal sealed class FriendlyAircraftType
{
    internal string Name { get; }
    internal bool CampaignLaunchSupported { get; }
    internal IReadOnlyDictionary<string, int?> Counts { get; }
    internal IReadOnlyList<string> LoadoutNames { get; }
    internal FriendlyAircraftType(string name, bool supported, IDictionary<string, int?> counts, IEnumerable<string> loadouts)
    {
        Name = name; CampaignLaunchSupported = supported;
        Counts = new ReadOnlyDictionary<string, int?>(new Dictionary<string, int?>(counts, StringComparer.Ordinal));
        LoadoutNames = Array.AsReadOnly(loadouts.ToArray());
    }
}

internal sealed class FriendlyAirFlight
{
    internal string Name { get; }
    internal string AircraftName { get; }
    internal MapPosition Position { get; }
    internal int Count { get; }
    internal string LoadoutName { get; }
    internal bool Returning { get; }
    internal int? SourceUnitIndex { get; }
    internal FriendlyAirFlight(string name, string aircraftName, MapPosition position, int count,
        string loadout, bool returning, int? sourceUnit)
    { Name = name; AircraftName = aircraftName; Position = position; Count = count; LoadoutName = loadout;
      Returning = returning; SourceUnitIndex = sourceUnit; }
}
