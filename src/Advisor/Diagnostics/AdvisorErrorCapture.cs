using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Captures provider failures independently of optional full communication logging.
internal sealed class AdvisorErrorCapture : IAiErrorObserver
{
    private readonly object gate = new();
    private readonly AdvisorErrorHistory history;
    private readonly SecretRedactor redactor;
    private readonly AdvisorErrorConnection main;
    private readonly AdvisorErrorConnection? sub;
    private readonly string operation;
    private readonly Dictionary<int, Attempt> requests = new();
    private Attempt? last;
    internal AdvisorErrorCapture(AdvisorErrorHistory history, AdvisorErrorConnection main, AdvisorErrorConnection? sub,
        string operation, IEnumerable<string> secrets)
    { this.history = history; this.main = main; this.sub = sub; this.operation = operation; redactor = new SecretRedactor(secrets); }
    public void Begin(int id, bool fallback, string model)
    {
        lock (gate)
        {
            foreach (var key in requests.Where(p => p.Value.Received || p.Value.Recorded).Select(p => p.Key).ToArray()) requests.Remove(key);
            last = new Attempt(id, fallback, model); requests.Add(id, last);
        }
    }
    public void Response(int id, int status, string body, bool complete)
    {
        lock (gate)
        {
            if (!requests.TryGetValue(id, out var attempt)) return;
            attempt.Received = true; attempt.Status = status; attempt.Body = redactor.RedactBody(body); attempt.Complete = complete; last = attempt;
            // A custom adapter can consume/retry an HTTP error without throwing it to the host.
            if (status is < 200 or >= 300) Record(new AiConnectionException(AiHttpTransport.ErrorCode(status), status), attempt, null);
        }
    }
    public void Failure(Exception error, int? request = null, Exception? cause = null)
    {
        if (error is OperationCanceledException || error is AiConnectionException { Code: "cancelled" }) return;
        lock (gate)
        {
            var attempt = request.HasValue && requests.TryGetValue(request.Value, out var found) ? found : last;
            // Opening the sub adapter can fail before it starts its first HTTP request.
            if (error is AiConnectionException { UsingFallback: true } && attempt?.Fallback == false) attempt = null;
            Record(error, attempt, cause);
        }
    }
    private void Record(Exception error, Attempt? attempt, Exception? cause)
    {
        var connectionError = error as AiConnectionException;
        if (connectionError?.HasDetails == true) return;
        if (connectionError != null) connectionError.HasDetails = true;
        if (attempt?.Recorded == true) return;
        if (attempt != null) attempt.Recorded = true;
        var fallback = attempt?.Fallback ?? (connectionError?.UsingFallback == true);
        var connection = fallback && sub != null ? sub : main;
        var record = new JObject
        {
            ["operation"] = operation, ["connection_id"] = connection.Id, ["connection_name"] = connection.Name,
            ["provider_id"] = connection.ProviderId, ["provider"] = connection.ProviderName,
            ["model"] = attempt?.Model ?? connection.Model, ["key_slot"] = fallback ? "sub" : "main",
            ["api_request"] = attempt?.Id, ["error_code"] = connectionError?.Code ?? "unexpected_error",
            ["http_status"] = attempt?.Status ?? connectionError?.HttpStatus,
            ["reported_http_status"] = connectionError?.HttpStatus, ["response_received"] = attempt?.Received ?? false,
            ["body_complete"] = attempt?.Complete ?? false, ["body_json"] = attempt?.Body,
            ["exception"] = redactor.Scrub((cause ?? error).ToString())
        };
        redactor.RedactToken(record);
        try { history.Append(record); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { history.WriteFailed(redactor.Scrub(failure.ToString())); }
    }
    private sealed class Attempt
    {
        internal readonly int Id;
        internal readonly bool Fallback;
        internal readonly string Model;
        internal int? Status;
        internal string? Body;
        internal bool Received, Complete, Recorded;
        internal Attempt(int id, bool fallback, string model) { Id = id; Fallback = fallback; Model = model; }
    }
}
internal sealed class AdvisorErrorConnection
{
    internal string Id { get; }
    internal string Name { get; }
    internal string ProviderId { get; }
    internal string ProviderName { get; }
    internal string Model { get; }
    internal AdvisorErrorConnection(AdvisorService service, AdvisorApiConfiguration settings)
    { Id = settings.Id; Name = settings.Name; ProviderId = service.Id; ProviderName = service.Name; Model = service.Model(settings); }
}
// Observation does not replace the original exception or dictate an adapter's consultation workflow.
internal sealed class AdvisorErrorBackend : IAdvisorBackend
{
    private readonly IAdvisorBackend inner;
    private readonly AiHttpTransport transport;
    internal AdvisorErrorBackend(IAdvisorBackend inner, AiHttpTransport transport) { this.inner = inner; this.transport = transport; }
    public async Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation)
    {
        try { return await inner.ConsultAsync(context, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { transport.CaptureFailure(error); throw; }
    }
}
