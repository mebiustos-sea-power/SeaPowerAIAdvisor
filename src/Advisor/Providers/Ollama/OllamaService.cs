using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class OllamaService : AdvisorService
{
    internal const string DefaultUrl = "http://localhost:11434";
    internal const string DefaultModel = "qwen3:8b";
    internal override string Id => "ollama";
    internal override string Name => "Ollama";
    internal override string Version => "1.0.0";
    internal override string Summary(AdvisorText text) => text.Format("extensions.adapter_summary", Name);
    internal override string Description(AdvisorText text) => text["connection.ollama_description"];
    internal override string Protocol => "ollama_chat";
    internal override IReadOnlyList<AdvisorConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new AdvisorConnectionField("base_url", "connection.server_url", DefaultUrl),
        new AdvisorConnectionField("model", "connection.model", DefaultModel),
        new AdvisorConnectionField("num_ctx", "connection.context_length", "8192", numeric: true)
    });
    internal override void Validate(JObject settings)
    {
        ValidateFields(settings); _ = OllamaConfiguration.Endpoint(settings); _ = OllamaConfiguration.ContextLength(settings);
        var model = (string)settings["model"]!;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || Array.Exists(model.ToCharArray(), char.IsWhiteSpace) ||
            Array.Exists(model.ToCharArray(), char.IsControl)) throw new AiConnectionException("ollama_model_invalid");
    }
    internal override IAiProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AiHttpTransport transport) => new OllamaProvider(transport, configuration.ToConnectionSettings(fallback));
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AiHttpTransport transport, string request, CancellationToken cancellation)
    {
        var body = OllamaProvider.Request(configuration.ToConnectionSettings(), new JArray(new JObject { ["role"] = "user", ["content"] = request }));
        var response = await transport.SendAsync(OllamaConfiguration.Endpoint(configuration.Settings), body, configuration.RequestTimeoutSeconds,
            false, configuration.Model, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var reply = OllamaProvider.Read(response, 0);
        if (reply.Calls.Count != 0) throw new AiConnectionException("unexpected_function_call");
        return new AdviceResult(reply.Text, 1, 0, reply.TotalTokens, model: configuration.Model);
    }
}
