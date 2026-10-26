using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed partial class AdvisorOperations
{
    // Keep accepted submissions for the battle lifetime, including after their history row disappears.
    private readonly Dictionary<string, AdvisorOperationProposal> planSubmissions = new(StringComparer.Ordinal);
    private static AdvisorOperationPlanPolicy Policy(IAdvisorOperation operation) => operation is IAdvisorOperationPlans plans
        ? plans.PlanPolicy : AdvisorOperationPlanPolicy.Default;

    internal JObject SubmitPlans(string owner, JObject request)
    {
        CheckEnabled(); AdvisorOperationPlanBatch.Validate(request);
        var id = (string)request["request_id"]!;
        planSubmissions.TryGetValue(owner, out var prior);
        if (prior?.Submitted(id) is JObject accepted)
        {
            if (!JToken.DeepEquals(accepted, request)) throw new AdvisorOperationException(409, "request_id_conflict");
            if ((string?)prior.Submission!["request_id"] == id) return SubmissionResult(prior);
            return new JObject { ["request_id"] = id, ["status"] = "superseded", ["registered"] = false,
                ["current_submission"] = SubmissionResult(prior) };
        }
        var proposal = prior ?? Proposal(owner);
        if (proposal == null || proposal.State != AdvisorProposalState.Collecting || !ReferenceEquals(proposal, Proposal(owner)))
            throw new AdvisorOperationException(409, "proposal_closed");
        var prefix = owner + "." + proposal.SubmissionCount.ToString(CultureInfo.InvariantCulture) + ".";
        var index = 0;
        var plans = ((JArray)request["plans"]!).Cast<JObject>().Select(p => new AdvisorOperationPlan((string)p["plan_id"]!,
            (string)p["title"]!, (string)p["summary"]!, ((JArray)p["operations"]!).Cast<JObject>().Select(o => new AdvisorOperationOrder(new JObject
            {
                ["scope_id"] = ScopeId, ["request_id"] = prefix + (index++).ToString(CultureInfo.InvariantCulture),
                ["operation"] = o["operation_id"]!.DeepClone(), ["target_ref"] = o["target_ref"]!.DeepClone(), ["arguments"] = o["arguments"]!.DeepClone()
            })), (JArray)p["reservation_updates"]!)).ToArray();
        var orders = plans.SelectMany(p => p.Orders).ToArray();
        var policies = orders.Select(o => Operation(o.OperationId)).Distinct().Select(Policy).ToArray();
        var minimum = policies.Max(p => p.Minimum); var maximum = policies.Min(p => p.Maximum);
        if (minimum > maximum) throw new AdvisorOperationException(400, "incompatible_plan_policies");
        if (plans.Length < minimum || plans.Length > maximum) throw new AdvisorOperationException(400, "plan_count_out_of_range");
        var selection = policies.Any(p => p.Selection == AdvisorPlanSelection.Single) ? AdvisorPlanSelection.Single : AdvisorPlanSelection.Multiple;
        if (receipts.Count + orders.Length > MaximumReceipts) throw new AdvisorOperationException(409, "operation_receipt_limit");
        foreach (var order in orders)
        {
            ValidateRequest(order.Request);
            if (receipts.ContainsKey(order.Id)) throw new AdvisorOperationException(409, "request_id_conflict");
        }
        // Validate the complete revision before changing any accepted plans or receipts.
        foreach (var plan in plans) ValidatePlan(plan);
        foreach (var order in proposal.Orders)
            receipts[order.Id].Result = new JObject { ["request_id"] = order.Id, ["status"] = "superseded", ["registered"] = false };
        foreach (var order in orders)
            receipts.Add(order.Id, new Receipt() { Result = new JObject { ["request_id"] = order.Id, ["status"] = "proposed", ["registered"] = false } });
        proposal.Submit(request, plans, selection);
        proposal.RequiresApproval = plans.Length > 1 || orders.Any(o => Operation(o.OperationId).RequiresApproval);
        planSubmissions[owner] = proposal;
        return SubmissionResult(proposal);
    }
    // Explicit, AI-free development execution through the same proposal validation and ordered approval path.
    internal JObject SubmitAndApprove(JObject request)
    {
        CheckEnabled();
        if (request.Properties().Any(p => p.Name is not ("scope_id" or "request_id" or "plans" or "selected_plan_ids")))
            throw new AdvisorOperationException(400, "invalid_operation_request");
        if (Text(request, "scope_id") != ScopeId) throw new AdvisorOperationException(409, "operation_scope_changed");
        var submission = (JObject)request.DeepClone(); submission.Remove("scope_id"); submission.Remove("selected_plan_ids");
        AdvisorOperationPlanBatch.Validate(submission);
        string[]? selected = null;
        if (request["selected_plan_ids"] != null)
        {
            if (request["selected_plan_ids"] is not JArray ids || ids.Any(id => id.Type != JTokenType.String))
                throw new AdvisorOperationException(400, "invalid_plan_selection");
            selected = ids.Values<string>().Select(id => id!).ToArray();
        }
        var owner = "developer-plan." + (string)submission["request_id"]!;
        if (!planSubmissions.ContainsKey(owner) && Proposal(owner) == null) BeginProposal(owner);
        _ = SubmitPlans(owner, submission);
        var proposal = planSubmissions[owner];
        // Validate selection before FinishProposal can auto-adopt a command that does not require approval.
        var orders = SelectedOrders(proposal, selected);
        if (proposal.State is not (AdvisorProposalState.Collecting or AdvisorProposalState.Pending) &&
            !proposal.ExecutionOrders.SequenceEqual(orders)) throw new AdvisorOperationException(409, "request_id_conflict");
        if (proposal.State == AdvisorProposalState.Collecting)
        {
            proposal.RequiresApproval = true;
            FinishProposal(owner, true);
        }
        if (proposal.State == AdvisorProposalState.Pending) Approve(proposal, selected);
        var result = SubmissionResult(proposal);
        result["scope_id"] = ScopeId;
        if (proposal.ErrorCode != null) result["code"] = proposal.ErrorCode;
        result["results"] = new JArray(proposal.ExecutionOrders.Select(order => receipts[order.Id].Result?.DeepClone()
            ?? new JObject { ["request_id"] = order.Id, ["status"] = "unknown" }));
        return result;
    }

    private static JObject SubmissionResult(AdvisorOperationProposal proposal) => new()
    {
        ["request_id"] = proposal.Submission!["request_id"]!.DeepClone(),
        ["status"] = proposal.State is AdvisorProposalState.Collecting or AdvisorProposalState.Pending ? "proposed" : proposal.State.ToString().ToLowerInvariant(),
        ["registered"] = proposal.State == AdvisorProposalState.Registered, ["requires_approval"] = proposal.RequiresApproval,
        ["replaces_previous_submission"] = proposal.SubmissionCount > 1,
        ["selection"] = proposal.Selection == AdvisorPlanSelection.Single ? "single" : "multiple",
        ["plans"] = new JArray(proposal.Plans.Select(p => new JObject { ["plan_id"] = p.Id, ["operation_count"] = p.Orders.Count }))
    };
    private static AdvisorOperationPlan Plan(AdvisorOperationProposal proposal, string? id) => id == null
        ? proposal.Plans[0] : proposal.Plans.FirstOrDefault(p => p.Id == id) ?? throw new AdvisorOperationException(400, "unknown_plan");
    private static IReadOnlyList<AdvisorOperationOrder> SelectedOrders(AdvisorOperationProposal proposal, IEnumerable<string>? selected)
    {
        var ids = selected?.ToArray() ?? (proposal.Plans.Count == 1 ? new[] { proposal.Plans[0].Id } : Array.Empty<string>());
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            proposal.Selection == AdvisorPlanSelection.Single && ids.Length != 1 || ids.Any(id => !proposal.Plans.Any(p => p.Id == id)))
            throw new AdvisorOperationException(400, "invalid_plan_selection");
        // Selection click order cannot reorder plans or their operations.
        return proposal.Plans.Where(p => ids.Contains(p.Id, StringComparer.Ordinal)).SelectMany(p => p.Orders).ToArray();
    }
}
