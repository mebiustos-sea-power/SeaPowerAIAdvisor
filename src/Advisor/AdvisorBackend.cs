using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Implementations choose their own retrieval/communication strategy, honor cancellation,
// and retrieve game information only through the context's host-controlled knowledge gateway.
internal interface IAdvisorBackend
{
    Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation);
}

internal enum AdvisorCompletionReason { Completed, RoundLimit, PayloadLimit }

internal sealed class AdviceResult
{
    internal AdvisorCompletionReason CompletionReason { get; }
    internal bool Completed => CompletionReason == AdvisorCompletionReason.Completed;
    internal string CompletionCode => CompletionReason switch
    {
        AdvisorCompletionReason.Completed => "completed",
        AdvisorCompletionReason.RoundLimit => "round_limit",
        AdvisorCompletionReason.PayloadLimit => "payload_limit",
        _ => throw new InvalidOperationException("Unknown consultation completion reason.")
    };
    internal string Text { get; }
    internal int ApiRequests { get; }
    internal int QueryRounds { get; }
    internal long? TotalTokens { get; }
    internal string Model { get; }
    internal IReadOnlyList<string> Queries { get; }
    internal AdviceResult(string text, int apiRequests, int queryRounds, long? totalTokens, IEnumerable<string>? queries = null, string model = "",
        AdvisorCompletionReason completionReason = AdvisorCompletionReason.Completed)
    { CompletionReason = completionReason; Text = text; ApiRequests = apiRequests; QueryRounds = queryRounds; TotalTokens = totalTokens; Model = model; Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).ToArray()); }
}

// Preparing validates configuration without opening a connection or writing a consultation log.
internal sealed class AdvisorBackendSetup
{
    internal string Model => Diagnostics.Model;
    internal string ConnectionName { get; }
    internal string SubConnectionName { get; }
    internal AdvisorTraceOptions Diagnostics { get; }
    private readonly Func<AdvisorTrace?, AdvisorBackendHandle> open;
    internal AdvisorBackendSetup(AdvisorTraceOptions diagnostics, Func<AdvisorTrace?, AdvisorBackendHandle> open,
        string connectionName = "", string subConnectionName = "")
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        ConnectionName = connectionName; SubConnectionName = subConnectionName;
    }
    internal AdvisorBackendHandle Open(AdvisorTrace? trace) => open(trace)
        ?? throw new InvalidOperationException("The adviser backend returned no execution scope.");
}

// The host owns one execution scope per consultation, including its transport or custom resources.
internal sealed class AdvisorBackendHandle : IDisposable
{
    internal IAdvisorBackend Backend { get; }
    private readonly IDisposable? resource;
    private bool disposed;
    internal AdvisorBackendHandle(IAdvisorBackend backend, IDisposable? resource = null)
    { Backend = backend ?? throw new ArgumentNullException(nameof(backend)); this.resource = resource ?? backend as IDisposable; }
    public void Dispose() { if (!disposed) { disposed = true; resource?.Dispose(); } }
}
