using System;

namespace SeaPowerAIAdvisor;

internal sealed class NanoGptProvider : ChatCompletionProvider
{
    // This route rejects requests outside the subscription instead of spending the account balance.
    private static readonly Uri SubscriptionEndpoint = new("https://nano-gpt.com/api/subscription/v1/chat/completions");
    protected override Uri Endpoint => SubscriptionEndpoint;
    protected override string PaymentErrorCode => "subscription_unavailable";
    internal NanoGptProvider(AdvisorHttpTransport transport, AdvisorApiConfiguration configuration,
        AdvisorConnectionOptions options, bool fallback) : base(transport, configuration, options, fallback) { }
}
