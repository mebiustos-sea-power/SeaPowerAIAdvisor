using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Presentation state only. Browsing never changes retained operations or registers game orders.
internal sealed class AdvisorOperationReviewState
{
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    internal AdvisorOperationProposal Proposal { get; }
    internal int Index { get; private set; }
    internal AdvisorOperationPlan Current => Proposal.Plans[Index];
    internal bool Single => Proposal.Selection == AdvisorPlanSelection.Single;
    internal bool CanNavigate => Proposal.Plans.Count > 1;
    internal bool IncludesCurrent => selected.Contains(Current.Id);
    internal IEnumerable<AdvisorOperationPlan> SelectedPlans => Proposal.Plans.Where(p => selected.Contains(p.Id));
    internal IReadOnlyList<string> SelectedPlanIds => SelectedPlans.Select(p => p.Id).ToArray();

    internal AdvisorOperationReviewState(AdvisorOperationProposal proposal, string? planId = null)
    {
        Proposal = proposal;
        if (proposal.Plans.Count == 0) throw new InvalidOperationException("An operation review requires a plan.");
        foreach (var plan in proposal.Plans.Where(p => p.Orders.Any(proposal.ExecutionOrders.Contains))) selected.Add(plan.Id);
        if (selected.Count == 0) selected.Add(proposal.Plans[0].Id);
        Index = planId == null ? Enumerable.Range(0, proposal.Plans.Count).First(i => selected.Contains(proposal.Plans[i].Id))
            : Enumerable.Range(0, proposal.Plans.Count).Where(i => proposal.Plans[i].Id == planId).DefaultIfEmpty(-1).First();
        if (Index < 0) throw new ArgumentException("The reviewed operation plan is unavailable.", nameof(planId));
        if (Single) { selected.Clear(); selected.Add(Current.Id); }
    }
    internal bool Move(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!CanNavigate) return false;
        Index = (Index + direction + Proposal.Plans.Count) % Proposal.Plans.Count;
        if (Single) { selected.Clear(); selected.Add(Current.Id); }
        return true;
    }
    internal void IncludeCurrent(bool include)
    {
        if (Single) throw new InvalidOperationException("Single-selection reviews execute the displayed plan.");
        if (include) selected.Add(Current.Id); else selected.Remove(Current.Id);
    }
}
