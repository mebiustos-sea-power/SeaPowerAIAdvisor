using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor.Crew;

// A unit's confirmed assignment outlives the currently visible map entry and its UI.
internal sealed class CrewAssignment
{
    internal string UnitId { get; }
    internal CrewPerson Person { get; }
    internal bool Enabled { get; }
    internal CrewAssignment(string unitId, CrewPerson person, bool enabled)
    { UnitId = unitId; Person = person; Enabled = enabled; }
}

internal sealed class CrewSavedState
{
    internal IReadOnlyList<CrewAssignment> People { get; }
    internal bool Paused { get; }
    internal int BeforeVotes { get; }
    internal int AfterVotes { get; }
    internal int InvalidVotes { get; }
    internal bool RankBeforeName { get; }
    internal bool IsEmpty => People.Count == 0 && !Paused && BeforeVotes == 0 && AfterVotes == 0 && InvalidVotes == 0 && RankBeforeName;
    internal CrewSavedState(IEnumerable<CrewAssignment> people, bool paused, int beforeVotes, int afterVotes, int invalidVotes, bool rankBeforeName)
    {
        People = people.ToArray(); Paused = paused; BeforeVotes = beforeVotes; AfterVotes = afterVotes;
        InvalidVotes = invalidVotes; RankBeforeName = rankBeforeName;
    }
}
