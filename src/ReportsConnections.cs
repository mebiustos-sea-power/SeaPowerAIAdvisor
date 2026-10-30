using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Crew;

namespace SeaPowerAIAdvisor;

// MOD-level bridge: credential storage and adapter diagnostics are not part of the tactical conversation.
internal sealed class ReportsConnections : ICrewConnections
{
    private readonly string path, logDirectory;
    private readonly Func<AdvisorText> text;
    private readonly CrewSettingsStore settings;
    private readonly AdvisorServiceCatalog catalog;
    private readonly Func<AdvisorTrace?, AiHttpTransport> transportFactory;
    public IAiConnectionStore Store { get; }
    public IReadOnlyList<IAiConnectionProvider> Providers => catalog.Services.Where(s => !s.Independent).Cast<IAiConnectionProvider>().ToArray();
    internal ReportsConnections(string path, string logDirectory, CrewSettingsStore settings, Func<AdvisorText> text, AdvisorServiceCatalog? catalog = null, Func<AdvisorTrace?, AiHttpTransport>? transportFactory = null)
    {
        this.transportFactory = transportFactory ?? (trace => new AiHttpTransport(trace));
        this.text = text; this.path = path; this.logDirectory = logDirectory; this.settings = settings; this.catalog = catalog ?? AdvisorServiceCatalog.BuiltIn;
        Store = new AdvisorApiSettingsStore(path, () => text()["connection.configuration_name_pattern"]);
    }
    public string Error(AiConnectionException error) => AdvisorNotice.Connection(error).Message(text());
    public string? Role(string id)
    {
        var saved = AdvisorSettings.Edit(path); var labels = text(); var roles = new List<string>();
        if (id == saved.MainId) roles.Add(labels["connection.card_main"]);
        if (id == saved.SubId) roles.Add(labels["connection.card_sub"]);
        if (id == saved.TranslationConnectionId) roles.Add(labels["connection.card_translation"]);
        if (id == settings.Current.ConnectionId) roles.Add(labels["connection.card_reports"]);
        return roles.Count == 0 ? null : string.Join(" / ", roles);
    }
    public async Task<CrewGeneration> GenerateAsync(CrewBatch batch, string id, CancellationToken cancellation, Action<long?> recordUsage)
    {
        var saved = AdvisorSettings.Edit(path);
        var profile = (saved.Find(id) ?? throw new CrewException("connection_missing")).Build();
        var service = catalog.Require(profile.Provider);
        if (service.Independent) throw new CrewException("provider_unavailable");
        service.Validate(profile.Settings);
        using var trace = AdvisorTrace.StartOperation(logDirectory, "reports-" + Guid.NewGuid().ToString("N"),
            AdvisorServices.Diagnostics(saved.Options, profile, null, catalog), "crew_reports", new JObject());
        using var transport = transportFactory(trace);
        AdvisorServices.ObserveErrors(transport, profile, null, catalog, "crew_reports", null);
        var requested = false; long? tokens = null;
        try
        {
            using var connection = new AiConnection(service.Open(profile, saved.Options, false, transport), null,
                batch.Input(profile.Prompt), _ => throw new InvalidOperationException("Reports has no fallback assignment."));
            var reply = await connection.GenerateAsync(true, null, cancellation, (_, _, _, bytes) =>
            { if (bytes > 1_000_000) throw new CrewException("too_large"); requested = true; }).ConfigureAwait(false);
            tokens = reply.TotalTokens;
            cancellation.ThrowIfCancellationRequested();
            var result = batch.Read(reply);
            trace?.FinishOperation(new JObject { ["outcome"] = "completed", ["api_requests"] = connection.ApiRequests,
                ["model"] = connection.Model, ["total_tokens"] = reply.TotalTokens });
            return result;
        }
        catch (OperationCanceledException) { trace?.FinishOperation(new JObject { ["outcome"] = "cancelled" }); throw; }
        catch (Exception error)
        {
            transport.CaptureFailure(error);
            trace?.FinishOperation(new JObject { ["outcome"] = "failed", ["error_code"] = error is CrewException crew ? crew.Code : error is AiConnectionException ai ? ai.Code : "unexpected_error" });
            throw;
        }
        finally { if (requested) recordUsage(tokens); }
    }
    public async Task<string> TestAsync(AiConnectionDraft draft, CancellationToken cancellation)
    {
        var store = (AdvisorApiSettingsStore)Store;
        var result = await AdvisorServices.TestAsync(logDirectory, ((AdvisorApiConfigurationDraft)draft).Build(), store.TestOptions(),
            "Reply with OK.", cancellation, catalog, transportFactory).ConfigureAwait(false);
        return result.Text;
    }
}
