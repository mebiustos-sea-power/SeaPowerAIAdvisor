using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class NanoGptService : AdvisorService
{
    internal const string DefaultModel = "qwen/qwen3.8-27b";
    internal override string Id => "nanogpt";
    internal override string Name => "NanoGPT";
    internal override string Caption(AdvisorText text) => text["connection.nanogpt_name"];
    internal override string Protocol => "nanogpt_subscription_chat";
    internal override string Description(AdvisorText text) => text["connection.nanogpt_description"];
    internal override IReadOnlyList<AdvisorConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new AdvisorConnectionField("api_key", "connection.api_key", secret: true),
        new AdvisorConnectionField("model", "connection.model", DefaultModel)
    });
    internal override void Validate(JObject settings)
    {
        ValidateFields(settings);
        var key = (string)settings["api_key"]!;
        if (string.IsNullOrWhiteSpace(key)) throw new AdvisorConnectionException("api_key_missing");
        if (key.Length > 512 || key.Any(c => c < '!' || c > '~')) throw new AdvisorConnectionException("settings_invalid");
        var model = (string)settings["model"]!;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new AdvisorConnectionException("nanogpt_model_invalid");
    }
    internal override IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport) => new NanoGptProvider(transport, configuration, options, fallback);
    internal override Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation) =>
        ChatCompletionConnectionTest.RunAsync(new NanoGptProvider(transport, configuration, options, false), request, cancellation);
}
