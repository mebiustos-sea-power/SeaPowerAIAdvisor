using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// A consultation implementation may use the standard engine or its own retrieval/communication strategy.
internal interface IAdvisorBackend
{
    Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation);
}

internal sealed class AdviceResult
{
    internal string Text { get; }
    internal int ApiRequests { get; }
    internal int QueryRounds { get; }
    internal long? TotalTokens { get; }
    internal string Model { get; }
    internal IReadOnlyList<string> Queries { get; }
    internal AdviceResult(string text, int apiRequests, int queryRounds, long? totalTokens, IEnumerable<string>? queries = null, string model = "")
    { Text = text; ApiRequests = apiRequests; QueryRounds = queryRounds; TotalTokens = totalTokens; Model = model; Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).ToArray()); }
}
