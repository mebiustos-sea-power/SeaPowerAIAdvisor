using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal interface IGeminiTransport
{
    Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken cancellation);
}

internal sealed class GeminiTransport : IGeminiTransport, IDisposable
{
    internal const int MaxResponseBytes = AiHttpTransport.MaxResponseBytes;
    private readonly AiHttpTransport transport;
    private readonly bool ownsTransport;
    internal GeminiTransport(IAiTraceObserver? trace = null) : this(new AiHttpTransport(trace), true) { }
    internal GeminiTransport(HttpMessageHandler handler, IAiTraceObserver? trace = null) : this(new AiHttpTransport(handler, trace), true) { }
    internal GeminiTransport(AiHttpTransport transport, bool ownsTransport = false)
    { this.transport = transport; this.ownsTransport = ownsTransport; }
    public Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken cancellation) =>
        transport.SendAsync(new Uri("https://generativelanguage.googleapis.com/v1beta/interactions"), body,
            settings.RequestTimeoutSeconds, settings.IsSubConnection, settings.Model, cancellation, new Dictionary<string, string> { ["x-goog-api-key"] = settings.ApiKey });
    public void Dispose() { if (ownsTransport) transport.Dispose(); }
}
