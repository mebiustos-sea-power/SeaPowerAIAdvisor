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
        if (planSubmissions.TryGetValue(owner, out var prior))
        {
            if (!JToken.DeepEquals(prior.Submission, request)) throw new AdvisorOperationException(409, "request_id_conflict");
            return SubmissionResult(prior);
        }
        var proposal = Proposal(owner);
        if (proposal == null || proposal.State != AdvisorProposalState.Collecting) throw new AdvisorOperationException(409, "proposal_closed");
        if (proposal.Orders.Count != 0) throw new AdvisorOperationException(409, "proposal_already_submitted");
        var index = 0;
        var plans = ((JArray)request["plans"]!).Cast<JObject>().Select(p => new AdvisorOperationPlan((string)p["plan_id"]!,
            (string)p["title"]!, (string)p["summary"]!, ((JArray)p["operations"]!).Cast<JObject>().Select(o => new AdvisorOperationOrder(new JObject
            {
                ["scope_id"] = ScopeId, ["request_id"] = owner + "." + (string)request["request_id"]! + "." + (index++).ToString(CultureInfo.InvariantCulture),
                ["operation"] = o["operation_id"]!.DeepClone(), ["target_ref"] = o["target_ref"]!.DeepClone(), ["arguments"] = o["arguments"]!.DeepClone()
            })))).ToArray();
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
        // Validate alternatives independently. No receipts, reservations or partial plans survive a rejected set.
        foreach (var plan in plans)
            foreach (var group in plan.Orders.GroupBy(o => o.OperationId)) Operation(group.Key).Validate(group.ToArray());
        foreach (var order in orders)
            receipts.Add(order.Id, new Receipt(order.Request) { Result = new JObject { ["request_id"] = order.Id, ["status"] = "proposed", ["registered"] = false } });
        proposal.Submit(request, plans, selection);
        proposal.RequiresApproval = plans.Length > 1 || orders.Any(o => Operation(o.OperationId).RequiresApproval);
        planSubmissions.Add(owner, proposal);
        return SubmissionResult(proposal);
    }
    private static JObject SubmissionResult(AdvisorOperationProposal proposal) => new()
    {
        ["request_id"] = proposal.Submission!["request_id"]!.DeepClone(),
        ["status"] = proposal.State is AdvisorProposalState.Collecting or AdvisorProposalState.Pending ? "proposed" : proposal.State.ToString().ToLowerInvariant(),
        ["registered"] = proposal.State == AdvisorProposalState.Registered, ["requires_approval"] = proposal.RequiresApproval,
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
