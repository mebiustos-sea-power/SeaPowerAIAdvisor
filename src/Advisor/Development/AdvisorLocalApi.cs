using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorLocalTarget
{
    JObject LocalStatus();
    JObject LocalPreviewNotice();
    AdvisorLocalRun LocalConsult(string? question, string? shortcut, bool allowFallback, Func<AdvisorSituationDelivery?, AdvisorBackendSetup> prepare, bool plainAdvisor, bool? allowOperations, AdvisorSituationDelivery? situationDelivery);
    void LocalClear();
    void LocalCancel();
}

// HTTP workers enqueue detached commands; only Pump on the game thread may call targets, change prompts or change native reservations.
internal sealed class AdvisorLocalApi
{
    private sealed class Command
    {
        internal readonly string Method, Path;
        internal readonly JObject Body;
        internal readonly DateTime Deadline = DateTime.UtcNow.AddSeconds(15);
        internal readonly TaskCompletionSource<LocalApiResponse> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Command(string method, string path, JObject body) { Method = method; Path = path; Body = (JObject)body.DeepClone(); }
    }
    private readonly ConcurrentQueue<Command> commands = new();
    private int queued;
    private readonly object gate = new();
    private volatile bool enabled;
    private readonly Func<AdvisorPromptOverrides> prompts;
    private readonly Action<Exception> unexpected;
    private readonly Func<JObject> connections;
    private readonly Func<string, bool, string?, AdvisorSituationDelivery?, string?, AdvisorBackendSetup> prepareConnection;
    private object? owner;
    private string mode = "";
    private Func<IAdvisorLocalTarget>? target;
    private AdvisorLocalRun? last;
    private AdvisorOperations? operations;
    private AdvisorUnitReservations? reservations;
    internal AdvisorLocalApi(Func<AdvisorPromptOverrides> prompts, Action<Exception> unexpected,
        Func<JObject> connections, Func<string, bool, string?, AdvisorSituationDelivery?, string?, AdvisorBackendSetup> prepareConnection)
    { this.prompts = prompts; this.unexpected = unexpected; this.connections = connections; this.prepareConnection = prepareConnection; }
    // UI guards still record and stop a broken adviser; API callers must also receive the failure.
    internal static void InvokeGuarded(Action<Action> guard, Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var invoked = false;
        guard(() =>
        {
            invoked = true;
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); throw; }
        });
        failure?.Throw();
        if (!invoked) throw new LocalApiException(409, "advisor_unavailable");
    }
    internal JObject ConnectionInfo() => connections();
    internal void Attach(object owner, string mode, Func<IAdvisorLocalTarget> target, AdvisorOperations? operations = null, AdvisorUnitReservations? reservations = null)
    { this.owner = owner; this.mode = mode; this.target = target; this.operations = operations; this.reservations = reservations; }
    internal void Detach(object owner)
    { if (ReferenceEquals(this.owner, owner)) { this.owner = null; target = null; operations = null; reservations = null; mode = ""; } }
    internal void Enable() { lock (gate) enabled = true; }
    internal void Disable()
    {
        lock (gate)
        {
            enabled = false;
            while (commands.TryDequeue(out var command))
            { Interlocked.Decrement(ref queued); command.Completion.TrySetResult(LocalApiResponse.Error(503, "local_api_disabled")); }
            last = null;
        }
    }
    internal async Task<LocalApiResponse> Enqueue(string method, string path, JObject body)
    {
        Command command;
        lock (gate)
        {
            if (!enabled) return LocalApiResponse.Error(503, "local_api_disabled");
            if (Interlocked.Increment(ref queued) > 16)
            { Interlocked.Decrement(ref queued); return LocalApiResponse.Error(429, "local_queue_full"); }
            command = new Command(method, path, body);
            commands.Enqueue(command);
        }
        var winner = await Task.WhenAny(command.Completion.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        return winner == command.Completion.Task ? await command.Completion.Task.ConfigureAwait(false)
            : LocalApiResponse.Error(503, "game_thread_unavailable");
    }
    internal void Pump()
    {
        for (var i = 0; i < 16 && commands.TryDequeue(out var command); i++)
        {
            Interlocked.Decrement(ref queued);
            LocalApiResponse result;
            try
            {
                result = !enabled ? LocalApiResponse.Error(503, "local_api_disabled")
                    : DateTime.UtcNow >= command.Deadline ? LocalApiResponse.Error(503, "game_thread_unavailable")
                    : Dispatch(command.Method, command.Path, command.Body);
            }
            catch (AdvisorOperationException error) { result = LocalApiResponse.Error(error.Status, error.Code); }
            catch (LocalApiException error) { result = LocalApiResponse.Error(error.Status, error.Code); }
            catch (AiConnectionException error) { result = LocalApiResponse.Error(400, error.Code); }
            catch (AdvisorOptionsException error) { result = LocalApiResponse.Error(400, error.Code); }
            catch (AdvisorPromptException error) { result = LocalApiResponse.Error(400, error.Code); }
            catch (Exception error) { result = LocalApiResponse.Error(500, "unexpected_error"); unexpected(error); }
            command.Completion.TrySetResult(result);
        }
    }
    private IAdvisorLocalTarget Target() => target?.Invoke() ?? throw new LocalApiException(409, "no_active_game");
    private LocalApiResponse Dispatch(string method, string path, JObject body)
    {
        if ((method == "PATCH" && path == "/v1/prompts" || method == "POST" && path == "/v1/prompts/reset") &&
            (bool?)target?.Invoke().LocalStatus()["settings_open"] == true)
            throw new LocalApiException(409, "settings_open");
        if (path.StartsWith("/v1/units/", StringComparison.Ordinal))
        {
            var parts = path.Substring("/v1/units/".Length).Split('/');
            var read = method == "GET" && parts.Length == 2 && parts[1] == "reservations";
            var clear = method == "POST" && parts.Length == 3 && parts[1] == "reservations" && parts[2] == "clear";
            if (read || clear)
            {
                if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var unitId) || unitId <= 0)
                    throw new LocalApiException(400, "invalid_unit_id");
                var service = reservations ?? throw new LocalApiException(409, "reservations_unavailable");
                if (read) { Empty(body); return new(200, service.Read(unitId)); }
                if (body.Count != 1 || body["scope_id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)body["scope_id"]))
                    throw new LocalApiException(400, "invalid_reservation_request");
                var status = Target().LocalStatus();
                if ((bool?)status["proposal_review_open"] == true) throw new LocalApiException(409, "proposal_review_open");
                if ((bool?)status["busy"] == true) throw new LocalApiException(409, "consultation_busy");
                if ((bool?)status["settings_open"] == true) throw new LocalApiException(409, "settings_open");
                return new(200, service.Clear(unitId, (string)body["scope_id"]!));
            }
        }
        if (method == "GET" && path == "/v1/operations") return new(200, Operations().Catalog());
        if (method == "GET" && path.StartsWith("/v1/operations/", StringComparison.Ordinal) && path.EndsWith("/candidates", StringComparison.Ordinal) && path.Length > "/v1/operations/".Length + "/candidates".Length)
        {
            var id = path.Substring("/v1/operations/".Length, path.Length - "/v1/operations/".Length - "/candidates".Length);
            return new(200, Operations().Candidates(id));
        }
        if (method == "POST" && path == "/v1/operation-plans")
        {
            var status = Target().LocalStatus();
            if ((bool?)status["proposal_review_open"] == true) throw new LocalApiException(409, "proposal_review_open");
            if ((bool?)status["busy"] == true) throw new LocalApiException(409, "consultation_busy");
            if ((bool?)status["settings_open"] == true) throw new LocalApiException(409, "settings_open");
            var result = Operations().SubmitAndApprove(body);
            return new((bool?)result["registered"] == true ? 200 : 409, result);
        }
        if (method == "GET" && path == "/v1/connections") return new(200, ConnectionInfo());
        if (method == "GET" && path == "/v1/status")
            return new(200, new JObject { ["enabled"] = true, ["mode"] = mode, ["advisor"] = target?.Invoke().LocalStatus(), ["last_consultation_id"] = last?.Id });
        if (method == "GET" && path.StartsWith("/v1/consultations/", StringComparison.Ordinal))
            return last != null && path == "/v1/consultations/" + last.Id ? new(200, last.Snapshot()) : LocalApiResponse.Error(404, "consultation_not_retained");
        if (method == "POST" && path == "/v1/notices/preview") { Empty(body); return new(200, Target().LocalPreviewNotice()); }
        if (method == "GET" && path == "/v1/prompts") return PromptState();
        if (method == "PATCH" && path == "/v1/prompts")
        {
            if (body.Count != 1 || body["values"] is not JObject changes || changes.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new LocalApiException(400, "invalid_prompt_changes");
            var state = prompts(); var values = state.Current.Fields.ToDictionary(f => f.Id, f => f.Value);
            foreach (var change in changes.Properties())
            {
                if (!values.ContainsKey(change.Name)) throw new LocalApiException(400, "unknown_prompt_id");
                values[change.Name] = (string)change.Value!;
            }
            state.Apply(values); return PromptState();
        }
        if (method == "POST" && path == "/v1/prompts/reset") { Empty(body); prompts().Reset(); return PromptState(); }
        if (method == "POST" && path == "/v1/conversation/clear") { Empty(body); Target().LocalClear(); return new(200, new JObject { ["cleared"] = true }); }
        if (method == "POST" && path == "/v1/consultations/cancel") { Empty(body); Target().LocalCancel(); return new(200, new JObject { ["cancelled"] = true }); }
        if (method == "POST" && path == "/v1/consultations")
        {
            var question = body["question"]; var shortcut = body["shortcut"]; var fallback = body["allow_fallback"];
            if (body.Properties().Any(p => p.Name != "question" && p.Name != "shortcut" && p.Name != "allow_fallback" && p.Name != "connection_id" && p.Name != "plain_advisor" && p.Name != "model" && p.Name != "situation_delivery" && p.Name != "allow_operations" && p.Name != "connection_prompt") ||
                (question == null) == (shortcut == null) || (question != null && question.Type != JTokenType.String) ||
                (shortcut != null && shortcut.Type != JTokenType.String))
                throw new LocalApiException(400, "specify_question_or_shortcut");
            if (fallback != null && fallback.Type != JTokenType.Boolean) throw new LocalApiException(400, "invalid_fallback_policy");
            if (body["connection_id"] != null && body["connection_id"]!.Type != JTokenType.String)
                throw new LocalApiException(400, "invalid_connection_id");
            var plain = body["plain_advisor"];
            if (plain != null && plain.Type != JTokenType.Boolean)
                throw new LocalApiException(400, "invalid_plain_advisor");
            var operationPermission = body["allow_operations"];
            if (operationPermission != null && operationPermission.Type != JTokenType.Boolean)
                throw new LocalApiException(400, "invalid_operation_policy");
            var connectionPrompt = body["connection_prompt"];
            if (connectionPrompt != null && connectionPrompt.Type != JTokenType.String)
                throw new LocalApiException(400, "invalid_connection_prompt");
            var model = body["model"];
            if (model != null && model.Type != JTokenType.String)
                throw new LocalApiException(400, "invalid_model");
            AdvisorSituationDelivery? delivery = null;
            if (body["situation_delivery"] is JToken suppliedDelivery)
            {
                if (suppliedDelivery.Type != JTokenType.String) throw new LocalApiException(400, "invalid_situation_delivery");
                delivery = (string?)suppliedDelivery switch
                {
                    "on_demand" => AdvisorSituationDelivery.OnDemand, "upfront" => AdvisorSituationDelivery.Upfront, "on_request" => AdvisorSituationDelivery.OnRequest,
                    _ => throw new LocalApiException(400, "invalid_situation_delivery")
                };
            }
            var info = ConnectionInfo();
            var id = (string?)body["connection_id"] ?? (string?)info["default_connection_id"];
            if (id == null) throw new LocalApiException(409, "default_connection_missing");
            if (!((JArray)info["connections"]!).Any(c => (string?)c["id"] == id))
                throw new LocalApiException(403, "connection_not_allowed");
            last = Target().LocalConsult((string?)question, (string?)shortcut, (bool?)fallback ?? false,
                chosen => prepareConnection(id, (bool?)fallback ?? false, (string?)model, chosen, (string?)connectionPrompt),
                (bool?)plain ?? false, (bool?)operationPermission, delivery);
            return new(202, new JObject { ["connection_id"] = id, ["connection_name"] = last.ConnectionName, ["model"] = last.Model,
                ["situation_delivery"] = last.SituationDelivery?.Code(),
                ["allow_fallback"] = last.AllowFallback, ["plain_advisor"] = last.PlainAdvisor, ["allow_operations"] = last.AllowOperations, ["id"] = last.Id, ["status_url"] = "/v1/consultations/" + last.Id });
        }
        return LocalApiResponse.Error(404, "unknown_endpoint");
    }
    private AdvisorOperations Operations() => operations ?? throw new LocalApiException(409, "operations_unavailable");
    private LocalApiResponse PromptState()
    {
        var state = prompts();
        return new(200, new JObject { ["has_overrides"] = state.HasOverrides, ["fields"] = new JArray(state.Current.Fields.Select(f => new JObject { ["file"] = f.FileName, ["id"] = f.Id, ["value"] = f.Value })) });
    }
    private static void Empty(JObject value) { if (value.Count != 0) throw new LocalApiException(400, "body_must_be_empty"); }
}
