using System;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// Lifecycle state is owned by the UI thread; background work returns data without touching Unity/Noesis.
internal sealed class AdvisorJob : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<string> warn;
    private bool retired;
    private bool observed;
    internal Task<AdviceCompletion> Completion { get; }

    internal AdvisorJob(Func<CancellationToken, Task<AdviceResult>> work, Action<string> warn)
    {
        this.warn = warn;
        Completion = Task.Run(async () =>
        {
            try
            {
                var result = await work(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                return AdviceCompletion.Success(result);
            }
            catch (OperationCanceledException) { return AdviceCompletion.Cancelled(); }
            catch (AdvisorConnectionException error) { return AdviceCompletion.Failure(error); }
            catch (Exception error) { return AdviceCompletion.Unexpected(error); }
        });
    }

    internal void Cancel() => cancellation.Cancel();
    internal AdviceCompletion TakeCompleted()
    {
        if (retired) throw new InvalidOperationException("The adviser job has been retired.");
        if (!Completion.IsCompleted) throw new InvalidOperationException("The adviser job has not completed.");
        observed = true;
        var result = Completion.GetAwaiter().GetResult();
        Dispose();
        return result;
    }

    public void Dispose()
    {
        if (retired) return;
        retired = true; cancellation.Cancel();
        _ = Completion.ContinueWith(t =>
        {
            if (!observed && t.Result.UnexpectedError != null)
                warn("Advisor background operation failed: " + t.Result.UnexpectedError);
            cancellation.Dispose();
        }, TaskScheduler.Default);
    }
}

internal sealed class AdviceCompletion
{
    internal AdviceResult? Result { get; private set; }
    internal AdvisorConnectionException? Error { get; private set; }
    internal Exception? UnexpectedError { get; private set; }
    internal bool IsCancelled { get; private set; }
    internal static AdviceCompletion Success(AdviceResult result) => new() { Result = result };
    internal static AdviceCompletion Failure(AdvisorConnectionException error) => new() { Error = error };
    internal static AdviceCompletion Unexpected(Exception error) => new() { UnexpectedError = error };
    internal static AdviceCompletion Cancelled() => new() { IsCancelled = true };
}
