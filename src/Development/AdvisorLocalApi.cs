using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorLocalTarget
{
    JObject LocalStatus();
    AdvisorLocalRun LocalConsult(string? question, string? shortcut, bool allowFallback, AdvisorBackendSetup execution, bool plainAdvisor);
    void LocalClear();
    void LocalCancel();
}

internal sealed class AdvisorLocalApiException : Exception
{
    internal int Status { get; }
    internal string Code { get; }
    internal AdvisorLocalApiException(int status, string code) : base(code) { Status = status; Code = code; }
}

internal sealed class AdvisorLocalResponse
{
    internal int Status { get; }
    internal JObject Data { get; }
    internal AdvisorLocalResponse(int status, JObject data) { Status = status; Data = data; }
    internal static AdvisorLocalResponse Error(int status, string code) => new(status, new JObject { ["error"] = code });
}

// HTTP workers enqueue detached commands; only Pump on the game thread may call targets, change prompts or register orders.
internal sealed class AdvisorLocalApi
{
    private sealed class Command
    {
        internal readonly string Method, Path;
        internal readonly JObject Body;
        internal readonly DateTime Deadline = DateTime.UtcNow.AddSeconds(15);
        internal readonly TaskCompletionSource<AdvisorLocalResponse> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Command(string method, string path, JObject body) { Method = method; Path = path; Body = (JObject)body.DeepClone(); }
    }
    private readonly ConcurrentQueue<Command> commands = new();
    private int queued;
    private readonly object gate = new();
    private volatile bool enabled;
    private readonly Func<AdvisorPromptOverrides> prompts;
    private readonly Action<Exception> unexpected;
    private readonly Func<JObject> connections;
    private readonly Func<string, bool, AdvisorBackendSetup> prepareConnection;
    private object? owner;
    private string mode = "";
    private Func<IAdvisorLocalTarget>? target;
    private AdvisorLocalRun? last;
    private AdvisorOperations? operations;
    internal AdvisorLocalApi(Func<AdvisorPromptOverrides> prompts, Action<Exception> unexpected,
        Func<JObject> connections, Func<string, bool, AdvisorBackendSetup> prepareConnection)
    { this.prompts = prompts; this.unexpected = unexpected; this.connections = connections; this.prepareConnection = prepareConnection; }
    internal JObject ConnectionInfo() => connections();
    internal void Attach(object owner, string mode, Func<IAdvisorLocalTarget> target, AdvisorOperations? operations = null)
    { this.owner = owner; this.mode = mode; this.target = target; this.operations = operations; }
    internal void Detach(object owner)
    { if (ReferenceEquals(this.owner, owner)) { this.owner = null; target = null; operations = null; mode = ""; } }
    internal void Enable() { lock (gate) enabled = true; }
    internal void Disable()
    {
        lock (gate)
        {
            enabled = false;
            while (commands.TryDequeue(out var command))
            { Interlocked.Decrement(ref queued); command.Completion.TrySetResult(AdvisorLocalResponse.Error(503, "local_api_disabled")); }
            last = null;
        }
    }
    internal async Task<AdvisorLocalResponse> Enqueue(string method, string path, JObject body)
    {
        Command command;
        lock (gate)
        {
            if (!enabled) return AdvisorLocalResponse.Error(503, "local_api_disabled");
            if (Interlocked.Increment(ref queued) > 16)
            { Interlocked.Decrement(ref queued); return AdvisorLocalResponse.Error(429, "local_queue_full"); }
            command = new Command(method, path, body);
            commands.Enqueue(command);
        }
        var winner = await Task.WhenAny(command.Completion.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        return winner == command.Completion.Task ? await command.Completion.Task.ConfigureAwait(false)
            : AdvisorLocalResponse.Error(503, "game_thread_unavailable");
    }
    internal void Pump()
    {
        for (var i = 0; i < 16 && commands.TryDequeue(out var command); i++)
        {
            Interlocked.Decrement(ref queued);
            AdvisorLocalResponse result;
            try
            {
                result = !enabled ? AdvisorLocalResponse.Error(503, "local_api_disabled")
                    : DateTime.UtcNow >= command.Deadline ? AdvisorLocalResponse.Error(503, "game_thread_unavailable")
                    : Dispatch(command.Method, command.Path, command.Body);
            }
            catch (AdvisorOperationException error) { result = AdvisorLocalResponse.Error(error.Status, error.Code); }
            catch (AdvisorLocalApiException error) { result = AdvisorLocalResponse.Error(error.Status, error.Code); }
            catch (AdvisorConnectionException error) { result = AdvisorLocalResponse.Error(400, error.Code); }
            catch (AdvisorOptionsException error) { result = AdvisorLocalResponse.Error(400, error.Code); }
            catch (AdvisorPromptException error) { result = AdvisorLocalResponse.Error(400, error.Code); }
            catch (Exception error) { result = AdvisorLocalResponse.Error(500, "unexpected_error"); unexpected(error); }
            command.Completion.TrySetResult(result);
        }
    }
    private IAdvisorLocalTarget Target() => target?.Invoke() ?? throw new AdvisorLocalApiException(409, "no_active_game");
    private AdvisorLocalResponse Dispatch(string method, string path, JObject body)
    {
        if ((method == "PATCH" && path == "/v1/prompts" || method == "POST" && path == "/v1/prompts/reset") &&
            (bool?)target?.Invoke().LocalStatus()["settings_open"] == true)
            throw new AdvisorLocalApiException(409, "settings_open");
        if (method == "GET" && path == "/v1/operations") return new(200, Operations().Catalog());
        if (method == "GET" && path.StartsWith("/v1/operations/", StringComparison.Ordinal) && path.EndsWith("/candidates", StringComparison.Ordinal) && path.Length > "/v1/operations/".Length + "/candidates".Length)
        {
            var id = path.Substring("/v1/operations/".Length, path.Length - "/v1/operations/".Length - "/candidates".Length);
            return new(200, Operations().Candidates(id));
        }
        if (method == "POST" && path == "/v1/operations")
        {
            var result = Operations().Execute(body);
            return new((string?)result["status"] == "registered" ? 200 : 409, result);
        }
        if (method == "GET" && path == "/v1/connections") return new(200, ConnectionInfo());
        if (method == "GET" && path == "/v1/status")
            return new(200, new JObject { ["enabled"] = true, ["mode"] = mode, ["advisor"] = target?.Invoke().LocalStatus(), ["last_consultation_id"] = last?.Id });
        if (method == "GET" && path.StartsWith("/v1/consultations/", StringComparison.Ordinal))
            return last != null && path == "/v1/consultations/" + last.Id ? new(200, last.Snapshot()) : AdvisorLocalResponse.Error(404, "consultation_not_retained");
        if (method == "GET" && path == "/v1/prompts") return PromptState();
        if (method == "PATCH" && path == "/v1/prompts")
        {
            if (body.Count != 1 || body["values"] is not JObject changes || changes.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new AdvisorLocalApiException(400, "invalid_prompt_changes");
            var state = prompts(); var values = state.Current.Fields.ToDictionary(f => f.Id, f => f.Value);
            foreach (var change in changes.Properties())
            {
                if (!values.ContainsKey(change.Name)) throw new AdvisorLocalApiException(400, "unknown_prompt_id");
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
            if (body.Properties().Any(p => p.Name != "question" && p.Name != "shortcut" && p.Name != "allow_fallback" && p.Name != "connection_id" && p.Name != "plain_advisor") ||
                (question == null) == (shortcut == null) || (question != null && question.Type != JTokenType.String) ||
                (shortcut != null && shortcut.Type != JTokenType.String))
                throw new AdvisorLocalApiException(400, "specify_question_or_shortcut");
            if (fallback != null && fallback.Type != JTokenType.Boolean) throw new AdvisorLocalApiException(400, "invalid_fallback_policy");
            if (body["connection_id"] != null && body["connection_id"]!.Type != JTokenType.String)
                throw new AdvisorLocalApiException(400, "invalid_connection_id");
            var plain = body["plain_advisor"];
            if (plain != null && plain.Type != JTokenType.Boolean)
                throw new AdvisorLocalApiException(400, "invalid_plain_advisor");
            var info = ConnectionInfo();
            var id = (string?)body["connection_id"] ?? (string?)info["default_connection_id"];
            if (id == null) throw new AdvisorLocalApiException(409, "default_connection_missing");
            if (!((JArray)info["connections"]!).Any(c => (string?)c["id"] == id))
                throw new AdvisorLocalApiException(403, "connection_not_allowed");
            var execution = prepareConnection(id, (bool?)fallback ?? false);
            last = Target().LocalConsult((string?)question, (string?)shortcut, (bool?)fallback ?? false, execution, (bool?)plain ?? false);
            return new(202, new JObject { ["connection_id"] = id, ["connection_name"] = execution.ConnectionName,
                ["allow_fallback"] = last.AllowFallback, ["plain_advisor"] = last.PlainAdvisor, ["id"] = last.Id, ["status_url"] = "/v1/consultations/" + last.Id });
        }
        return AdvisorLocalResponse.Error(404, "unknown_endpoint");
    }
    private AdvisorOperations Operations() => operations ?? throw new AdvisorLocalApiException(409, "operations_unavailable");
    private AdvisorLocalResponse PromptState()
    {
        var state = prompts();
        return new(200, new JObject { ["has_overrides"] = state.HasOverrides, ["fields"] = new JArray(state.Current.Fields.Select(f => new JObject { ["file"] = f.FileName, ["id"] = f.Id, ["value"] = f.Value })) });
    }
    private static void Empty(JObject value) { if (value.Count != 0) throw new AdvisorLocalApiException(400, "body_must_be_empty"); }
}
