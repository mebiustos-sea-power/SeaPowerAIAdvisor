using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Only local-API consultations retain this diagnostic copy. No provider credentials or HTTP headers enter it.
internal sealed class AdvisorLocalRun
{
    private readonly object gate = new();
    private readonly JArray retrievals = new();
    private readonly JArray operations = new();
    private JObject? input;
    private AdvisorTurn? turn;
    private AdviceResult? result;
    private string connection = "";
    private JObject? finished;
    internal bool AllowFallback { get; }
    internal bool PlainAdvisor { get; }
    internal bool AllowOperations { get; }
    internal string ConnectionName { get; }
    internal string Model { get; }
    internal AdvisorSituationDelivery? SituationDelivery { get; }
    internal AdvisorLocalRun(bool allowFallback = false, bool plainAdvisor = false, bool allowOperations = true, AdvisorBackendSetup? execution = null)
    {
        AllowFallback = allowFallback; PlainAdvisor = plainAdvisor; AllowOperations = allowOperations;
        ConnectionName = execution?.ConnectionName ?? ""; Model = execution?.Model ?? ""; SituationDelivery = execution?.SituationDelivery;
    }
    internal AdvisorProfile? ProfileFor(AdvisorProfile? selected) => PlainAdvisor
        ? new AdvisorProfile(Array.Empty<AdvisorPersona>(), null, true, "") : selected;
    internal string CommonPromptFor(string configured) => PlainAdvisor ? "" : configured;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly string started = DateTime.UtcNow.ToString("O");

    internal void Attach(AdvisorTurn value) => turn = value;
    internal bool Attached => turn != null || finished != null;
    internal void Complete(AdviceResult value, string connectionName) { result = value; connection = connectionName; }
    internal void CaptureInput(AiProviderInput value)
    {
        lock (gate) if (finished == null) input = new JObject { ["data"] = value.Data.DeepClone(), ["system_instruction"] = value.SystemInstruction, ["tools"] = new JArray(value.Tools.Select(t => t.DeepClone())) };
    }
    internal IAdvisorOperationSession Observe(IAdvisorOperationSession session) => new RecordingOperations(session, this);
    internal IAdvisorQuerySession Observe(IAdvisorQuerySession session) => new RecordingSession(session, this);
    internal void Finish()
    {
        lock (gate)
        {
            finished ??= Snapshot(); turn = null;
            input = null; retrievals.Clear(); operations.Clear();
        }
    }
    internal JObject Snapshot()
    {
        lock (gate) if (finished != null) return (JObject)finished.DeepClone();
        var progress = turn?.Progress;
        lock (gate) return new JObject
        {
            ["id"] = Id, ["started_utc"] = started, ["allow_fallback"] = AllowFallback, ["plain_advisor"] = PlainAdvisor, ["allow_operations"] = AllowOperations,
            ["state"] = turn?.State.ToString().ToLowerInvariant() ?? "starting",
            ["situation_delivery"] = SituationDelivery?.Code(), ["situation_delivery_override"] = turn?.Request.SituationDelivery?.Code(),
            ["question"] = turn?.Request.Text, ["display_question"] = turn?.Request.DisplayText,
            ["advisor_name"] = turn?.AdvisorName, ["answer"] = turn?.Exchange?.Answer,
            ["error"] = turn?.Error == null ? null : new JObject { ["code"] = turn.Error.Code, ["http_status"] = turn.Error.HttpStatus },
            ["progress"] = progress == null ? null : new JObject
            {
                ["stage"] = progress.Stage.ToString(), ["api_request"] = progress.ApiRequest,
                ["rounds"] = progress.Rounds, ["max_rounds"] = progress.MaxRounds,
                ["queries"] = new JArray(progress.Queries), ["using_fallback"] = progress.UsingFallback,
                ["connection_name"] = progress.ConnectionName, ["model"] = progress.Model
            },
            ["result"] = result == null ? null : new JObject
            {
                ["connection_name"] = connection, ["model"] = result.Model, ["completion_reason"] = result.CompletionCode,
                ["api_requests"] = result.ApiRequests, ["query_rounds"] = result.QueryRounds,
                ["reported_total_tokens"] = result.TotalTokens, ["queries"] = new JArray(result.Queries)
            },
            ["input"] = input?.DeepClone(), ["retrievals"] = retrievals.DeepClone(), ["operations"] = operations.DeepClone()
        };
    }

    private sealed class RecordingOperations : IAdvisorOperationSession
    {
        private readonly IAdvisorOperationSession inner;
        private readonly AdvisorLocalRun run;
        internal RecordingOperations(IAdvisorOperationSession inner, AdvisorLocalRun run) { this.inner = inner; this.run = run; }
        public JObject Tool => inner.Tool;
        public JArray History => inner.History;
        public async Task<JObject> ExecuteAsync(JObject request, CancellationToken cancellation)
        {
            var entry = new JObject { ["time_utc"] = DateTime.UtcNow.ToString("O"), ["request"] = request.DeepClone() };
            try
            {
                var response = await inner.ExecuteAsync(request, cancellation).ConfigureAwait(false);
                entry["response"] = response.DeepClone(); return response;
            }
            catch (OperationCanceledException) { entry["error_code"] = "cancelled"; throw; }
            catch { entry["error_code"] = "unexpected_error"; throw; }
            finally { lock (run.gate) if (run.finished == null) run.operations.Add(entry); }
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingSession : IAdvisorSnapshotSource, IAdvisorSnapshotReadObserver, IAdvisorQuerySession
    {
        private readonly IAdvisorQuerySession inner;
        private readonly AdvisorLocalRun run;
        internal RecordingSession(IAdvisorQuerySession inner, AdvisorLocalRun run) { this.inner = inner; this.run = run; }
        public string Id => inner.Id;
        public int RoundsUsed => inner.RoundsUsed;
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        public JObject InitialInput() => inner.InitialInput();
        public JArray ExportSituation() => AdvisorSnapshotExport.From(inner);
        public void RecordSnapshotRead(JObject request, JObject response)
        {
            if (inner is IAdvisorSnapshotReadObserver observer) observer.RecordSnapshotRead(request, response);
            var entry = new JObject { ["time_utc"] = DateTime.UtcNow.ToString("O"), ["request"] = request.DeepClone(), ["response"] = response.DeepClone() };
            lock (run.gate) if (run.finished == null) run.retrievals.Add(entry);
        }
        public JObject Execute(JObject request)
        {
            var entry = new JObject { ["time_utc"] = DateTime.UtcNow.ToString("O"), ["request"] = request.DeepClone() };
            try
            {
                var response = inner.Execute(request);
                entry["response"] = response.DeepClone();
                return response;
            }
            catch (AdvisorQueryException error) { entry["error_code"] = error.Code; throw; }
            catch { entry["error_code"] = "unexpected_error"; throw; }
            finally { lock (run.gate) if (run.finished == null) run.retrievals.Add(entry); }
        }
    }
}
