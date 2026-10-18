using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Public objectives are separate from sightings: ownership is disclosed, garrison strength is not.
internal sealed class VictorySnapshot
{
    internal IReadOnlyList<VictoryBase> Bases { get; }
    internal IReadOnlyList<VictorySide> Sides { get; }
    internal string? WinnerSide { get; }
    internal DateTime? DecidedAt { get; }
    internal VictorySnapshot(IEnumerable<VictoryBase> bases, IEnumerable<VictorySide> sides, string? winnerSide, DateTime? decidedAt)
    {
        Bases = Array.AsReadOnly(bases.ToArray()); Sides = Array.AsReadOnly(sides.ToArray());
        WinnerSide = winnerSide; DecidedAt = decidedAt;
    }
}

internal sealed class VictoryBase
{
    internal string Name { get; }
    internal string Kind { get; }
    internal MapPosition Position { get; }
    internal string? OwnerSide { get; }
    internal VictoryBase(string name, string kind, MapPosition position, string? ownerSide)
    { Name = name; Kind = kind; Position = position; OwnerSide = ownerSide; }
}

internal sealed class VictorySide
{
    internal string Side { get; }
    internal string Name { get; }
    internal int Held { get; }
    internal IReadOnlyList<int> Objectives { get; }
    internal IReadOnlyList<ExcludedVictoryBase> Excluded { get; }
    internal VictorySide(string side, string name, int held, IEnumerable<int> objectives, IEnumerable<ExcludedVictoryBase> excluded)
    {
        Side = side; Name = name; Held = held;
        Objectives = Array.AsReadOnly(objectives.ToArray()); Excluded = Array.AsReadOnly(excluded.ToArray());
    }
}

internal sealed class ExcludedVictoryBase
{
    internal int BaseIndex { get; }
    internal string Reason { get; }
    internal ExcludedVictoryBase(int baseIndex, string reason) { BaseIndex = baseIndex; Reason = reason; }
}
