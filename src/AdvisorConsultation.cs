using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Owns logging and transport for one consultation; neither UI nor knowledge readers write HTTP logs.
internal static class AdvisorConsultation
{
    internal static async Task<AdviceResult> RunAsync(string logDirectory, IAdvisorQuerySession session, AdvisorSettings settings,
        AdvisorQuestion question, IReadOnlyList<ChatExchange> history, CancellationToken cancellation,
        Func<AdvisorTrace?, GeminiTransport>? transportFactory = null, AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        using var trace = AdvisorTrace.Start(logDirectory, session.Id, settings, question);
        try
        {
            using var transport = transportFactory == null ? new GeminiTransport(trace) : transportFactory(trace);
            var result = await new GeminiAdvisor(transport, error => trace?.Fallback(error)).ConsultAsync(session, settings, history, cancellation, profile, commonPrompt, progress, historyExchanges, userName).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            trace?.Finish("completed", result);
            return result;
        }
        catch (OperationCanceledException)
        {
            trace?.Finish("cancelled");
            throw;
        }
        catch (AdvisorConnectionException error)
        {
            trace?.Finish("failed", errorCode: error.Code, status: error.HttpStatus);
            throw;
        }
        catch (Exception error)
        {
            trace?.Finish("unexpected_error", errorType: error.GetType().FullName);
            throw;
        }
    }
}
