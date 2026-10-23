using System;
using System.Collections.Generic;
using System.Globalization;
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
    internal IReadOnlyList<string> SelectedPlanIds => Proposal.Plans.Where(p => selected.Contains(p.Id)).Select(p => p.Id).ToArray();

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
    internal string FormatTargets(AdvisorText text, Func<int, string?> unitName)
    {
        var targets = Proposal.Plans.Where(p => selected.Contains(p.Id)).SelectMany(p => p.Orders)
            .Select(order => order.Target).Distinct(StringComparer.Ordinal).Select(target =>
            {
                if (!target.StartsWith("unit:", StringComparison.Ordinal) ||
                    !int.TryParse(target.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return target;
                var name = unitName(id);
                return string.IsNullOrWhiteSpace(name) ? target : name + " (" + id.ToString(CultureInfo.InvariantCulture) + ")";
            }).ToArray();
        return text.Format("operation.targets", targets.Length == 0 ? text["operation.no_targets"] : string.Join(", ", targets));
    }
    internal void IncludeCurrent(bool include)
    {
        if (Single) throw new InvalidOperationException("Single-selection reviews execute the displayed plan.");
        if (include) selected.Add(Current.Id); else selected.Remove(Current.Id);
    }
}
