using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class NanoGptProvider : ChatCompletionProvider
{
    // This route rejects requests outside the subscription instead of spending the account balance.
    private static readonly Uri SubscriptionEndpoint = new("https://nano-gpt.com/api/subscription/v1/chat/completions");
    protected override Uri Endpoint => SubscriptionEndpoint;
    protected override string PaymentErrorCode => "subscription_unavailable";
    private readonly int maxOutputTokens;
    protected override void ConfigureRequest(JObject body) => body["max_tokens"] = maxOutputTokens;
    internal NanoGptProvider(AdvisorHttpTransport transport, AdvisorApiConfiguration configuration,
        AdvisorConnectionOptions options, bool fallback) : base(transport, configuration, options, fallback)
    { maxOutputTokens = NanoGptService.OutputTokens(configuration.Settings, options.MaxOutputTokens); }
}
