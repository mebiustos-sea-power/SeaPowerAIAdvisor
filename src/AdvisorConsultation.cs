using System;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Owns consultation lifecycle and outcomes; retrieval strategy and API round trips belong to the selected backend.
internal static class AdvisorConsultation
{
    internal static async Task<AdviceResult> RunAsync(AdvisorBackendSetup setup, AdvisorContext context,
        string logDirectory, AdvisorQuestion question, CancellationToken cancellation)
    {
        using var trace = AdvisorTrace.Start(logDirectory, context.Knowledge.Id, setup.Diagnostics, question);
        return await RunAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var execution = setup.Open(trace);
            return await ExecuteAsync(execution.Backend, context, cancellation).ConfigureAwait(false);
        }, trace, cancellation).ConfigureAwait(false);
    }

    internal static Task<AdviceResult> RunAsync(IAdvisorBackend backend, AdvisorContext context, CancellationToken cancellation) =>
        RunAsync(() => ExecuteAsync(backend, context, cancellation), null, cancellation);

    private static async Task<AdviceResult> ExecuteAsync(IAdvisorBackend backend, AdvisorContext context, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(context.TimeoutSeconds));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var result = await backend.ConsultAsync(context, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return new AdviceResult(result.Text, result.ApiRequests, context.Knowledge.RoundsUsed, result.TotalTokens, result.Queries, result.Model);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new AdvisorConnectionException("consultation_timeout"); }
    }

    internal static async Task<AdviceResult> RunAsync(Func<Task<AdviceResult>> consult, AdvisorTrace? trace, CancellationToken cancellation)
    {
        try
        {
            var result = await consult().ConfigureAwait(false);
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
