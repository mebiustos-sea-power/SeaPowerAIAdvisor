using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal sealed class AiConnectionField
{
    internal string Key { get; }
    internal string Label { get; }
    internal string DefaultValue { get; }
    internal bool Secret { get; }
    internal bool Numeric { get; }
    private readonly Func<string, string>? caption;
    internal string Caption(Func<string, string> text, string language) => caption == null ? text(Label) : caption(language);
    internal AiConnectionField(string key, string label, string defaultValue = "", bool secret = false,
        Func<string, string>? caption = null, bool numeric = false)
    { Key = key; Label = label; DefaultValue = defaultValue; Secret = secret; Numeric = numeric; this.caption = caption; }
}

internal interface IAiConnectionProvider
{
    string Id { get; }
    IReadOnlyList<AiConnectionField> Fields { get; }
    string Caption(Func<string, string> text, string language);
    string Description(Func<string, string> text, string language);
    string Model(AiConnectionProfile profile);
    JObject Defaults();
}

// Neutral built-in definitions are shared; feature extension registries remain feature-owned.
internal sealed class AiProviderDefinition : IAiConnectionProvider
{
    internal const string GeminiDefaultModel = "gemini-3.5-flash-lite";
    internal const string NanoGptDefaultModel = "deepseek/deepseek-v4.1-flash-1017";
    internal const string OllamaDefaultUrl = "http://localhost:11434";
    internal const string OllamaDefaultModel = "qwen3:8b";
    internal const string OpenRouterDefaultModel = "google/gemini-3.5-flash-lite";
    internal static IReadOnlyList<AiProviderDefinition> BuiltIn { get; } = Array.AsReadOnly(new[]
    { new AiProviderDefinition("gemini", "Gemini", "interactions", GeminiDefaultModel),
      new AiProviderDefinition("ollama", "Ollama", "ollama_chat", OllamaDefaultModel),
      new AiProviderDefinition("openrouter", "OpenRouter", "openrouter_chat", OpenRouterDefaultModel),
      new AiProviderDefinition("nanogpt", "NanoGPT", "nanogpt_subscription_chat", NanoGptDefaultModel) });
    public string Id { get; }
    internal string Name { get; }
    internal string Protocol { get; }
    internal string Version => "1.0.0";
    public IReadOnlyList<AiConnectionField> Fields { get; }
    private AiProviderDefinition(string id, string name, string protocol, string model)
    {
        Id = id; Name = name; Protocol = protocol;
        Fields = Array.AsReadOnly(id == "ollama" ? new[]
        { new AiConnectionField("base_url", "connection.server_url", OllamaDefaultUrl), new AiConnectionField("model", "connection.model", model),
          new AiConnectionField("num_ctx", "connection.context_length", "8192", numeric: true) } : new[]
        { new AiConnectionField("api_key", "connection.api_key", secret: true), new AiConnectionField("model", "connection.model", model) });
    }
    internal static AiProviderDefinition Require(string id) => BuiltIn.SingleOrDefault(p => p.Id == id) ?? throw new AiConnectionException("provider_unavailable");
    public string Caption(Func<string, string> text, string language) => Id == "nanogpt" ? text("connection.nanogpt_name") : Name;
    public string Description(Func<string, string> text, string language) => text("connection." + Id + "_description");
    public string Model(AiConnectionProfile profile) => profile.Model;
    public JObject Defaults() => new(Fields.Select(f => new JProperty(f.Key, f.DefaultValue)));
    internal void Validate(JObject settings)
    {
        if (settings.Properties().Any(p => !Fields.Any(f => f.Key == p.Name)) || Fields.Any(f => settings[f.Key]?.Type != JTokenType.String))
            throw new AiConnectionException("settings_invalid");
        var model = (string)settings["model"]!;
        if (Id == "gemini")
        {
            if (!Regex.IsMatch(model, @"\Agemini-[a-z0-9][a-z0-9._-]*\z") || model.Length > 100) throw new AiConnectionException("model_invalid");
        }
        else if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            Id == "openrouter" && (model.IndexOf('/') <= 0 || model.EndsWith("/", StringComparison.Ordinal)))
            throw new AiConnectionException(Id + "_model_invalid");
        if (Id == "ollama") { _ = OllamaConfiguration.Endpoint(settings); _ = OllamaConfiguration.ContextLength(settings); return; }
        var key = (string)settings["api_key"]!; if (Id == "gemini") key = key.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new AiConnectionException("api_key_missing");
        if (key.Length > 512 || key.Any(c => c < '!' || c > '~')) throw new AiConnectionException("settings_invalid");
    }
    internal IAiProvider Open(AiConnectionSettings settings, AiHttpTransport transport)
    {
        Validate(settings.Settings); settings = Normalize(settings);
        return Id switch { "gemini" => new GeminiProvider(new GeminiTransport(transport), settings),
            "ollama" => new OllamaProvider(transport, settings), "nanogpt" => new NanoGptProvider(transport, settings),
            "openrouter" => new OpenRouterProvider(transport, settings), _ => throw new AiConnectionException("provider_unavailable") };
    }
    private AiConnectionSettings Normalize(AiConnectionSettings value) => Id == "gemini" ?
        new AiConnectionSettings(value.Model, value.ApiKey.Trim(), value.RequestTimeoutSeconds, value.MaxOutputTokens, value.Settings, value.IsSubConnection) : value;
    internal async Task<AiConnectionTestResult> TestAsync(AiConnectionSettings settings, AiHttpTransport transport, string request,
        string toolInstruction, CancellationToken cancellation)
    {
        settings = Normalize(settings); var provider = Open(settings, transport);
        if (provider is ChatCompletionProvider chat) return await AiConnectionTest.RunAsync(chat, request, toolInstruction, cancellation).ConfigureAwait(false);
        AiReply reply;
        if (Id == "gemini")
        {
            var body = GeminiInteraction.Request(settings, new JArray(new JObject { ["type"] = "user_input",
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = request }) }), store: false);
            var response = await new GeminiTransport(transport).GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); var result = GeminiInteraction.Read(response, requireStoredId: false);
            if (result.Calls.Length != 0) throw new AiConnectionException("unexpected_function_call");
            return new AiConnectionTestResult(result.Text, 1, result.TotalTokens, settings.Model);
        }
        else
        {
            var body = OllamaProvider.Request(settings, new JArray(new JObject { ["role"] = "user", ["content"] = request }));
            var response = await transport.SendAsync(OllamaConfiguration.Endpoint(settings.Settings), body, settings.RequestTimeoutSeconds,
                settings.IsSubConnection, settings.Model, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); reply = OllamaProvider.Read(response, 0);
        }
        if (reply.Calls.Count != 0) throw new AiConnectionException("unexpected_function_call");
        return new AiConnectionTestResult(reply.Text, 1, reply.TotalTokens, settings.Model);
    }
}
