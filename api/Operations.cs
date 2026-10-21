using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor.Api;

// Optional host service. Requests are serialized data; the host owns permission, identity and game-thread dispatch.
public interface IAdvisorOperations
{
    Task<string> ExecuteAsync(string requestJson, CancellationToken cancellation);
}
