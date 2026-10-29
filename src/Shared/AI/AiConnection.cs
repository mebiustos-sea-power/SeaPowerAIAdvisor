using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor.Shared;

// One task owns connection selection. The host supplies any continuation when switching providers.
internal sealed class AiConnection : IDisposable
{
    private readonly IAiProvider main;
    private readonly IAiProvider? sub;
    private readonly Func<bool, AiProviderInput> input;
    private readonly Action<AiConnectionException> switched;
    private IAiProviderSession? session;
    internal bool UsingFallback { get; private set; }
    internal int ApiRequests { get; private set; }
    internal string Model => (UsingFallback ? sub! : main).Model;

    internal AiConnection(IAiProvider main, IAiProvider? sub, AiProviderInput input, Action<AiConnectionException> switched, AiContinuation? continuation = null)
        : this(main, sub, _ => input, switched, continuation) { }

    internal AiConnection(IAiProvider main, IAiProvider? sub, Func<bool, AiProviderInput> input,
        Action<AiConnectionException> switched, AiContinuation? continuation = null)
    {
        this.main = main; this.sub = sub; this.input = input; this.switched = switched;
        session = main.Start(input(false).Copy(), continuation);
    }

    internal async Task<AiReply> GenerateAsync(bool allowTools, AiContinuation? continuation, CancellationToken cancellation,
        Action<int, bool, string, int> beforeSend)
    {
        async Task<AiReply> Send()
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
            catch (AiConnectionException error) when (!UsingFallback && sub != null && CanSwitch(error))
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
        catch (AiConnectionException error) when (UsingFallback)
        { throw new AiConnectionException(error.Code, error.HttpStatus, true) { HasDetails = error.HasDetails }; }
    }

    internal void AcceptResults(IReadOnlyList<AiToolResult> results) =>
        (session ?? throw new InvalidOperationException("The connection session has been disposed.")).AcceptResults(results);
    public void Dispose() => Interlocked.Exchange(ref session, null)?.Dispose();

    internal static bool CanSwitch(AiConnectionException error) =>
        error.Code is "network_error" or "request_timeout" ||
        error.HttpStatus is 429 or 503 or 408 or 504;
}

// Only fixed error codes and HTTP statuses cross the provider/UI boundary; never a raw response or key.
internal sealed class AiConnectionException : Exception
{
    internal string Code { get; }
    internal int? HttpStatus { get; }
    internal bool UsingFallback { get; }
    internal bool HasDetails { get; set; }
    internal AiConnectionException(string code, int? httpStatus = null, bool usingFallback = false) : base(code + (httpStatus.HasValue ? " HTTP " + httpStatus : ""))
    { Code = code; HttpStatus = httpStatus; UsingFallback = usingFallback; }
}
