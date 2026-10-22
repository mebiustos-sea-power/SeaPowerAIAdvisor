using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class NanoGptService : AdvisorService
{
    internal const string DefaultModel = "z-ai/glm-5.3";
    internal const int DefaultOutputTokens = 4096;
    internal override string Id => "nanogpt";
    internal override string Name => "NanoGPT";
    internal override string Caption(AdvisorText text) => text["connection.nanogpt_name"];
    internal override string Protocol => "nanogpt_subscription_chat";
    internal override string Description(AdvisorText text) => text["connection.nanogpt_description"];
    internal override IReadOnlyList<AdvisorConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new AdvisorConnectionField("api_key", "connection.api_key", secret: true),
        new AdvisorConnectionField("model", "connection.model", DefaultModel),
        new AdvisorConnectionField("max_output_tokens", "connection.output_tokens", DefaultOutputTokens.ToString(CultureInfo.InvariantCulture))
    });
    internal override JObject EditSettings(JObject settings, AdvisorConnectionOptions defaults)
    {
        var value = base.EditSettings(settings, defaults);
        if (value["max_output_tokens"] == null)
            value["max_output_tokens"] = defaults.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        return value;
    }
    internal static int OutputTokens(JObject settings, int fallback)
    {
        var value = settings["max_output_tokens"];
        if (value == null) return fallback;
        if (value.Type != JTokenType.String || !int.TryParse((string?)value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) || tokens < 1)
            throw new AdvisorConnectionException("nanogpt_output_tokens_invalid");
        return tokens;
    }
    internal override void Validate(JObject settings)
    {
        var tokens = OutputTokens(settings, DefaultOutputTokens);
        var complete = (JObject)settings.DeepClone();
        if (complete["max_output_tokens"] == null) complete["max_output_tokens"] = tokens.ToString(CultureInfo.InvariantCulture);
        ValidateFields(complete);
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
