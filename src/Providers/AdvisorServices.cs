using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Api = SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorConnectionField
{
    internal string Key { get; }
    internal string Label { get; }
    internal string DefaultValue { get; }
    internal bool Secret { get; }
    internal bool Numeric { get; }
    private readonly Func<string, string>? label;
    internal string Caption(AdvisorText text) => label == null ? text[Label] : label(text.Language);
    internal AdvisorConnectionField(string key, string label, string defaultValue = "", bool secret = false,
        Func<string, string>? caption = null, bool numeric = false)
    { Key = key; Label = label; DefaultValue = defaultValue; Secret = secret; Numeric = numeric; this.label = caption; }
}

// Provider definitions own fields, validation and wire adapters. The form only renders their declarations.
internal abstract class AdvisorService
{
    internal abstract string Id { get; }
    internal abstract string Name { get; }
    internal virtual string? Version => null;
    internal virtual string Caption(AdvisorText text) => Name;
    internal abstract string Protocol { get; }
    internal virtual string Summary(AdvisorText text) => "";
    internal virtual string Description(AdvisorText text) => "";
    internal virtual bool Independent => false;
    internal virtual string Model(AdvisorApiConfiguration configuration) => configuration.Model;
    internal virtual IAdvisorBackend OpenIndependent(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport, AdvisorContinuation continuation) =>
        throw new InvalidOperationException("This provider uses the standard engine.");
    internal abstract IReadOnlyList<AdvisorConnectionField> Fields { get; }
    internal JObject Defaults() => new(Fields.Select(f => new JProperty(f.Key, f.DefaultValue)));
    internal abstract void Validate(JObject settings);
    internal abstract IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport);
    internal abstract Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation);
    internal IEnumerable<string> Secrets(AdvisorApiConfiguration configuration) => Fields.Where(f => f.Secret)
        .Select(f => (string?)configuration.Settings[f.Key] ?? "");
    protected void ValidateFields(JObject settings)
    {
        if (settings.Properties().Any(p => !Fields.Any(f => f.Key == p.Name)) ||
            Fields.Any(f => settings[f.Key]?.Type != JTokenType.String))
            throw new AdvisorConnectionException("settings_invalid");
    }
}

internal sealed class AdvisorServiceCatalog
{
    internal static AdvisorServiceCatalog BuiltIn { get; } = new(new AdvisorService[] { new GeminiService(), new OllamaService(), new OpenRouterService(), new NanoGptService() });
    private readonly object sync = new();
    private readonly List<AdvisorService> services = new();
    internal IReadOnlyList<AdvisorService> Services { get { lock (sync) return Array.AsReadOnly(services.ToArray()); } }
    internal AdvisorServiceCatalog(IEnumerable<AdvisorService> services)
    { foreach (var service in services) Register(new BuiltInAdvisorExtension(service)); }
    internal void Register(Api.IAdvisorExtension extension)
    {
        if (extension.ApiVersion != Api.AdvisorApi.ContractVersion)
            throw new NotSupportedException("Unsupported AI Advisor contract version.");
        if ((extension is Api.IStandardAdvisorExtension) == (extension is Api.IIndependentAdvisorExtension))
            throw new ArgumentException("Implement exactly one advisor execution contract.", nameof(extension));
        static bool Identifier(string value) => value != null && value.Length is > 0 and <= 80 &&
            value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || c is '_' or '-' or '.');
        if (!Identifier(extension.Id) || !Identifier(extension.Protocol) || string.IsNullOrWhiteSpace(extension.Name) ||
            extension.Name.Length > 120 || extension.Name.Any(char.IsControl) || extension.Fields == null || extension.Fields.Count > 32 ||
            extension.Fields.Any(f => f == null || !Identifier(f.Key) || string.IsNullOrWhiteSpace(f.Label) || f.DefaultValue == null) ||
            extension.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != extension.Fields.Count)
            throw new ArgumentException("Invalid advisor extension metadata.", nameof(extension));
        var service = extension is BuiltInAdvisorExtension builtIn ? builtIn.Service : new ExternalAdvisorService(extension);
        lock (sync)
        {
            if (services.Any(s => s.Id == service.Id)) throw new InvalidOperationException("Duplicate connection provider ID: " + service.Id);
            services.Add(service);
        }
    }
    internal AdvisorService? Find(string id) { lock (sync) return services.SingleOrDefault(s => s.Id == id); }
    internal AdvisorService Require(string id) => Find(id) ?? throw new AdvisorConnectionException("provider_unavailable");
}

// Logging and legacy request deadlines are shared; generation limits belong to each connection.
internal sealed class AdvisorConnectionOptions
{
    internal int RequestTimeoutSeconds { get; }
    internal bool LogAiJson { get; }
    internal JObject Json { get; }
    internal AdvisorConnectionOptions(JObject source)
    {
        RequestTimeoutSeconds = AdvisorRequestTimeout.Read(source);
        if (source["log_ai_json"] != null && source["log_ai_json"]!.Type != JTokenType.Boolean)
            throw new AdvisorConnectionException("settings_invalid");
        LogAiJson = (bool?)source["log_ai_json"] ?? false;
        Json = new JObject { ["request_timeout_seconds"] = RequestTimeoutSeconds,
            ["log_ai_json"] = LogAiJson };
    }
}

