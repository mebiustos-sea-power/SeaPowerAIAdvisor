using System.Collections.Generic;
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
