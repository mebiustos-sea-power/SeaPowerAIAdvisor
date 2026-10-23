using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Internal command contract. Game-specific implementations own applicability, validation and order creation.
internal interface IAdvisorOperation
{
    string Id { get; }
    string Description { get; }
    AdvisorOperationText Documentation { get; }
    bool RequiresApproval { get; }
    void Validate(IReadOnlyList<AdvisorOperationOrder> orders);
    string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language);
    // Metadata is read without querying live candidates or changing game state.
    JObject Definition();
    JArray Candidates();
    JObject Execute(string target, JObject arguments);
}

// Optional: each command owns the meaning of its arguments; normalization precedes receipt comparison.
internal interface IAdvisorOperationArgumentNormalizer
{
    JObject NormalizeArguments(string target, JObject arguments, AdvisorOperationReferences references, JArray resolutions);
}

internal sealed class AdvisorOperationException : Exception
{
    internal int Status { get; }
    internal string Code { get; }
    internal AdvisorOperationException(int status, string code) : base(code) { Status = status; Code = code; }
}

// Lives for one loaded battle. Never evict request receipts and then risk replaying their side effects.
internal sealed partial class AdvisorOperations
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
    private readonly Dictionary<string, AdvisorOperationProposal> proposals = new(StringComparer.Ordinal);
    private readonly Func<bool> enabled;
    private bool retired;
    private readonly int thread = System.Environment.CurrentManagedThreadId;
    internal string ScopeId { get; } = Guid.NewGuid().ToString("N");
    internal AdvisorOperations(Func<bool> enabled) => this.enabled = enabled;

    internal bool Enabled { get { CheckThread(); return !retired && enabled(); } }

    internal void Register(IAdvisorOperation operation)
    {
        CheckThread();
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (!Identifier(operation.Id) || operations.ContainsKey(operation.Id))
            throw new InvalidOperationException("Invalid or duplicate operation ID: " + operation.Id);
        if (operation.Documentation == null) throw new InvalidOperationException("Command text is missing: " + operation.Id);
        operations.Add(operation.Id, operation);
    }
    // Reading documentation does not enable commands or create an execution session.
    internal IReadOnlyList<(string Id, AdvisorText Text, JObject Definition)> Inspect(string language)
    {
        CheckThread();
        if (retired) throw new AdvisorOperationException(409, "operation_scope_changed");
        return operations.Values.Select(operation => (operation.Id, operation.Documentation.For(language), Definition(operation))).ToArray();
    }
    internal JObject Catalog()
    {
        CheckThread();
        return new JObject { ["scope_id"] = ScopeId, ["enabled"] = Enabled,
            ["operations"] = Enabled ? new JArray(operations.Values.Select(Definition)) : new JArray() };
    }
    internal JArray PriorOrders(string owner)
    {
        CheckThread();
        if (planSubmissions.TryGetValue(owner, out var submitted))
        {
            var request = (JObject)submitted.Submission!.DeepClone(); request["action"] = "submit";
            return new JArray(new JObject { ["request"] = request, ["response"] = SubmissionResult(submitted) });
        }
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
        return Page(new JArray(matches.Select(o => new JObject { ["id"] = o.Id, ["description"] = o.Description, ["requires_approval"] = o.RequiresApproval })), offset, limit, "operations");
    }
    internal JObject Describe(string id)
    { CheckEnabled(); return new JObject { ["operation"] = Definition(Operation(id)) }; }
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
    private static JObject Definition(IAdvisorOperation operation)
    {
        var definition = (JObject)operation.Definition().DeepClone();
        definition["requires_approval"] = operation.RequiresApproval;
        definition["plan_policy"] = Policy(operation).Json();
        return definition;
    }
    internal AdvisorOperationProposal? Proposal(string owner)
    { CheckThread(); return proposals.TryGetValue(owner, out var proposal) ? proposal : null; }

    internal void BeginProposal(string owner)
    {
        CheckEnabled();
        if (Proposal(owner)?.State != AdvisorProposalState.Collecting)
            proposals[owner] = new AdvisorOperationProposal(owner);
    }
    internal JObject NormalizeArguments(string id, string target, JObject arguments, AdvisorOperationReferences references, JArray resolutions)
    {
        CheckEnabled();
        var operation = Operation(id);
        var detached = (JObject)arguments.DeepClone();
        return operation is IAdvisorOperationArgumentNormalizer normalizer
            ? normalizer.NormalizeArguments(target, detached, references, resolutions) : detached;
    }
    internal JObject Propose(string owner, JObject request)
    {
        CheckEnabled();
        ValidateRequest(request);
        var id = (string)request["request_id"]!;
        if (receipts.TryGetValue(id, out var previous)) return Replay(previous, request);
        var operation = Operation((string)request["operation"]!);
        if (receipts.Count >= MaximumReceipts) throw new AdvisorOperationException(409, "operation_receipt_limit");
        var proposal = Proposal(owner);
        if (proposal == null || proposal.State != AdvisorProposalState.Collecting) throw new AdvisorOperationException(409, "proposal_closed");
        if (proposal.Submission != null) throw new AdvisorOperationException(409, "proposal_already_submitted");
        var receipt = new Receipt(request);
        receipts.Add(id, receipt);
        var order = new AdvisorOperationOrder(request);
        try
        {
            // Validate the combined demand, without reserving inventory or mutating game state.
            operation.Validate(proposal.Orders.Where(o => o.OperationId == operation.Id).Concat(new[] { order }).ToArray());
            proposal.Add(order);
            proposal.RequiresApproval |= operation.RequiresApproval;
            receipt.Result = new JObject { ["request_id"] = id, ["status"] = "proposed", ["registered"] = false,
                ["requires_approval"] = operation.RequiresApproval };
        }
        catch (AdvisorOperationException error)
        { receipt.Result = new JObject { ["request_id"] = id, ["status"] = "rejected", ["code"] = error.Code }; }
        return (JObject)receipt.Result.DeepClone();
    }
    internal AdvisorOperationProposal? FinishProposal(string owner, bool success)
    {
        CheckThread();
        var proposal = Proposal(owner);
        if (proposal == null || proposal.State != AdvisorProposalState.Collecting) return proposal;
        if (!success) { CancelProposal(proposal); return proposal; }
        proposal.State = AdvisorProposalState.Pending;
        if (proposal.Orders.Count == 0) proposal.State = AdvisorProposalState.Rejected;
        else if (!proposal.RequiresApproval) Approve(proposal);
        return proposal;
    }
    internal string DescribeProposal(AdvisorOperationProposal proposal, string language, string? planId = null)
    {
        CheckThread(); RequireCurrent(proposal);
        return string.Join("\n\n", Plan(proposal, planId).Orders.GroupBy(o => o.OperationId)
            .Select(group => Operation(group.Key).DescribeProposal(group.ToArray(), language)));
    }
    internal AdvisorMapPreview PreviewProposal(AdvisorOperationProposal proposal, string language, string? planId = null)
    {
        CheckThread(); RequireCurrent(proposal);
        if (proposal.State != AdvisorProposalState.Pending || !Enabled) return AdvisorMapPreview.Empty;
        var previews = Plan(proposal, planId).Orders.GroupBy(o => o.OperationId)
            .Select(group => Operation(group.Key) is IAdvisorOperationPreview operation
                ? operation.Preview(group.ToArray(), language) : AdvisorMapPreview.Empty).ToArray();
        return new AdvisorMapPreview(previews.SelectMany(preview => preview.Drawings));
    }
    internal void Approve(AdvisorOperationProposal proposal, IEnumerable<string>? selected = null)
    {
        CheckThread(); RequireCurrent(proposal);
        if (proposal.State != AdvisorProposalState.Pending) return;
        var orders = SelectedOrders(proposal, selected);
        proposal.ExecutionOrders = orders;
        proposal.ErrorCode = null;
        try
        {
            CheckEnabled();
            foreach (var group in orders.GroupBy(o => o.OperationId))
                Operation(group.Key).Validate(group.ToArray());
        }
        catch (AdvisorOperationException error)
        {
            if (proposal.Submission != null) { proposal.ErrorCode = error.Code; return; }
            RejectRemaining(proposal, error.Code); proposal.State = AdvisorProposalState.Rejected; return;
        }
        proposal.State = AdvisorProposalState.Executing;
        foreach (var order in proposal.Orders.Except(orders))
            receipts[order.Id].Result = new JObject { ["request_id"] = order.Id, ["status"] = "not_selected" };
        // Preserve request order. Preflight is not a transaction: native side effects cannot be rolled back.
        foreach (var order in orders)
        {
            try
            {
                var data = Operation(order.OperationId).Execute(order.Target, order.Arguments);
                receipts[order.Id].Result = new JObject { ["request_id"] = order.Id, ["status"] = "registered", ["data"] = data.DeepClone() };
                proposal.RegisteredCount++;
            }
            catch (AdvisorOperationException error)
            {
                RejectRemaining(proposal, error.Code);
                proposal.State = proposal.RegisteredCount == 0 ? AdvisorProposalState.Rejected : AdvisorProposalState.Partial;
                return;
            }
            catch
            {
                RejectRemaining(proposal, "operation_outcome_unknown");
                receipts[order.Id].Result = null;
                proposal.State = AdvisorProposalState.Unknown;
                proposal.ErrorCode = "operation_outcome_unknown";
                throw;
            }
        }
        proposal.State = AdvisorProposalState.Registered;
    }
    internal void DiscardProposalsExcept(IEnumerable<string> owners)
    {
        CheckThread();
        var keep = new HashSet<string>(owners, StringComparer.Ordinal);
        foreach (var owner in proposals.Keys.Where(id => !keep.Contains(id)).ToArray())
        { CancelProposal(proposals[owner]); proposals.Remove(owner); }
    }
    internal void Retire()
    {
        CheckThread(); retired = true;
        foreach (var proposal in proposals.Values) CancelProposal(proposal);
    }
    private void CancelProposal(AdvisorOperationProposal proposal)
    {
        if (proposal.State is not (AdvisorProposalState.Collecting or AdvisorProposalState.Pending)) return;
        RejectRemaining(proposal, "proposal_cancelled", "cancelled");
        proposal.State = AdvisorProposalState.Cancelled;
    }
    private void RejectRemaining(AdvisorOperationProposal proposal, string code, string status = "rejected")
    {
        proposal.ErrorCode = code;
        foreach (var order in (proposal.State == AdvisorProposalState.Executing ? proposal.ExecutionOrders : proposal.Orders).Skip(proposal.RegisteredCount))
            receipts[order.Id].Result = new JObject { ["request_id"] = order.Id, ["status"] = status, ["code"] = code };
    }
    private void RequireCurrent(AdvisorOperationProposal proposal)
    {
        if (retired || !proposals.TryGetValue(proposal.Owner, out var current) || !ReferenceEquals(current, proposal))
            throw new AdvisorOperationException(409, "operation_scope_changed");
    }
    private void ValidateRequest(JObject request)
    {
        if (request.Properties().Any(p => p.Name is not ("scope_id" or "request_id" or "operation" or "target_ref" or "arguments")) ||
            request.Count != 5 || request["arguments"] is not JObject)
            throw new AdvisorOperationException(400, "invalid_operation_request");
        var scope = Text(request, "scope_id");
        if (!Identifier(Text(request, "request_id"))) throw new AdvisorOperationException(400, "invalid_request_id");
        _ = Text(request, "operation"); _ = Text(request, "target_ref");
        if (scope != ScopeId) throw new AdvisorOperationException(409, "operation_scope_changed");
    }
    private static JObject Replay(Receipt receipt, JObject request)
    {
        if (!JToken.DeepEquals(receipt.Request, request)) throw new AdvisorOperationException(409, "request_id_conflict");
        return receipt.Result == null ? throw new AdvisorOperationException(409, "operation_outcome_unknown") : (JObject)receipt.Result.DeepClone();
    }

    // Explicit developer API registration. This is not exposed through the AI operation gateway.
    internal JObject Execute(JObject request)
    {
        CheckEnabled();
        ValidateRequest(request);
        var id = (string)request["request_id"]!;
        var operationId = (string)request["operation"]!;
        var target = (string)request["target_ref"]!;
        var arguments = (JObject)request["arguments"]!;
        if (receipts.TryGetValue(id, out var previous)) return Replay(previous, request);
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
        if (retired || !enabled()) throw new AdvisorOperationException(403, "unit_operations_disabled");
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
