using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal sealed class OpenRouterProvider : ChatCompletionProvider
{
    private static readonly Uri ChatEndpoint = new("https://openrouter.ai/api/v1/chat/completions");
    protected override Uri Endpoint => ChatEndpoint;
    protected override void ConfigureRequest(JObject body) => body["provider"] = new JObject { ["require_parameters"] = true };
    internal OpenRouterProvider(AiHttpTransport transport, AiConnectionSettings configuration) : base(transport, configuration) { }
}