// Built-in standard strategy is one selectable backend; independent backends remain replaceable.
internal static class AdvisorServices
{
    internal static AdvisorBackendRegistry CreateRegistry(string path, AdvisorServiceCatalog? catalog = null,
        Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null)
    {
        var registry = new AdvisorBackendRegistry();
        registry.Register("standard", (allowFallback, delivery) => Prepare(AdvisorSettings.Edit(path), catalog, transportFactory, allowFallback: allowFallback, situationDelivery: delivery));
        registry.Select("standard");
        return registry;
    }
    internal static AdvisorBackendSetup Prepare(AdvisorSettingsDraft settings, AdvisorServiceCatalog? catalog = null,
        Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null, AdvisorErrorHistory? errorHistory = null, bool allowFallback = true, AdvisorSituationDelivery? situationDelivery = null)
    {
        catalog ??= AdvisorServiceCatalog.BuiltIn;
        var main = Delivery(settings.Main.Build(), situationDelivery);
        var sub = allowFallback && settings.Sub != null ? Delivery(settings.Sub.Build(), situationDelivery) : null;
        var options = settings.Options;
        var mainService = catalog.Require(main.Provider); ValidateDelivery(mainService, main); mainService.Validate(main.Settings);
        var subService = sub == null ? null : catalog.Require(sub.Provider);
        if (sub != null) { ValidateDelivery(subService!, sub); subService!.Validate(sub.Settings); }
        var diagnostics = Diagnostics(options, main, sub, catalog);
        return new AdvisorBackendSetup(diagnostics, trace =>
        {
            var transport = transportFactory?.Invoke(trace) ?? new AdvisorHttpTransport(trace);
            ObserveErrors(transport, main, sub, catalog, "consultation", errorHistory);
            void Switched(AdvisorConnectionException error) { transport.CaptureFailure(error); trace?.Fallback(error); }
            try
            {
                IAdvisorBackend backend = mainService.Independent || subService?.Independent == true
                    ? new AdvisorExtensionBackend(mainService, subService, main, sub, options, transport, Switched)
                    : new AdvisorEngine(mainService.Open(main, options, false, transport),
                        sub == null ? null : subService!.Open(sub, options, true, transport), Switched, delivery: main.SituationDelivery, fallbackDelivery: sub?.SituationDelivery, connectionPrompt: main.Prompt, fallbackPrompt: sub?.Prompt ?? "");
                return new AdvisorBackendHandle(new AdvisorErrorBackend(backend, transport), transport);
            }
            catch (Exception error) { transport.CaptureFailure(error); transport.Dispose(); throw; }
        }, main.Name, sub?.Name ?? "", mainService.Independent ? null : main.SituationDelivery);
    }
    private static AdvisorApiConfiguration Delivery(AdvisorApiConfiguration configuration, AdvisorSituationDelivery? mode) => !mode.HasValue ? configuration :
        new(configuration.Id, configuration.Name, configuration.Provider, configuration.Settings, configuration.RequestTimeoutSeconds, mode.Value, configuration.MaxOutputTokens, configuration.Prompt);

    internal static void ValidateDelivery(AdvisorService service, AdvisorApiConfiguration configuration)
    {
        if (service.Independent && configuration.SituationDelivery != AdvisorSituationDelivery.OnDemand)
            throw new AdvisorConnectionException("situation_delivery_unavailable");
    }
    internal static AdvisorTraceOptions Diagnostics(AdvisorConnectionOptions options, AdvisorApiConfiguration main,
        AdvisorApiConfiguration? sub, AdvisorServiceCatalog catalog) => new(options.LogAiJson,
        catalog.Require(main.Provider).Protocol, catalog.Require(main.Provider).Model(main),
        sub == null ? "" : catalog.Require(sub.Provider).Model(sub),
        catalog.Require(main.Provider).Secrets(main).Concat(sub == null ? Array.Empty<string>() : catalog.Require(sub.Provider).Secrets(sub)));

    internal static async Task<AdviceResult> TestAsync(string directory, AdvisorApiConfiguration configuration,
        AdvisorConnectionOptions options, string request, CancellationToken cancellation,
        AdvisorServiceCatalog? catalog = null, Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null, AdvisorErrorHistory? errorHistory = null)
    {
        catalog ??= AdvisorServiceCatalog.BuiltIn;
        var service = catalog.Require(configuration.Provider); service.Validate(configuration.Settings);
        using var trace = AdvisorTrace.Start(directory, "connection-test-" + Guid.NewGuid().ToString("N"),
            Diagnostics(options, configuration, null, catalog), new AdvisorQuestion("Connection test"));
        return await AdvisorConsultation.RunAsync(async () =>
        {
            using var transport = transportFactory?.Invoke(trace) ?? new AdvisorHttpTransport(trace);
            ObserveErrors(transport, configuration, null, catalog, "connection_test", errorHistory);
            try { return await service.TestAsync(configuration, options, transport, request, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { transport.CaptureFailure(error); throw; }
        }, trace, cancellation).ConfigureAwait(false);
    }
    private static void ObserveErrors(AdvisorHttpTransport transport, AdvisorApiConfiguration main, AdvisorApiConfiguration? sub,
        AdvisorServiceCatalog catalog, string operation, AdvisorErrorHistory? history)
    {
        history ??= AdvisorErrorHistory.Active;
        if (history == null) return;
        var mainService = catalog.Require(main.Provider);
        var subService = sub == null ? null : catalog.Require(sub.Provider);
        transport.ObserveErrors(new AdvisorErrorCapture(history, new AdvisorErrorConnection(mainService, main),
            sub == null ? null : new AdvisorErrorConnection(subService!, sub), operation,
            mainService.Secrets(main).Concat(sub == null ? Array.Empty<string>() : subService!.Secrets(sub))));
    }

}
