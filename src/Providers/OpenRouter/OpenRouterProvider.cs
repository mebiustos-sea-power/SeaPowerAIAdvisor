using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class OpenRouterProvider : ChatCompletionProvider
{
    private static readonly Uri ChatEndpoint = new("https://openrouter.ai/api/v1/chat/completions");
    protected override Uri Endpoint => ChatEndpoint;
    protected override void ConfigureRequest(JObject body) => body["provider"] = new JObject { ["require_parameters"] = true };
    internal OpenRouterProvider(AdvisorHttpTransport transport, AdvisorApiConfiguration configuration,
        AdvisorConnectionOptions options, bool fallback) : base(transport, configuration, options, fallback) { }
}
