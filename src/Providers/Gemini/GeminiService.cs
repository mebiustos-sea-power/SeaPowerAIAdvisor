using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class GeminiService : AdvisorService
{
    internal override string Id => "gemini";
    internal override string Name => "Gemini";
    internal override string Protocol => "interactions";
    internal override IReadOnlyList<AdvisorConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new AdvisorConnectionField("api_key", "connection.api_key", secret: true),
        new AdvisorConnectionField("model", "connection.model", AdvisorSettings.DefaultModel)
    });
    internal override void Validate(JObject settings)
    { ValidateFields(settings); _ = Resolve(settings, new AdvisorConnectionOptions(new JObject()), false); }
    private static AdvisorSettings Resolve(JObject settings, AdvisorConnectionOptions options, bool fallback, int? timeout = null)
    {
        var document = (JObject)options.Json.DeepClone();
        if (timeout.HasValue) document["request_timeout_seconds"] = timeout.Value;
        document["api_key"] = settings["api_key"]!.DeepClone(); document["model"] = settings["model"]!.DeepClone();
        if (fallback)
        {
            document["fallback_api_key"] = settings["api_key"]!.DeepClone(); document["fallback_model"] = settings["model"]!.DeepClone();
        }
        var result = AdvisorSettings.Parse(document.ToString(Formatting.None));
        return fallback ? result.ForFallback() : result;
    }
    internal override IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport) => new GeminiProvider(new GeminiTransport(transport), Resolve(configuration.Settings, options, fallback, configuration.RequestTimeoutSeconds));
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation)
    {
        var settings = Resolve(configuration.Settings, options, false, configuration.RequestTimeoutSeconds);
        var body = GeminiInteraction.Request(settings, new JArray(new JObject { ["type"] = "user_input",
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = request }) }), store: false);
        var response = await new GeminiTransport(transport).GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var reply = GeminiInteraction.Read(response, requireStoredId: false);
        if (reply.Calls.Length != 0) throw new AdvisorConnectionException("unexpected_function_call");
        return new AdviceResult(reply.Text, 1, 0, reply.TotalTokens, model: configuration.Model);
    }
}
