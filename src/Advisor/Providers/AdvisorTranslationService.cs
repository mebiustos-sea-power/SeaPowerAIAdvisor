using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Translation;

namespace SeaPowerAIAdvisor;

// The host resolves saved roles and diagnostics; translation itself knows neither the settings file nor an advisor conversation.
internal static class AdvisorTranslationService
{
    internal static Task<TranslationResult> RunAsync(string path, string logDirectory, TranslationBatch batch, CancellationToken cancellation) =>
        RunAsync(AdvisorSettings.Edit(path), logDirectory, batch, cancellation);

    internal static async Task<TranslationResult> RunAsync(AdvisorSettingsDraft settings, string logDirectory, TranslationBatch batch,
        CancellationToken cancellation, AdvisorServiceCatalog? catalog = null, Func<AdvisorTrace?, AiHttpTransport>? transportFactory = null)
    {
        catalog ??= AdvisorServiceCatalog.BuiltIn;
        var main = (settings.Translation ?? throw new TranslationException("disabled")).Build();
        // A shared fallback may also be the explicitly chosen translation connection. Do not retry that same connection against itself.
        var sub = settings.Sub?.Id == main.Id ? null : settings.Sub?.Build();
        var service = catalog.Require(main.Provider); var fallback = sub == null ? null : catalog.Require(sub.Provider);
        if (service.Independent || fallback?.Independent == true) throw new TranslationException("provider_unavailable");
        service.Validate(main.Settings); if (sub != null) fallback!.Validate(sub.Settings);
        var diagnostics = AdvisorServices.Diagnostics(settings.Options, main, sub, catalog);
        using var trace = AdvisorTrace.StartOperation(logDirectory, "translation-" + Guid.NewGuid().ToString("N"), diagnostics, "translation",
            new JObject { ["text_blocks"] = batch.Texts.Count, ["target_language"] = batch.Language });
        using var transport = transportFactory?.Invoke(trace) ?? new AiHttpTransport(trace);
        AdvisorServices.ObserveErrors(transport, main, sub, catalog, "translation", null);
        try
        {
            var result = await TranslationEngine.RunAsync(batch, service.Open(main, settings.Options, false, transport),
                sub == null ? null : fallback!.Open(sub, settings.Options, true, transport), main.Prompt, sub?.Prompt ?? "",
                error => { transport.CaptureFailure(error); trace?.Fallback(error); }, cancellation).ConfigureAwait(false);
            trace?.FinishOperation(new JObject { ["outcome"] = "completed", ["api_requests"] = result.ApiRequests,
                ["model"] = result.Model, ["total_tokens"] = result.TotalTokens });
            return result;
        }
        catch (OperationCanceledException)
        { trace?.FinishOperation(new JObject { ["outcome"] = "cancelled" }); throw; }
        catch (Exception error)
        {
            transport.CaptureFailure(error);
            trace?.FinishOperation(new JObject { ["outcome"] = "failed", ["error_code"] = error is TranslationException translation ? translation.Code :
                error is AiConnectionException connection ? connection.Code : "unexpected_error" });
            throw;
        }
    }
}
