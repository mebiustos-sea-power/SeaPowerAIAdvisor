using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor.ExtensionFixture;

// A separate client assembly depends only on the published contract, never host internals.
public abstract class Definition : IAdvisorExtension
{
    public int ApiVersion { get; set; } = AdvisorApi.ContractVersion;
    public string Id { get; set; } = "fixture." + Guid.NewGuid().ToString("N");
    public string Name => "External fixture";
    public string Protocol => "fixture";
    public IReadOnlyList<ConnectionField> Fields { get; set; } = Array.AsReadOnly(new[] { new ConnectionField("model", "Fixture model", "fixture-model"),
        new ConnectionField("secret", "Credential", secret: true, translations: new Dictionary<string,string> { ["ja"] = "認証情報" }) });
    public Action<string> Validate { get; set; } = _ => { };
    public Func<string, string> Model { get; set; } = _ => "fixture-model";
    public Func<ConnectionSettings, IAdvisorConnectionServices, string, CancellationToken, Task<AdvisorAnswer>> Test { get; set; }
        = (configuration, _, _, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new AdvisorAnswer("Test OK", configuration.Model)); };
    public void ValidateSettings(string json) => Validate(json);
    public string GetModel(string json) => Model(json);
    public Task<AdvisorAnswer> TestAsync(ConnectionSettings connection, IAdvisorConnectionServices services, string message, CancellationToken token) => Test(connection, services, message, token);
}

public sealed class StandardExtension : Definition, IStandardAdvisorExtension
{
    public Func<ConnectionSettings, IAdvisorConnectionServices, ConsultationInput, RetrievalHistory?, IAdvisorSession> Open { get; set; }
        = (_, _, _, _) => throw new InvalidOperationException("Fixture session not configured");
    public IAdvisorSession OpenSession(ConnectionSettings connection, IAdvisorConnectionServices services, ConsultationInput input, RetrievalHistory? history)
        => Open(connection, services, input, history);
}

public sealed class ToolListExtension : Definition, IAdvisorToolListExtension
{
    public Func<ConnectionSettings, IAdvisorConnectionServices, ConsultationInput, RetrievalHistory?, IAdvisorSession> Open { get; set; }
        = (_, _, _, _) => throw new InvalidOperationException("Fixture session not configured");
    public IAdvisorSession OpenSession(ConnectionSettings connection, IAdvisorConnectionServices services, ConsultationInput input, RetrievalHistory? history)
        => Open(connection, services, input, history);
}

public sealed class IndependentExtension : Definition, IIndependentAdvisorExtension
{
    public Func<ConnectionSettings, IAdvisorConnectionServices, ConsultationContext, CancellationToken, Task<AdvisorAnswer>> Consult { get; set; }
        = (_, _, _, _) => throw new InvalidOperationException("Fixture consultation not configured");
    public Action Dispose { get; set; } = () => { };
    public IAdvisorConsultation OpenConsultation(ConnectionSettings connection, IAdvisorConnectionServices services) => new Consultation(connection, services, Consult, Dispose);
    private sealed class Consultation : IAdvisorConsultation
    {
        private readonly ConnectionSettings connection;
        private readonly IAdvisorConnectionServices services;
        private readonly Func<ConnectionSettings, IAdvisorConnectionServices, ConsultationContext, CancellationToken, Task<AdvisorAnswer>> run;
        private readonly Action dispose;
        internal Consultation(ConnectionSettings connection, IAdvisorConnectionServices services,
            Func<ConnectionSettings, IAdvisorConnectionServices, ConsultationContext, CancellationToken, Task<AdvisorAnswer>> run, Action dispose)
        { this.connection = connection; this.services = services; this.run = run; this.dispose = dispose; }
        public Task<AdvisorAnswer> ConsultAsync(ConsultationContext context, CancellationToken token) => run(connection, services, context, token);
        public void Dispose() => dispose();
    }
}

public sealed class Session : IAdvisorSession
{
    private readonly Func<bool, ProviderRequest> prepare;
    private readonly Action<IReadOnlyList<ToolResult>> accept;
    private readonly Action dispose;
    public Session(Func<bool, ProviderRequest> prepare, Action<IReadOnlyList<ToolResult>> accept, Action dispose)
    { this.prepare = prepare; this.accept = accept; this.dispose = dispose; }
    public ProviderRequest Prepare(bool allowTools) => prepare(allowTools);
    public void AcceptResults(IReadOnlyList<ToolResult> results) => accept(results);
    public void Dispose() => dispose();
}
