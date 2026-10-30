using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// The MOD supplies registered connections; Reports owns only its selected ID and detached generation input.
internal interface ICrewConnections
{
    IAiConnectionStore Store { get; }
    IReadOnlyList<IAiConnectionProvider> Providers { get; }
    string? Role(string id);
    string Error(AiConnectionException error);
    Task<IReadOnlyList<CrewSubmission>> GenerateAsync(CrewBatch batch, string id, CancellationToken cancellation);
    Task<string> TestAsync(AiConnectionDraft draft, CancellationToken cancellation);
}

internal static class CrewConnectionSelection
{
    internal static void ValidateSelection(this ICrewConnections connections, string? id)
    {
        if (id == null) throw new CrewException("connection_required");
        var selected = connections.Store.ReadConnections().SingleOrDefault(p => p.Id == id);
        if (selected == null) throw new CrewException("connection_missing");
        if (!connections.Providers.Any(p => p.Id == selected.Provider)) throw new CrewException("provider_unavailable");
    }
}
