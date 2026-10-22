using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor;

// One consultation owns connection selection. Switching never resets knowledge or returns to the main.
internal sealed class AdvisorConnection : IDisposable
{
    private readonly IAdvisorProvider main;
    private readonly IAdvisorProvider? sub;
    private readonly Func<bool, AdvisorProviderInput> input;
    private readonly Action<AdvisorConnectionException> switched;
    private IAdvisorProviderSession? session;
    internal bool UsingFallback { get; private set; }
    internal int ApiRequests { get; private set; }
    internal string Model => (UsingFallback ? sub! : main).Model;

    internal AdvisorConnection(IAdvisorProvider main, IAdvisorProvider? sub, AdvisorProviderInput input, Action<AdvisorConnectionException> switched, AdvisorContinuation? continuation = null)
        : this(main, sub, _ => input, switched, continuation) { }

    internal AdvisorConnection(IAdvisorProvider main, IAdvisorProvider? sub, Func<bool, AdvisorProviderInput> input,
        Action<AdvisorConnectionException> switched, AdvisorContinuation? continuation = null)
    {
        this.main = main; this.sub = sub; this.input = input; this.switched = switched;
        session = main.Start(input(false).Copy(), continuation);
    }

    internal async Task<AdvisorReply> GenerateAsync(bool allowTools, AdvisorContinuation continuation, CancellationToken cancellation,
        Action<int, bool, string, int> beforeSend)
    {
        async Task<AdvisorReply> Send()
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (session ?? throw new InvalidOperationException("The connection session has been disposed.")).Prepare(allowTools);
            beforeSend(ApiRequests + 1, UsingFallback, Model, request.Bytes);
            ApiRequests++;
            return await request.SendAsync(cancellation).ConfigureAwait(false);
        }
        try
        {
            try { return await Send().ConfigureAwait(false); }
            catch (AdvisorConnectionException error) when (!UsingFallback && sub != null && CanSwitch(error))
            {
                cancellation.ThrowIfCancellationRequested();
                UsingFallback = true;
                switched(error);
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
                Dispose();
                session = sub!.Start(input(true).Copy(), continuation);
                return await Send().ConfigureAwait(false);
            }
        }
        catch (AdvisorConnectionException error) when (UsingFallback)
        { throw new AdvisorConnectionException(error.Code, error.HttpStatus, true) { HasDetails = error.HasDetails }; }
    }

    internal void AcceptResults(IReadOnlyList<AdvisorToolResult> results) =>
        (session ?? throw new InvalidOperationException("The connection session has been disposed.")).AcceptResults(results);
    public void Dispose() => Interlocked.Exchange(ref session, null)?.Dispose();

    internal static bool CanSwitch(AdvisorConnectionException error) =>
        error.Code is "network_error" or "request_timeout" ||
        error.HttpStatus is 429 or 503 or 408 or 504;
}

// Only fixed error codes and HTTP statuses cross the provider/UI boundary; never a raw response or key.
internal sealed class AdvisorConnectionException : Exception
{
    internal string Code { get; }
    internal int? HttpStatus { get; }
    internal bool UsingFallback { get; }
    internal bool HasDetails { get; set; }
    internal AdvisorConnectionException(string code, int? httpStatus = null, bool usingFallback = false) : base(code + (httpStatus.HasValue ? " HTTP " + httpStatus : ""))
    { Code = code; HttpStatus = httpStatus; UsingFallback = usingFallback; }
}
