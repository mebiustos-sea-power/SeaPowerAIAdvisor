using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Gemini composition root. Consultation policy lives in AdvisorEngine; wire state lives in GeminiProvider.
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
        using var trace = AdvisorTrace.Start(logDirectory, session.Id, settings, question);
        return await AdvisorConsultation.RunAsync(async () =>
        {
            using var transport = transportFactory == null ? new GeminiTransport(trace) : transportFactory(trace);
            return await new GeminiAdvisor(transport, error => trace?.Fallback(error)).ConsultAsync(session, settings, history,
                cancellation, profile, commonPrompt, progress, historyExchanges, userName).ConfigureAwait(false);
        }, trace, cancellation).ConfigureAwait(false);
    }

    internal Task<AdviceResult> ConsultAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        var context = AdvisorContext.Create(session, settings.ConsultationTimeoutSeconds, history, profile, commonPrompt, progress, historyExchanges, userName);
        IAdvisorBackend backend = new AdvisorEngine(new GeminiProvider(transport, settings),
            settings.FallbackApiKey.Length == 0 ? null : new GeminiProvider(transport, settings.ForFallback()), switched);
        return backend.ConsultAsync(context, cancellation);
    }
}
