using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Api = SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Built-ins declare the same registration contract. The catalog retains their native adapters
// after validation, preserving the established Gemini/Ollama wire requests and test seams.
internal sealed class BuiltInAdvisorExtension : Api.IStandardAdvisorExtension
{
    internal AdvisorService Service { get; }
    public int ApiVersion => Api.AdvisorApi.ContractVersion;
    public string Id => Service.Id;
    public string Name => Service.Name;
    public string Protocol => Service.Protocol;
    public IReadOnlyList<Api.ConnectionField> Fields { get; }
    internal BuiltInAdvisorExtension(AdvisorService service)
    { Service = service; Fields = Array.AsReadOnly(service.Fields.Select(f => new Api.ConnectionField(f.Key, f.Label, f.DefaultValue, f.Secret)).ToArray()); }
    public void ValidateSettings(string settingsJson) => Service.Validate(AdvisorExtensionBridge.Object(settingsJson, "settings_invalid"));
    public string GetModel(string settingsJson) => (string?)AdvisorExtensionBridge.Object(settingsJson, "settings_invalid")["model"] ?? "";
    private AdvisorApiConfiguration Configuration(Api.ConnectionSettings c) => new(c.Id, c.Name, c.ProviderId,
        AdvisorExtensionBridge.Object(c.SettingsJson, "settings_invalid"), c.RequestTimeoutSeconds);
    private AdvisorConnectionOptions Options(Api.ConnectionSettings c) => new(new JObject
    { ["request_timeout_seconds"] = c.RequestTimeoutSeconds, ["max_output_tokens"] = c.MaxOutputTokens });
    public async Task<Api.AdvisorAnswer> TestAsync(Api.ConnectionSettings connection, Api.IAdvisorConnectionServices services,
        string testMessage, CancellationToken cancellation)
    {
        var result = await Service.TestAsync(Configuration(connection), Options(connection),
            RequireServices(services), testMessage, cancellation).ConfigureAwait(false);
        return new Api.AdvisorAnswer(result.Text, result.Model, result.ApiRequests, result.TotalTokens);
    }
    public Api.IAdvisorSession OpenSession(Api.ConnectionSettings connection, Api.IAdvisorConnectionServices services,
        Api.ConsultationInput input, Api.RetrievalHistory? continuation)
    {
        var provider = Service.Open(Configuration(connection), Options(connection), connection.IsSubConnection, RequireServices(services));
        return new Session(provider.Start(new AdvisorProviderInput(AdvisorExtensionBridge.Object(input.DataJson), input.SystemInstruction,
            AdvisorExtensionBridge.Object(input.ToolJson)), continuation == null ? null : new AdvisorContinuation(
                JArray.Parse(continuation.RetrievalsJson), continuation.RoundsUsed, continuation.MaxRounds)));
    }
    private static AdvisorHttpTransport RequireServices(Api.IAdvisorConnectionServices services) =>
        services is AdvisorExtensionServices host ? host.Transport : throw new InvalidOperationException("Built-in adapter requires host connection services.");
    private sealed class Session : Api.IAdvisorSession
    {
        private readonly IAdvisorProviderSession session;
        internal Session(IAdvisorProviderSession session) { this.session = session; }
        public Api.ProviderRequest Prepare(bool allowTools)
        {
            var request = session.Prepare(allowTools);
            return new Api.ProviderRequest(request.Bytes, async cancellation =>
            {
                var result = await request.SendAsync(cancellation).ConfigureAwait(false);
                return new Api.ProviderReply(result.Text, result.Calls.Select(c => new Api.ToolCall(c.Id, c.Name,
                    AdvisorExtensionBridge.Json(c.Arguments))), result.TotalTokens);
            });
        }
        public void AcceptResults(IReadOnlyList<Api.ToolResult> results) => session.AcceptResults(results.Select(r =>
            new AdvisorToolResult(new AdvisorToolCall(r.Id, r.Name, new JObject()), AdvisorExtensionBridge.Object(r.DataJson))).ToArray());
        public void Dispose() => session.Dispose();
    }
}
