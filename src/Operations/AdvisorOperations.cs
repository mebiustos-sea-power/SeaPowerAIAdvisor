using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

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
        internal JObject? Result;
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
        return new JArray();
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
        var operation = Operation(id);
        var rows = operation.Candidates();
        if (target != null) rows = new JArray(rows.Where(c => (string?)c["target_ref"] == target));
        var result = Page(rows, offset, limit, "candidates");
        // Supply the contract with live choices so a plan needs no separate definition lookup.
        result["operation"] = Definition(operation);
        return result;
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
        if (operation is IAdvisorReservationCommand reservation && reservation.ComposesReservations)
            definition["reservation_sequence"] = new JObject { ["ends_sequence"] = reservation.EndsReservationSequence };
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
        return DescribePlan(Plan(proposal, planId), language);
    }
    internal AdvisorMapPreview PreviewProposal(AdvisorOperationProposal proposal, string language, string? planId = null)
    {
        CheckThread(); RequireCurrent(proposal);
        if (proposal.State != AdvisorProposalState.Pending || !Enabled) return AdvisorMapPreview.Empty;
        return PreviewPlan(Plan(proposal, planId), language);
    }
    internal void Approve(AdvisorOperationProposal proposal, IEnumerable<string>? selected = null)
    {
        CheckThread(); RequireCurrent(proposal);
        if (proposal.State != AdvisorProposalState.Pending) return;
        var orders = SelectedOrders(proposal, selected);
        proposal.ExecutionOrders = orders;
        proposal.ErrorCode = null;
        ReservationPlan? prepared = null;
        try
        {
            CheckEnabled();
            prepared = ValidatePlan(CombinePlans(proposal.Plans.Where(p => p.Orders.Any(orders.Contains))));
        }
        catch (AdvisorOperationException error)
        {
            proposal.ErrorCode = error.Code; return;
        }
        proposal.State = AdvisorProposalState.Executing;
        foreach (var order in proposal.Orders.Except(orders))
            receipts[order.Id].Result = new JObject { ["request_id"] = order.Id, ["status"] = "not_selected" };
        // Preserve request order. Preflight is not a transaction: native side effects cannot be rolled back.
        var begun = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            try
            {
                JObject data;
                if (ReservationCommand(order) is IAdvisorReservationCommand command)
                {
                    var changed = false;
                    try
                    {
                        if (begun.Add(order.Target)) changed = command.BeginQueue(order.Target, prepared.Placements[order.Target]);
                        data = command.ExecuteReservation(prepared.Steps[order.Id]);
                    }
                    catch (AdvisorOperationException error) when (changed)
                    { throw new InvalidOperationException("Reservation execution failed after queue/control changes.", error); }
                }
                else data = Operation(order.OperationId).Execute(order.Target, order.Arguments);
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
        CheckThread(); if (retired) return;
        retired = true;
        foreach (var proposal in proposals.Values) CancelProposal(proposal);
        var errors = new List<Exception>();
        foreach (var operation in operations.Values.OfType<IDisposable>())
            try { operation.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Command sessions could not be fully disposed.", errors);
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
