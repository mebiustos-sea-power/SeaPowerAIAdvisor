using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor.Example;

// AnchorChain inspects public types before applying entry-point ordering. Keep API-dependent
// implementation types internal so discovery works before the host API DLL is loaded.
internal static class ProviderRegistration
{
    internal static void Register()
    {
        if (!AdvisorApi.IsAvailable) return;
        AdvisorApi.Register(new StandardExample());
        AdvisorApi.Register(new IndependentExample());
    }
}

// These examples make no AI requests. Their replies identify them as offline examples.
internal abstract class ExampleDefinition : IAdvisorExtension, IAdvisorExtensionDescription
{
    public int ApiVersion => AdvisorApi.ContractVersion;
    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Protocol => "offline_example";
    public string GetDescription(string uiLanguage) => uiLanguage == "ja"
        ? "オフラインの実装例です。AIへの通信は行いません。"
        : "Offline implementation example. No AI requests are sent.";
    public IReadOnlyList<ConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    { new ConnectionField("model", "Model", "offline-example", translations: new Dictionary<string,string> { ["ja"] = "モデル名" }) });
    public void ValidateSettings(string settingsJson)
    {
        var value = JObject.Parse(settingsJson);
        if (value.Properties().Any(p => p.Name != "model") || value["model"]?.Type != JTokenType.String ||
            string.IsNullOrWhiteSpace((string?)value["model"])) throw new AdvisorException("model_invalid");
    }
    public string GetModel(string settingsJson) => (string?)JObject.Parse(settingsJson)["model"] ?? "";
    public Task<AdvisorAnswer> TestAsync(ConnectionSettings connection, IAdvisorConnectionServices services,
        string testMessage, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new AdvisorAnswer("Offline example connection succeeded; no AI request was sent.", connection.Model));
    }
}

internal sealed class StandardExample : ExampleDefinition, IAdvisorToolListExtension
{
    public override string Id => "example.standard";
    public override string Name => "Example: standard engine (offline)";
    public IAdvisorSession OpenSession(ConnectionSettings connection, IAdvisorConnectionServices services,
        ConsultationInput input, RetrievalHistory? continuation) => new Session(input.ToolsJson.Select(JObject.Parse).Select(t => (string?)t["name"]).FirstOrDefault(n => n == "query_situation"));
    private sealed class Session : IAdvisorSession
    {
        private readonly string? toolName;
        private bool received;
        internal Session(string? toolName) { this.toolName = toolName; }
        public ProviderRequest Prepare(bool allowTools)
        {
            var args = "{\"requests\":[{\"query\":\"catalog\"}]}";
            return new ProviderRequest(Encoding.UTF8.GetByteCount(args), cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                return Task.FromResult(received || !allowTools || toolName == null
                    ? new ProviderReply("**Offline API example**: the standard engine completed this demonstration.")
                    : new ProviderReply("", new[] { new ToolCall("example-catalog", toolName, args) }));
            });
        }
        public void AcceptResults(IReadOnlyList<ToolResult> results) { received = true; }
        public void Dispose() { }
    }
}

internal sealed class IndependentExample : ExampleDefinition, IIndependentAdvisorExtension
{
    public override string Id => "example.independent";
    public override string Name => "Example: independent workflow (offline)";
    public IAdvisorConsultation OpenConsultation(ConnectionSettings connection, IAdvisorConnectionServices services) => new Consultation(connection.Model);
    private sealed class Consultation : IAdvisorConsultation
    {
        private readonly string model;
        internal Consultation(string model) { this.model = model; }
        public Task<AdvisorAnswer> ConsultAsync(ConsultationContext context, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            context.ReportProgress(new AdvisorProgressUpdate(AdvisorPhase.Retrieving, 0, new[] { "catalog" }));
            var result = JObject.Parse(context.Knowledge.Execute("{\"requests\":[{\"query\":\"catalog\"}]}"));
            return Task.FromResult(new AdvisorAnswer("**Offline API example**: an independent workflow queried the catalog; status: " +
                (string?)result["status"] + ". No AI request was sent.", model));
        }
        public void Dispose() { }
    }
}
