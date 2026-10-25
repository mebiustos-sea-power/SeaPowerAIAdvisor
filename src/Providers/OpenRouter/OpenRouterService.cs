using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class OpenRouterService : AdvisorService
{
    internal const string DefaultModel = "google/gemini-3.5-flash-lite";
    internal override string Id => "openrouter";
    internal override string Name => "OpenRouter";
    internal override string Version => "1.0.0";
    internal override string Summary(AdvisorText text) => text.Format("extensions.adapter_summary", Name);
    internal override string Protocol => "openrouter_chat";
    internal override string Description(AdvisorText text) => text["connection.openrouter_description"];
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
        var slash = model.IndexOf('/');
        if (model.Length > 200 || model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || slash <= 0 || slash == model.Length - 1)
            throw new AdvisorConnectionException("openrouter_model_invalid");
    }
    internal override IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport) => new OpenRouterProvider(transport, configuration, options, fallback);
    internal override Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation) =>
        ChatCompletionConnectionTest.RunAsync(new OpenRouterProvider(transport, configuration, options, false), request, cancellation);
}
