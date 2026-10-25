using System;
using System.Collections.Generic;
using System.Globalization;
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
        ValidateFields(settings); _ = Endpoint(settings); _ = ContextLength(settings);
        var model = (string)settings["model"]!;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || Array.Exists(model.ToCharArray(), char.IsWhiteSpace) ||
            Array.Exists(model.ToCharArray(), char.IsControl)) throw new AdvisorConnectionException("ollama_model_invalid");
    }
    internal static Uri Endpoint(JObject settings)
    {
        var value = (string?)settings["base_url"];
        if (value == null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AdvisorConnectionException("server_url_invalid");
        // Accept a reverse-proxy prefix, but never treat the supplied value as an arbitrary API endpoint.
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/api/chat");
    }
    internal static int ContextLength(JObject settings)
    {
        if (!int.TryParse((string?)settings["num_ctx"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 4096 || n > 262144)
            throw new AdvisorConnectionException("context_length_invalid");
        return n;
    }
    internal override IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport) => new OllamaProvider(transport, configuration, options, fallback);
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation)
    {
        var body = OllamaProvider.Request(configuration, options, new JArray(new JObject { ["role"] = "user", ["content"] = request }));
        var response = await transport.SendAsync(Endpoint(configuration.Settings), body, configuration.RequestTimeoutSeconds,
            false, configuration.Model, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var reply = OllamaProvider.Read(response, 0);
        if (reply.Calls.Count != 0) throw new AdvisorConnectionException("unexpected_function_call");
        return new AdviceResult(reply.Text, 1, 0, reply.TotalTokens, model: configuration.Model);
    }
}
