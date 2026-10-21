using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Internal trial contract. Game-specific implementations own applicability, validation and order creation.
internal interface IAdvisorOperation
{
    string Id { get; }
    string Description { get; }
    JObject Definition();
    JArray Candidates();
    JObject Execute(string target, JObject arguments);
}

internal sealed class AdvisorOperationException : Exception
{
    internal int Status { get; }
    internal string Code { get; }
    internal AdvisorOperationException(int status, string code) : base(code) { Status = status; Code = code; }
}

// Lives for one loaded battle. Never evict request receipts and then risk replaying their side effects.
internal sealed class AdvisorOperations
{
    private sealed class Receipt
    {
        internal readonly JObject Request;
        internal JObject? Result;
        internal Receipt(JObject request) => Request = (JObject)request.DeepClone();
    }
    private const int MaximumReceipts = 1024;
    private readonly Dictionary<string, IAdvisorOperation> operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> receipts = new(StringComparer.Ordinal);
    private readonly Func<bool> enabled;
    private readonly int thread = System.Environment.CurrentManagedThreadId;
    internal string ScopeId { get; } = Guid.NewGuid().ToString("N");
    internal AdvisorOperations(Func<bool> enabled) => this.enabled = enabled;

    internal bool Enabled { get { CheckThread(); return enabled(); } }

    internal void Register(IAdvisorOperation operation)
    {
        CheckThread();
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (!Identifier(operation.Id) || operations.ContainsKey(operation.Id))
            throw new InvalidOperationException("Invalid or duplicate operation ID: " + operation.Id);
        operations.Add(operation.Id, operation);
    }
    internal JObject Catalog()
    {
        CheckThread();
        return new JObject { ["scope_id"] = ScopeId, ["enabled"] = enabled(),
            ["operations"] = enabled() ? new JArray(operations.Values.Select(o => o.Definition().DeepClone())) : new JArray() };
    }
    internal JArray PriorOrders(string owner)
    {
        CheckThread();
        var prefix = owner + ".";
        return new JArray(receipts.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => new JObject
        {
            ["request"] = new JObject { ["action"] = "execute", ["operation_id"] = p.Value.Request["operation"]!.DeepClone(),
                ["target_ref"] = p.Value.Request["target_ref"]!.DeepClone(), ["request_id"] = p.Key.Substring(prefix.Length),
                ["arguments"] = p.Value.Request["arguments"]!.DeepClone() },
            ["response"] = p.Value.Result?.DeepClone() ?? new JObject { ["status"] = "unknown", ["code"] = "operation_outcome_unknown" }
        }));
    }
    internal JObject Discover(string search, int offset, int limit)
    {
        CheckEnabled();
        var matches = operations.Values.OrderBy(o => o.Id, StringComparer.Ordinal)
            .Where(o => search.Length == 0 || o.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                o.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        return Page(new JArray(matches.Select(o => new JObject { ["id"] = o.Id, ["description"] = o.Description })), offset, limit, "operations");
    }
    internal JObject Describe(string id)
    { CheckEnabled(); return new JObject { ["operation"] = Operation(id).Definition().DeepClone() }; }
    internal JObject CandidatePage(string id, string? target, int offset, int limit)
    {
        CheckEnabled();
        var rows = Operation(id).Candidates();
        if (target != null) rows = new JArray(rows.Where(c => (string?)c["target_ref"] == target));
        return Page(rows, offset, limit, "candidates");
    }
    private static JObject Page(JArray rows, int offset, int limit, string field) => new()
    {
        [field] = new JArray(rows.Skip(offset).Take(limit).Select(r => r.DeepClone())),
        ["total_count"] = rows.Count, ["offset"] = offset, ["limit"] = limit,
        ["has_more"] = (long)offset + limit < rows.Count, ["captured_at_utc"] = DateTime.UtcNow.ToString("O")
    };
    internal JObject Candidates(string id)
    {
        CheckEnabled();
        return new JObject { ["scope_id"] = ScopeId, ["operation"] = id, ["candidates"] = Operation(id).Candidates().DeepClone() };
    }
    internal JObject Execute(JObject request)
    {
        CheckEnabled();
        if (request.Properties().Any(p => p.Name is not ("scope_id" or "request_id" or "operation" or "target_ref" or "arguments")) ||
            request.Count != 5 || request["arguments"] is not JObject arguments)
            throw new AdvisorOperationException(400, "invalid_operation_request");
        var scope = Text(request, "scope_id");
        var id = Text(request, "request_id");
        var operationId = Text(request, "operation");
        var target = Text(request, "target_ref");
        if (!Identifier(id)) throw new AdvisorOperationException(400, "invalid_request_id");
        if (scope != ScopeId) throw new AdvisorOperationException(409, "operation_scope_changed");
        if (receipts.TryGetValue(id, out var previous))
        {
            if (!JToken.DeepEquals(previous.Request, request)) throw new AdvisorOperationException(409, "request_id_conflict");
            return previous.Result == null ? throw new AdvisorOperationException(409, "operation_outcome_unknown") : (JObject)previous.Result.DeepClone();
        }
        var operation = Operation(operationId);
        if (receipts.Count >= MaximumReceipts) throw new AdvisorOperationException(409, "operation_receipt_limit");
        var receipt = new Receipt(request);
        receipts.Add(id, receipt);
        try
        {
            var data = operation.Execute(target, (JObject)arguments.DeepClone());
            receipt.Result = new JObject { ["request_id"] = id, ["status"] = "registered", ["data"] = data.DeepClone() };
        }
        catch (AdvisorOperationException error)
        {
            receipt.Result = new JObject { ["request_id"] = id, ["status"] = "rejected", ["code"] = error.Code };
        }
        // Unexpected errors propagate; the unresolved receipt prevents a blind retry from registering twice.
        return (JObject)receipt.Result.DeepClone();
    }
    private IAdvisorOperation Operation(string id) => operations.TryGetValue(id, out var operation) ? operation
        : throw new AdvisorOperationException(404, "unknown_operation");
    private void CheckEnabled()
    {
        CheckThread();
        if (!enabled()) throw new AdvisorOperationException(403, "unit_operations_disabled");
    }
    private void CheckThread()
    {
        if (thread != System.Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Operations must run on their owning game thread.");
    }
    private static string Text(JObject value, string key) => value[key]?.Type == JTokenType.String &&
        !string.IsNullOrWhiteSpace((string?)value[key]) && ((string)value[key]!).Length <= 256 ? (string)value[key]!
        : throw new AdvisorOperationException(400, "invalid_operation_request");
    private static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
}
