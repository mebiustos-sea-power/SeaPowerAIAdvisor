using System;

namespace SeaPowerAIAdvisor.Shared;

internal sealed class NanoGptProvider : ChatCompletionProvider
{
    // This route rejects requests outside the subscription instead of spending the account balance.
    private static readonly Uri SubscriptionEndpoint = new("https://nano-gpt.com/api/subscription/v1/chat/completions");
    protected override Uri Endpoint => SubscriptionEndpoint;
    protected override string PaymentErrorCode => "subscription_unavailable";
    internal NanoGptProvider(AiHttpTransport transport, AiConnectionSettings configuration) : base(transport, configuration)
    { }
}
