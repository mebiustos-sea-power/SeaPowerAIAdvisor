using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Api = SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Serialization is confined to this boundary. Public extensions never receive host objects,
// mutable preferences, provider credentials belonging to another connection, or game references.
internal static class AdvisorExtensionBridge
{
    internal static string Json(JToken value) => value.ToString(Formatting.None);
    internal static JObject Object(string json, string errorCode = "invalid_response", int maximum = AiHttpTransport.MaxResponseBytes)
    {
        if (json == null || Encoding.UTF8.GetByteCount(json) > maximum) throw new Api.AdvisorException(errorCode);
        try
        {
            using var input = new StringReader(json);
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException();
            return result;
        }
        catch (JsonException) { throw new Api.AdvisorException(errorCode); }
    }
    internal static Api.ConsultationInput Input(AiProviderInput input) =>
        new(Json(input.Data), input.SystemInstruction, input.Tools.Select(Json));
    internal static Api.RetrievalHistory? History(AiContinuation? value) => value == null ? null : new(Json(value.Retrievals), value.RoundsUsed, value.MaxRounds, Json(value.Operations));
    internal static Api.ConnectionSettings Configuration(AdvisorService service, AdvisorApiConfiguration configuration,
        bool fallback) => new(configuration.Id, configuration.Name, configuration.Provider,
        Json(configuration.Settings), service.Model(configuration), configuration.RequestTimeoutSeconds, configuration.MaxOutputTokens, fallback);
    internal static AiConnectionException Error(Api.AdvisorException error) => new(error.Code, error.HttpStatus);
    internal static T Invoke<T>(Func<T> action)
    { try { return action(); } catch (Api.AdvisorException error) { throw Error(error); } }
    internal static async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    { try { return await action().ConfigureAwait(false); } catch (Api.AdvisorException error) { throw Error(error); } }
    internal static AdviceResult Answer(Api.AdvisorAnswer answer)
    {
        if (answer == null || string.IsNullOrWhiteSpace(answer.Text) || answer.Model == null ||
            answer.ApiRequests < 0 || answer.TotalTokens < 0) throw new AiConnectionException("invalid_response");
        return new AdviceResult(answer.Text, answer.ApiRequests, 0, answer.TotalTokens, model: answer.Model);
    }
}

internal sealed class AdvisorExtensionServices : Api.IAdvisorConnectionServices
{
    private readonly AiHttpTransport transport;
    internal AiHttpTransport Transport => transport;
    private readonly Api.ConnectionSettings settings;
    private readonly HashSet<int> pending = new();
    private readonly object sync = new();
    internal AdvisorExtensionServices(AiHttpTransport transport, Api.ConnectionSettings settings)
    { this.transport = transport; this.settings = settings; }
    public async Task<string> PostJsonAsync(Uri endpoint, string bodyJson, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        try
        {
            return AdvisorExtensionBridge.Json(await transport.SendAsync(endpoint,
                AdvisorExtensionBridge.Object(bodyJson, "api_bad_request"), settings.RequestTimeoutSeconds,
                settings.IsSubConnection, settings.Model, cancellation, headers).ConfigureAwait(false));
        }
        catch (AiConnectionException error) { throw new Api.AdvisorException(error.Code, error.HttpStatus); }
    }
    public int BeginRequest(string bodyJson)
    {
        lock (sync)
        {
            var id = transport.BeginRequest(bodyJson, settings.IsSubConnection, settings.Model);
            pending.Add(id); return id;
        }
    }
    public void EndRequest(int request, int httpStatus, string body, bool complete = true)
    {
        lock (sync)
        {
            RequirePending(request); transport.EndRequest(request, httpStatus, body, complete);
            pending.Remove(request);
        }
    }
    public void FailRequest(int request, string errorCode, int? httpStatus = null)
    {
        lock (sync)
        {
            RequirePending(request); transport.FailRequest(request, errorCode, httpStatus);
            pending.Remove(request);
        }
    }
    private void RequirePending(int id)
    { if (!pending.Contains(id)) throw new InvalidOperationException("Unknown or completed extension diagnostic request."); }
}

