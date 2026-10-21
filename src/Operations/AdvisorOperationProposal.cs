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

internal enum AdvisorProposalState { Collecting, Pending, Registered, Rejected, Partial, Cancelled, Unknown }

// The conversation owns visibility; the battle-scoped operation service owns state and execution.
internal sealed class AdvisorOperationProposal
{
    private readonly List<AdvisorOperationOrder> orders = new();
    internal string Owner { get; }
    internal IReadOnlyList<AdvisorOperationOrder> Orders => orders.AsReadOnly();
    internal AdvisorProposalState State { get; set; } = AdvisorProposalState.Collecting;
    internal bool RequiresApproval { get; set; }
    internal string? ErrorCode { get; set; }
    internal int RegisteredCount { get; set; }
    internal AdvisorOperationProposal(string owner) => Owner = owner;
    internal void Add(AdvisorOperationOrder order) => orders.Add(order);
    internal string CaptionKey => "operation." + (State switch
    {
        AdvisorProposalState.Pending => "review", AdvisorProposalState.Registered => "registered",
        AdvisorProposalState.Partial => "partial", AdvisorProposalState.Cancelled => "cancelled",
        _ => "rejected"
    });
}
