using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IGeminiTransport
{
    Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation);
}

internal sealed class GeminiTransport : IGeminiTransport, IDisposable
{
    internal const int MaxResponseBytes = AdvisorHttpTransport.MaxResponseBytes;
    private readonly AdvisorHttpTransport transport;
    private readonly bool ownsTransport;
    internal GeminiTransport(AdvisorTrace? trace = null) : this(new AdvisorHttpTransport(trace), true) { }
    internal GeminiTransport(HttpMessageHandler handler, AdvisorTrace? trace = null) : this(new AdvisorHttpTransport(handler, trace), true) { }
    internal GeminiTransport(AdvisorHttpTransport transport, bool ownsTransport = false)
    { this.transport = transport; this.ownsTransport = ownsTransport; }
    public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation) =>
        transport.SendAsync(new Uri("https://generativelanguage.googleapis.com/v1beta/interactions"), body,
            settings.RequestTimeoutSeconds, settings.IsFallbackKey, settings.RequestModel, cancellation, new Dictionary<string, string> { ["x-goog-api-key"] = settings.RequestApiKey });
    public void Dispose() { if (ownsTransport) transport.Dispose(); }
}
