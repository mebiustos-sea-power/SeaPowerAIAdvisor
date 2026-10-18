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
    private readonly Func<string, string>? label;
    internal string Caption(AdvisorText text) => label == null ? text[Label] : label(text.Language);
    internal AdvisorConnectionField(string key, string label, string defaultValue = "", bool secret = false,
        Func<string, string>? caption = null)
    { Key = key; Label = label; DefaultValue = defaultValue; Secret = secret; this.label = caption; }
}

// Provider definitions own fields, validation and wire adapters. The form only renders their declarations.
internal abstract class AdvisorService
{
    internal abstract string Id { get; }
    internal abstract string Name { get; }
    internal abstract string Protocol { get; }
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
    internal static AdvisorServiceCatalog BuiltIn { get; } = new(new AdvisorService[] { new GeminiService(), new OllamaService() });
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

// Existing file name remains stable; provider-specific values live inside each registered entry.
internal sealed class AdvisorConnectionOptions
{
    internal int RequestTimeoutSeconds { get; }
    internal int MaxOutputTokens { get; }
    internal bool LogAiJson { get; }
    internal JObject Json { get; }
    internal AdvisorConnectionOptions(JObject source)
    {
        RequestTimeoutSeconds = AdvisorRequestTimeout.Read(source);
        MaxOutputTokens = Number(source, "max_output_tokens", 4096, 256, 8192);
        if (source["log_ai_json"] != null && source["log_ai_json"]!.Type != JTokenType.Boolean)
            throw new AdvisorConnectionException("settings_invalid");
        LogAiJson = (bool?)source["log_ai_json"] ?? false;
        Json = new JObject { ["request_timeout_seconds"] = RequestTimeoutSeconds,
            ["max_output_tokens"] = MaxOutputTokens, ["log_ai_json"] = LogAiJson };
    }
    private static int Number(JObject value, string name, int fallback, int min, int max)
    {
        if (value[name] == null) return fallback;
        if (value[name]!.Type != JTokenType.Integer || !int.TryParse(value[name]!.ToString(), out var n) || n < min || n > max)
            throw new AdvisorConnectionException("settings_invalid");
        return n;
    }
}

// Built-in standard strategy is one selectable backend; independent backends remain replaceable.
internal static class AdvisorServices
{
    internal static AdvisorBackendRegistry CreateRegistry(string path, AdvisorServiceCatalog? catalog = null,
        Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null)
    {
        var registry = new AdvisorBackendRegistry();
        registry.Register("standard", () => Prepare(AdvisorSettings.Edit(path), catalog, transportFactory));
        registry.Select("standard");
        return registry;
    }
    internal static AdvisorBackendSetup Prepare(AdvisorSettingsDraft settings, AdvisorServiceCatalog? catalog = null,
        Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null)
    {
        catalog ??= AdvisorServiceCatalog.BuiltIn;
        var main = settings.Main.Build(); var sub = settings.Sub?.Build();
        var options = settings.Options;
        var mainService = catalog.Require(main.Provider); mainService.Validate(main.Settings);
        var subService = sub == null ? null : catalog.Require(sub.Provider);
        if (sub != null) subService!.Validate(sub.Settings);
        var diagnostics = Diagnostics(options, main, sub, catalog);
        return new AdvisorBackendSetup(diagnostics, trace =>
        {
            var transport = transportFactory?.Invoke(trace) ?? new AdvisorHttpTransport(trace);
            try
            {
                IAdvisorBackend backend = mainService.Independent || subService?.Independent == true
                    ? new AdvisorExtensionBackend(mainService, subService, main, sub, options, transport, error => trace?.Fallback(error))
                    : new AdvisorEngine(mainService.Open(main, options, false, transport),
                        sub == null ? null : subService!.Open(sub, options, true, transport), error => trace?.Fallback(error));
                return new AdvisorBackendHandle(backend, transport);
            }
            catch { transport.Dispose(); throw; }
        });
    }
    internal static AdvisorTraceOptions Diagnostics(AdvisorConnectionOptions options, AdvisorApiConfiguration main,
        AdvisorApiConfiguration? sub, AdvisorServiceCatalog catalog) => new(options.LogAiJson,
        catalog.Require(main.Provider).Protocol, catalog.Require(main.Provider).Model(main),
        sub == null ? "" : catalog.Require(sub.Provider).Model(sub),
        catalog.Require(main.Provider).Secrets(main).Concat(sub == null ? Array.Empty<string>() : catalog.Require(sub.Provider).Secrets(sub)));

    internal static async Task<AdviceResult> TestAsync(string directory, AdvisorApiConfiguration configuration,
        AdvisorConnectionOptions options, string request, CancellationToken cancellation,
        AdvisorServiceCatalog? catalog = null, Func<AdvisorTrace?, AdvisorHttpTransport>? transportFactory = null)
    {
        catalog ??= AdvisorServiceCatalog.BuiltIn;
        var service = catalog.Require(configuration.Provider); service.Validate(configuration.Settings);
        using var trace = AdvisorTrace.Start(directory, "connection-test-" + Guid.NewGuid().ToString("N"),
            Diagnostics(options, configuration, null, catalog), new AdvisorQuestion("Connection test"));
        return await AdvisorConsultation.RunAsync(async () =>
        {
            using var transport = transportFactory?.Invoke(trace) ?? new AdvisorHttpTransport(trace);
            return await service.TestAsync(configuration, options, transport, request, cancellation).ConfigureAwait(false);
        }, trace, cancellation).ConfigureAwait(false);
    }
}