internal sealed class ExternalAdvisorService : AdvisorService
{
    private readonly Api.IAdvisorExtension extension;
    private readonly string id, name, protocol;
    internal override string Id => id;
    internal override string Name => name;
    internal override string? Version { get; }
    internal override string Protocol => protocol;
    internal override IReadOnlyList<AiConnectionField> Fields { get; }
    internal override bool Independent => extension is Api.IIndependentAdvisorExtension;
    internal override string Summary(AdvisorText text) => extension is Api.IAdvisorExtensionSummary summary
        ? AdvisorExtensionBridge.Invoke(() => summary.GetSummary(text.Language))
            ?? throw new InvalidOperationException("The advisor extension returned no adapter summary.")
        : "";
    internal override string Description(AdvisorText text) => extension is Api.IAdvisorExtensionDescription description
        ? AdvisorExtensionBridge.Invoke(() => description.GetDescription(text.Language))
            ?? throw new InvalidOperationException("The advisor extension returned no connection description.")
        : "";
    internal ExternalAdvisorService(Api.IAdvisorExtension extension)
    {
        this.extension = extension; id = extension.Id; name = extension.Name; protocol = extension.Protocol;
        Version = AdvisorExtensionVersion.Read(extension);
        Fields = Array.AsReadOnly(extension.Fields.Select(field =>
            new AiConnectionField(field.Key, field.Label, field.DefaultValue, field.Secret, field.GetLabel, field.Numeric)).ToArray());
    }
    internal override void Validate(JObject settings) => AdvisorExtensionBridge.Invoke(() =>
    { extension.ValidateSettings(AdvisorExtensionBridge.Json(settings)); return true; });
    internal override string Model(AdvisorApiConfiguration configuration)
    {
        var model = AdvisorExtensionBridge.Invoke(() => extension.GetModel(AdvisorExtensionBridge.Json(configuration.Settings)));
        if (model == null || model.Length > 200 || model.Any(char.IsControl)) throw new AiConnectionException("model_invalid");
        return model;
    }
    internal override IAiProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AiHttpTransport transport)
    {
        if (extension is not Api.IStandardAdvisorExtension standard) throw new InvalidOperationException("The extension owns its consultation workflow.");
        var settings = AdvisorExtensionBridge.Configuration(this, configuration, fallback);
        return new Provider(standard, settings, new AdvisorExtensionServices(transport, settings));
    }
    internal override IAdvisorBackend OpenIndependent(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AiHttpTransport transport, AiContinuation continuation)
    {
        var settings = AdvisorExtensionBridge.Configuration(this, configuration, fallback);
        var execution = AdvisorExtensionBridge.Invoke(() => ((Api.IIndependentAdvisorExtension)extension)
            .OpenConsultation(settings, new AdvisorExtensionServices(transport, settings)))
            ?? throw new InvalidOperationException("The extension returned no consultation.");
        return new IndependentBackend(execution, settings, continuation, () => transport.RequestCount);
    }
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AiHttpTransport transport, string request, CancellationToken cancellation)
    {
        var settings = AdvisorExtensionBridge.Configuration(this, configuration, false);
        var answer = await AdvisorExtensionBridge.InvokeAsync(() => extension.TestAsync(settings,
            new AdvisorExtensionServices(transport, settings), request, cancellation)).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested(); return AdvisorExtensionBridge.Answer(answer);
    }

    private sealed class Provider : IAiProvider
    {
        private readonly Api.IStandardAdvisorExtension extension;
        private readonly Api.ConnectionSettings settings;
        private readonly Api.IAdvisorConnectionServices services;
        public string Model => settings.Model;
        internal Provider(Api.IStandardAdvisorExtension extension, Api.ConnectionSettings settings, Api.IAdvisorConnectionServices services)
        { this.extension = extension; this.settings = settings; this.services = services; }
        public IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation) =>
            new Session(AdvisorExtensionBridge.Invoke(() => extension.OpenSession(settings, services,
                AdvisorExtensionBridge.Input(input), AdvisorExtensionBridge.History(continuation)))
                ?? throw new InvalidOperationException("The extension returned no provider session."));
    }
    private sealed class Session : IAiProviderSession
    {
        private readonly Api.IAdvisorSession session;
        internal Session(Api.IAdvisorSession session) { this.session = session; }
        public AiProviderRequest Prepare(bool allowTools)
        {
            var request = AdvisorExtensionBridge.Invoke(() => session.Prepare(allowTools))
                ?? throw new InvalidOperationException("The extension returned no prepared request.");
            return new AiProviderRequest(request.Bytes, async cancellation =>
            {
                var result = await AdvisorExtensionBridge.InvokeAsync(() => request.SendAsync(cancellation)).ConfigureAwait(false);
                if (result == null || result.Text == null || result.TotalTokens < 0) throw new AiConnectionException("invalid_response");
                if (result.Calls.Count == 0 && string.IsNullOrWhiteSpace(result.Text)) throw new AiConnectionException("empty_response");
                return AdvisorExtensionBridge.Invoke(() => new AiReply(result.Text, result.Calls.Select(call =>
                    new AiToolCall(call.Id, call.Name, AdvisorExtensionBridge.Object(call.ArgumentsJson,
                        "invalid_function_call", AdvisorQueryLimits.MaxRequestBytes))), result.TotalTokens));
            });
        }
        public void AcceptResults(IReadOnlyList<AiToolResult> results) => AdvisorExtensionBridge.Invoke(() =>
        { session.AcceptResults(results.Select(r => new Api.ToolResult(r.Id, r.Name, AdvisorExtensionBridge.Json(r.Data))).ToArray()); return true; });
        public void Dispose() => session.Dispose();
    }
    private sealed class IndependentBackend : IAdvisorBackend, IDisposable
    {
        private readonly Api.IAdvisorConsultation execution;
        private readonly Api.ConnectionSettings settings;
        private readonly AiContinuation continuation;
        private readonly Func<int> requestCount;
        internal IndependentBackend(Api.IAdvisorConsultation execution, Api.ConnectionSettings settings, AiContinuation continuation, Func<int> requestCount)
        { this.execution = execution; this.settings = settings; this.continuation = continuation; this.requestCount = requestCount; }
        public async Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation)
        {
            var known = AdvisorRequestShape.Values((JObject)(context.Input.Tools.FirstOrDefault() ?? throw new InvalidOperationException("The independent workflow query definition is missing."))["parameters"]!["properties"]!["requests"]!["items"]!, "query").ToHashSet(StringComparer.Ordinal);
            var input = new Api.ConsultationContext(AdvisorExtensionBridge.Input(context.Input), new Knowledge(context.Knowledge),
                AdvisorExtensionBridge.History(continuation)!, progress =>
                {
                    if (progress == null || progress.ApiRequests < 0 || !Enum.IsDefined(typeof(Api.AdvisorPhase), progress.Phase))
                        throw new InvalidOperationException("Invalid extension progress metadata.");
                    var phase = progress.Phase switch
                    { Api.AdvisorPhase.Retrieving => AdvisorStage.Retrieving, Api.AdvisorPhase.FinalAnswer => AdvisorStage.FinalAnswer,
                        Api.AdvisorPhase.SwitchingConnection => AdvisorStage.SwitchingKey, _ => AdvisorStage.Waiting };
                    context.Progress?.Invoke(new AdvisorProgress(phase, Math.Max(progress.ApiRequests, requestCount()), context.Knowledge.RoundsUsed,
                        context.Knowledge.MaxRounds, progress.Queries.Select(q => known.Contains(q) ? q : "unknown_query"), settings.IsSubConnection, settings.Model));
                }, context.Operations == null ? null : new Operations(context.Operations));
            var answer = await AdvisorExtensionBridge.InvokeAsync(() => execution.ConsultAsync(input, cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); return AdvisorExtensionBridge.Answer(answer);
        }
        public void Dispose() => execution.Dispose();
    }
    private sealed class Operations : Api.IAdvisorOperations
    {
        private readonly IAdvisorOperationSession session;
        internal Operations(IAdvisorOperationSession session) => this.session = session;
        public async Task<string> ExecuteAsync(string requestJson, CancellationToken cancellation) =>
            AdvisorExtensionBridge.Json(await session.ExecuteAsync(AdvisorExtensionBridge.Object(requestJson,
                "invalid_operation_request", AdvisorQueryLimits.MaxRequestBytes), cancellation).ConfigureAwait(false));
    }
    private sealed class Knowledge : Api.IAdvisorKnowledge
    {
        private readonly IAdvisorQuerySession session;
        internal Knowledge(IAdvisorQuerySession session) { this.session = session; }
        public string ConsultationId => session.Id;
        public int RoundsUsed => session.RoundsUsed;
        public int MaxRounds => session.MaxRounds;
        public string Execute(string requestJson)
        {
            var request = AdvisorExtensionBridge.Object(requestJson, "invalid_function_call", AdvisorQueryLimits.MaxRequestBytes);
            if (request["consultation_id"] != null) throw new Api.AdvisorException("invalid_function_call");
            request["consultation_id"] = session.Id;
            return AdvisorExtensionBridge.Json(session.Execute(request));
        }
    }
}
