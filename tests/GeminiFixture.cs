using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

// Protocol tests inject Gemini replies; HTTP/logging tests go through the current service catalog.
internal sealed class GeminiFixture
{
    private readonly IGeminiTransport transport;
    private readonly Action<AdvisorConnectionException>? switched;
    internal GeminiFixture(IGeminiTransport transport, Action<AdvisorConnectionException>? switched = null)
    { this.transport = transport; this.switched = switched; }

    internal Task<AdviceResult> ConsultAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile = null,
        string commonPrompt = "", Action<AdvisorProgress>? progress = null,
        int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "") =>
        AdvisorConsultation.RunAsync(new AdvisorEngine(new GeminiProvider(transport, settings),
            settings.FallbackApiKey.Length == 0 ? null : new GeminiProvider(transport, settings.ForFallback()), switched),
            AdvisorContext.Create(session, history, profile, commonPrompt, progress, historyExchanges, userName), cancellation);

    internal static Task<AdviceResult> RunAsync(string directory, IAdvisorQuerySession session, AdvisorSettings settings,
        AdvisorQuestion question, IReadOnlyList<ChatExchange> history, CancellationToken cancellation,
        Func<AdvisorTrace?, AdvisorHttpTransport> transportFactory, AdvisorProfile? profile = null,
        string commonPrompt = "", Action<AdvisorProgress>? progress = null,
        int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "") =>
        AdvisorConsultation.RunAsync(AdvisorServices.Prepare(Draft(settings), transportFactory: transportFactory),
            AdvisorContext.Create(session, history, profile, commonPrompt, progress, historyExchanges, userName),
            directory, question, cancellation);

    internal static Task<AdviceResult> TestAsync(string directory, AdvisorSettings settings, CancellationToken cancellation,
        Func<AdvisorTrace?, AdvisorHttpTransport> transportFactory, string? requestText = null)
    {
        var draft = Draft(settings);
        return AdvisorServices.TestAsync(directory, draft.Main.Build(), draft.Options,
            requestText ?? AdvisorPrompts.ConnectionTestRequest(), cancellation, transportFactory: transportFactory);
    }
    internal static AdvisorTraceOptions Diagnostics(AdvisorSettings settings)
    {
        var draft = Draft(settings);
        return AdvisorServices.Diagnostics(draft.Options, draft.Main.Build(), draft.Sub?.Build(), AdvisorServiceCatalog.BuiltIn);
    }
    private static AdvisorSettingsDraft Draft(AdvisorSettings settings)
    {
        var main = new AdvisorApiConfiguration("test-main", "Main", settings.RequestApiKey, settings.RequestModel,
            settings.RequestTimeoutSeconds, settings.MaxOutputTokens);
        var entries = new List<AdvisorApiConfiguration> { main };
        AdvisorApiConfiguration? sub = null;
        if (!settings.IsFallbackKey && settings.FallbackApiKey.Length > 0)
        {
            var fallback = settings.ForFallback();
            sub = new AdvisorApiConfiguration("test-sub", "Sub", fallback.RequestApiKey, fallback.RequestModel,
                fallback.RequestTimeoutSeconds, fallback.MaxOutputTokens);
            entries.Add(sub);
        }
        return new AdvisorSettingsDraft(new AdvisorApiLibrary(entries, main.Id, sub?.Id).Document(new JObject(), settings.LogAiJson));
    }
}
