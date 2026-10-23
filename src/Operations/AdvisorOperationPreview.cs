using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

// Optional for operations whose proposed work has a useful geographic representation.
internal interface IAdvisorOperationPreview
{
    AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language);
}

// The review owns this lease; closing a preview does not cancel the proposal or change the game.
internal sealed class AdvisorOperationPreview : IDisposable
{
    private readonly IAdvisorMap? map;
    private IDisposable? drawing;
    private AdvisorOperations? owner;
    private AdvisorOperationProposal? proposal;
    internal AdvisorOperationPreview(IAdvisorMap? map) => this.map = map;
    internal void Show(AdvisorOperations operations, AdvisorOperationProposal value, string language, string? planId = null)
    {
        Dispose();
        if (map == null) return;
        var preview = operations.PreviewProposal(value, language, planId);
        if (preview.Drawings.Count == 0) return;
        owner = operations; proposal = value;
        // Reviewing or switching plans preserves the player's current map position and zoom.
        try { drawing = map.Draw(preview.Drawings); }
        catch { Dispose(); throw; }
    }
    internal void Tick(AdvisorOperations? operations)
    {
        if (proposal != null && (!ReferenceEquals(owner, operations) || !owner!.Enabled ||
            !ReferenceEquals(owner.Proposal(proposal.Owner), proposal) || proposal.State != AdvisorProposalState.Pending)) Dispose();
    }
    public void Dispose()
    {
        var previous = drawing; drawing = null; owner = null; proposal = null;
        previous?.Dispose();
    }
}
