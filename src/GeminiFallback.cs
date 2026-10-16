using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// One consultation owns this state. Moving to the fallback never restarts game queries or returns to the primary.
internal sealed class GeminiFallback
{
    private readonly IGeminiTransport transport;
    private readonly AdvisorSettings settings;
    private readonly Action<AdvisorConnectionException> switched;
    internal bool UsingFallback { get; private set; }
    internal int ApiRequests { get; private set; }

    internal GeminiFallback(IGeminiTransport transport, AdvisorSettings settings, Action<AdvisorConnectionException> switched)
    { this.transport = transport; this.settings = settings; this.switched = switched; }

    internal async Task<JObject> GenerateAsync(Func<bool, JObject> createBody, CancellationToken cancellation, Action<int, bool, JObject> beforeSend)
    {
        async Task<JObject> Send()
        {
            cancellation.ThrowIfCancellationRequested();
            var body = createBody(UsingFallback);
            beforeSend(ApiRequests + 1, UsingFallback, body);
            ApiRequests++;
            return await transport.GenerateAsync(UsingFallback ? settings.ForFallback() : settings, body, cancellation).ConfigureAwait(false);
        }
        try
        {
            try { return await Send().ConfigureAwait(false); }
            catch (AdvisorConnectionException error) when (!UsingFallback && settings.FallbackApiKey.Length > 0 && CanSwitch(error))
            {
                cancellation.ThrowIfCancellationRequested();
                UsingFallback = true;
                switched(error);
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
                return await Send().ConfigureAwait(false);
            }
        }
        catch (AdvisorConnectionException error) when (UsingFallback)
        { throw new AdvisorConnectionException(error.Code, error.HttpStatus, true); }
    }

    private static bool CanSwitch(AdvisorConnectionException error) =>
        error.Code is "network_error" or "request_timeout" ||
        error.HttpStatus is 429 or 503 or 408 or 504;
}
