using System;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Records consultation outcomes independently of the provider and consultation strategy.
internal static class AdvisorConsultation
{
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
