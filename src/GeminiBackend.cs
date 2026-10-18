using System;

namespace SeaPowerAIAdvisor;

// Built-in registration and configuration belong to Gemini, not to the backend registry or host.
internal static class GeminiBackend
{
    internal const string Id = "gemini";
    internal static AdvisorBackendRegistry CreateRegistry(string settingsPath, Func<AdvisorTrace?, GeminiTransport>? transportFactory = null)
    {
        var registry = new AdvisorBackendRegistry();
        registry.Register(Id, () => Prepare(AdvisorSettings.Load(settingsPath), transportFactory));
        registry.Select(Id);
        return registry;
    }

    internal static AdvisorTraceOptions Diagnostics(AdvisorSettings settings) => new(settings.LogAiJson, "interactions",
        settings.RequestModel, settings.FallbackModel, new[] { settings.ApiKey, settings.FallbackApiKey });

    internal static AdvisorBackendSetup Prepare(AdvisorSettings settings, Func<AdvisorTrace?, GeminiTransport>? transportFactory = null) =>
        new(Diagnostics(settings), trace =>
        {
            var transport = transportFactory == null ? new GeminiTransport(trace) : transportFactory(trace);
            try
            {
                var backend = new AdvisorEngine(new GeminiProvider(transport, settings),
                    settings.FallbackApiKey.Length == 0 ? null : new GeminiProvider(transport, settings.ForFallback()), error => trace?.Fallback(error));
                return new AdvisorBackendHandle(backend, transport);
            }
            catch { transport.Dispose(); throw; }
        });
}
