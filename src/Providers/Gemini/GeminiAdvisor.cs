using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Gemini entry points for existing integrations and tests. The game UI selects backends through the registry.
internal sealed class GeminiAdvisor
{
    private readonly IGeminiTransport transport;
    private readonly Action<AdvisorConnectionException>? switched;
    internal GeminiAdvisor(IGeminiTransport transport, Action<AdvisorConnectionException>? switched = null)
    { this.transport = transport; this.switched = switched; }

    internal static async Task<AdviceResult> RunAsync(string logDirectory, IAdvisorQuerySession session, AdvisorSettings settings,
        AdvisorQuestion question, IReadOnlyList<ChatExchange> history, CancellationToken cancellation,
        Func<AdvisorTrace?, GeminiTransport>? transportFactory = null, AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        var context = AdvisorContext.Create(session, history, profile, commonPrompt, progress, historyExchanges, userName);
        return await AdvisorConsultation.RunAsync(GeminiBackend.Prepare(settings, transportFactory), context,
            logDirectory, question, cancellation).ConfigureAwait(false);
    }

    internal Task<AdviceResult> ConsultAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        var context = AdvisorContext.Create(session, history, profile, commonPrompt, progress, historyExchanges, userName);
        IAdvisorBackend backend = new AdvisorEngine(new GeminiProvider(transport, settings),
            settings.FallbackApiKey.Length == 0 ? null : new GeminiProvider(transport, settings.ForFallback()), switched);
        return AdvisorConsultation.RunAsync(backend, context, cancellation);
    }
}
