using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorOperationOrder
{
    private readonly JObject request;
    internal AdvisorOperationOrder(JObject request) => this.request = (JObject)request.DeepClone();
    internal string Id => (string)request["request_id"]!;
    internal string OperationId => (string)request["operation"]!;
    internal string Target => (string)request["target_ref"]!;
    internal JObject Arguments => (JObject)request["arguments"]!.DeepClone();
    internal JObject Request => (JObject)request.DeepClone();
}

internal enum AdvisorProposalState { Collecting, Pending, Executing, Registered, Rejected, Partial, Cancelled, Unknown }

// The conversation owns visibility; the battle-scoped operation service owns state and execution.
internal sealed class AdvisorOperationProposal
{
    private readonly List<AdvisorOperationPlan> plans = new() { new AdvisorOperationPlan("default", "", "", Array.Empty<AdvisorOperationOrder>()) };
    internal string Owner { get; }
    internal IReadOnlyList<AdvisorOperationPlan> Plans => plans.AsReadOnly();
    internal IReadOnlyList<AdvisorOperationOrder> Orders => plans.SelectMany(p => p.Orders).ToArray();
    internal IReadOnlyList<AdvisorOperationOrder> ExecutionOrders { get; set; } = Array.Empty<AdvisorOperationOrder>();
    internal AdvisorPlanSelection Selection { get; private set; } = AdvisorPlanSelection.Single;
    internal JObject? Submission { get; private set; }
    internal AdvisorProposalState State { get; set; } = AdvisorProposalState.Collecting;
    internal bool RequiresApproval { get; set; }
    internal string? ErrorCode { get; set; }
    internal int RegisteredCount { get; set; }
    internal AdvisorOperationProposal(string owner) => Owner = owner;
    internal void Add(AdvisorOperationOrder order) => plans[0].Add(order);
    internal void Submit(JObject submission, IEnumerable<AdvisorOperationPlan> values, AdvisorPlanSelection selection)
    {
        Submission = (JObject)submission.DeepClone();
        plans.Clear(); plans.AddRange(values); Selection = selection;
    }
}
