using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorOperationSession : IDisposable
{
    JObject Tool { get; }
    JArray History { get; }
    Task<JObject> ExecuteAsync(JObject request, CancellationToken cancellation);
}

// One loaded battle owns this bridge. Provider workers enqueue detached data; Pump alone touches the game.
internal sealed class AdvisorOperationGateway : IDisposable
{
    internal const string ToolName = "game_operations";
    private static readonly AdvisorRequestShape[] requestShapes = {
        new(new[] { "list" }, Array.Empty<string>(), "search", "offset", "limit"),
        new(new[] { "describe" }, new[] { "operation_id" }),
        new(new[] { "candidates" }, new[] { "operation_id" }, "target_ref", "offset", "limit"),
        new(new[] { "execute" }, new[] { "operation_id", "target_ref", "request_id", "arguments" }),
        new(new[] { "submit" }, new[] { "request_id", "plans" })
    };
    internal static JObject ActionSchema(JObject tool, string action) => requestShapes.Single(s => s.Matches(action)).Schema("action",
        (JObject)tool["parameters"]!["properties"]!, new[] { action });
    internal static JObject ReadSchema(JObject tool) => AdvisorRequestShape.Union("action", requestShapes,
        (JObject)tool["parameters"]!["properties"]!, new[] { "list", "describe", "candidates" });
    internal static JObject ExecutionSchema(JObject tool) => ActionSchema(tool, "submit");
    // Standard adapters expose proposals separately; independent clients retain the complete service.
    internal static Task<JObject> ProposeAsync(IAdvisorOperationSession session, JObject request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request["action"]?.Type != JTokenType.String || (string?)request["action"] is not ("execute" or "submit"))
            return Task.FromResult(Error("invalid_operation_request"));
        return session.ExecuteAsync(request, cancellation);
    }
    private sealed class Command
    {
        internal readonly string Owner;
        internal readonly JObject Request;
        internal readonly AdvisorOperationReferences References;
        internal readonly CancellationToken Cancellation;
        internal readonly DateTime Deadline = DateTime.UtcNow.AddSeconds(15);
        internal readonly TaskCompletionSource<JObject> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Command(string owner, JObject request, AdvisorOperationReferences references, CancellationToken cancellation)
        { Owner = owner; Request = (JObject)request.DeepClone(); References = references; Cancellation = cancellation; }
    }
    private readonly AdvisorOperations operations;
    private readonly ConcurrentQueue<Command> commands = new();
    private readonly object gate = new();
    private bool disposed;
    private int queued;
    internal AdvisorOperations Operations => operations;
    internal AdvisorOperationGateway(AdvisorOperations operations) => this.operations = operations;
    internal IAdvisorOperationSession? CreateSession(string owner, AdvisorPrompts prompts, AdvisorOperationReferences? references = null)
    {
        if (!operations.Enabled) return null;
        operations.BeginProposal(owner);
        return new Session(this, owner, prompts, operations.PriorOrders(owner), references ?? AdvisorOperationReferences.Empty);
    }

    internal void Pump()
    {
        while (commands.TryDequeue(out var command))
        {
            Interlocked.Decrement(ref queued);
            if (command.Cancellation.IsCancellationRequested) { command.Completion.TrySetCanceled(); continue; }
            if (DateTime.UtcNow >= command.Deadline) { command.Completion.TrySetResult(Error("game_thread_unavailable")); continue; }
            var resolutions = new JObject();
            JObject result;
            try { result = Dispatch(command.Owner, command.Request, command.References, resolutions); }
            catch (AdvisorOperationException error) { result = Error(error.Code); }
            catch (Exception error) { command.Completion.TrySetException(error); throw; }
            foreach (var field in resolutions.Properties())
                if (field.Value is not JArray array || array.Count > 0) result[field.Name] = field.Value.DeepClone();
            command.Completion.TrySetResult(result);
        }
    }
    private async Task<JObject> Enqueue(string owner, JObject request, AdvisorOperationReferences references, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Command command;
        lock (gate)
        {
            if (disposed) return Error("operation_scope_changed");
            if (Interlocked.Increment(ref queued) > 16)
            { Interlocked.Decrement(ref queued); return Error("operation_queue_full"); }
            command = new Command(owner, request, references, cancellation); commands.Enqueue(command);
        }
        var deadline = Task.Delay(TimeSpan.FromSeconds(15), cancellation);
        await Task.WhenAny(command.Completion.Task, deadline).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return command.Completion.Task.IsCompleted ? await command.Completion.Task.ConfigureAwait(false) : Error("game_thread_unavailable");
    }
    private JObject Dispatch(string owner, JObject request, AdvisorOperationReferences references, JObject resolutions)
    {
        if (!operations.Enabled) throw new AdvisorOperationException(403, "unit_operations_disabled");
        var action = Text(request, "action");
        if (requestShapes.Any(s => s.Matches(action)) && !requestShapes.Any(s => s.Matches(action) && s.AllowsFields(request, "action") && s.HasRequired(request)))
            throw new AdvisorOperationException(400, "invalid_operation_request");
        if ((action is "candidates" or "execute") && request["target_ref"] != null)
            request = NormalizeTarget(request, references, resolutions);
        switch (action)
        {
            case "list":
                var search = request["search"] == null ? "" : request["search"]!.Type == JTokenType.String && ((string)request["search"]!).Length <= 200
                    ? ((string)request["search"]!).Trim() : throw new AdvisorOperationException(400, "invalid_operation_request");
                return operations.Discover(search, Number(request, "offset", 0, int.MaxValue), Number(request, "limit", 10, 20, 1));
            case "describe":
                return operations.Describe(Text(request, "operation_id"));
            case "candidates":
                return operations.CandidatePage(Text(request, "operation_id"), request["target_ref"] == null ? null : Text(request, "target_ref"),
                    Number(request, "offset", 0, int.MaxValue), Number(request, "limit", 10, 20, 1));
            case "submit":
                var submission = (JObject)request.DeepClone(); submission.Remove("action");
                AdvisorOperationPlanBatch.Validate(submission);
                var fixes = new JArray(); resolutions["plan_resolutions"] = fixes;
                foreach (var plan in ((JArray)submission["plans"]!).Cast<JObject>())
                    for (var i = 0; i < ((JArray)plan["operations"]!).Count; i++)
                    {
                        var order = (JObject)plan["operations"]![i]!; var correction = new JObject();
                        var normalized = NormalizeTarget(order, references, correction);
                        var argumentsFixed = new JArray();
                        normalized["arguments"] = operations.NormalizeArguments(Text(normalized, "operation_id"), Text(normalized, "target_ref"),
                            (JObject)normalized["arguments"]!, references, argumentsFixed);
                        if (argumentsFixed.Count > 0) correction["argument_resolutions"] = argumentsFixed;
                        if (correction.Count > 0)
                        { correction["plan_id"] = plan["plan_id"]!.DeepClone(); correction["operation_index"] = i; fixes.Add(correction); }
                        plan["operations"]![i] = normalized;
                    }
                return operations.SubmitPlans(owner, submission);
            case "execute":
                var id = Text(request, "request_id");
                if (id.Length > 40 || request["arguments"] is not JObject args) throw new AdvisorOperationException(400, "invalid_operation_request");
                var operationId = Text(request, "operation_id");
                var targetRef = Text(request, "target_ref");
                var argumentResolutions = new JArray();
                resolutions["argument_resolutions"] = argumentResolutions;
                var arguments = operations.NormalizeArguments(operationId, targetRef, args, references, argumentResolutions);
                // The host binds battle and owner identity. A repeated order ID reaches the same receipt.
                return operations.Propose(owner, new JObject { ["scope_id"] = operations.ScopeId, ["request_id"] = owner + "." + id,
                    ["operation"] = operationId, ["target_ref"] = targetRef, ["arguments"] = arguments });
            default: throw new AdvisorOperationException(400, "unknown_operation_action");
        }
    }
    private static JObject NormalizeTarget(JObject request, AdvisorOperationReferences references, JObject resolutions)
    {
        var target = Text(request, "target_ref");
        var alias = target.StartsWith("unit:", StringComparison.Ordinal) ? target.Substring(5) : target;
        var normalized = (JObject)request.DeepClone();
        if (alias.StartsWith("own_unit_", StringComparison.Ordinal))
        {
            var id = references.Units.Resolve(alias) ?? throw new AdvisorOperationException(400, "unknown_unit_reference");
            var canonical = "unit:" + id.ToString(CultureInfo.InvariantCulture);
            resolutions["target_ref_resolution"] = new JObject { ["requested"] = target, ["resolved"] = canonical, ["reason"] = "consultation_unit_reference" };
            normalized["target_ref"] = canonical;
        }
        return normalized;
    }
    private static string Text(JObject request, string key) => request[key]?.Type == JTokenType.String &&
        !string.IsNullOrWhiteSpace((string?)request[key]) && ((string)request[key]!).Length <= 200
        ? (string)request[key]! : throw new AdvisorOperationException(400, "invalid_operation_request");
    private static int Number(JObject request, string key, int fallback, int maximum, int minimum = 0)
    {
        if (request[key] == null) return fallback;
        if (request[key]!.Type != JTokenType.Integer || !int.TryParse(request[key]!.ToString(), out var value) || value < minimum || value > maximum)
            throw new AdvisorOperationException(400, "invalid_operation_pagination");
        return value;
    }
    private static JObject Error(string code) => new() { ["status"] = "error", ["error"] = new JObject { ["code"] = code } };
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { operations.Retire(); }
            finally
            {
                while (commands.TryDequeue(out var command))
                { Interlocked.Decrement(ref queued); command.Completion.TrySetResult(Error("operation_scope_changed")); }
            }
        }
    }

    internal static JObject ToolDefinition(AdvisorPrompts prompts)
    {
        var batch = AdvisorOperationPlanBatch.Schema(new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200,
            ["description"] = prompts.Text("battle.json", "operation_target_ref") }, prompts.Text("battle.json", "operation_request_id"));
        return new JObject
        {
            ["name"] = ToolName, ["description"] = prompts.Text("battle.json", "operation_tool_description"),
            ["parameters"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("action"),
                ["properties"] = new JObject
                {
                    ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("list", "describe", "candidates", "submit", "execute") },
                    ["search"] = new JObject { ["type"] = "string", ["description"] = prompts.Text("battle.json", "operation_search") },
                    ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20 },
                    ["operation_id"] = new JObject { ["type"] = "string" },
                    ["target_ref"] = new JObject { ["type"] = "string", ["description"] = prompts.Text("battle.json", "operation_target_ref") },
                    ["request_id"] = batch["properties"]!["request_id"]!.DeepClone(),
                    ["arguments"] = new JObject { ["type"] = "object" },
                    ["plans"] = batch["properties"]!["plans"]!.DeepClone()
                }
            }
        };
    }

    private sealed class Session : IAdvisorOperationSession
    {
        private readonly AdvisorOperationGateway gateway;
        private readonly string owner;
        private readonly AdvisorOperationReferences references;
        private readonly CancellationTokenSource lifetime = new();
        private readonly object gate = new();
        private readonly JArray history = new();
        private int calls, bytes;
        public JObject Tool { get; }
        public JArray History { get { lock (gate) return (JArray)history.DeepClone(); } }
        internal Session(AdvisorOperationGateway gateway, string owner, AdvisorPrompts prompts, JArray prior, AdvisorOperationReferences references)
        {
            this.gateway = gateway; this.owner = owner; this.references = references; history = (JArray)prior.DeepClone();
            Tool = ToolDefinition(prompts);
        }
        public async Task<JObject> ExecuteAsync(JObject request, CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (++calls > 64 || bytes >= AdvisorQueryLimits.MaxTotalBytes) return Error("operation_budget_exhausted");
            }
            if (AdvisorQueryLimits.Bytes(request) > AdvisorQueryLimits.MaxRequestBytes) return Error("request_too_large");
            var detached = (JObject)request.DeepClone();
            var result = await gateway.Enqueue(owner, detached, references, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (AdvisorQueryLimits.Bytes(result) > AdvisorQueryLimits.MaxResponseBytes) result = Error("operation_response_too_large");
            lock (gate)
            {
                bytes += AdvisorQueryLimits.Bytes(result);
                history.Add(new JObject { ["request"] = detached.DeepClone(), ["response"] = result.DeepClone() });
            }
            return result;
        }
        public void Dispose() { lifetime.Cancel(); }
    }
}
