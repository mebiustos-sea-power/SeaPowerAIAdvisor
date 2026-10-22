using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// A small modal review; operations supply descriptions and map data, never UI controls.
internal sealed class AdvisorOperationReview
{
    private readonly FrameworkElement overlay, conversation;
    private readonly TextBlock details;
    private readonly Button execute, back;
    private readonly Func<AdvisorOperations?> operations;
    private readonly Func<AdvisorText> text;
    private readonly Action refresh;
    private readonly AdvisorOperationPreview preview;
    private AdvisorOperationProposal? current;
    internal bool IsOpen => current != null;
    internal AdvisorOperationReview(FrameworkElement root, FrameworkElement conversation, Func<AdvisorOperations?> operations,
        Func<AdvisorText> text, Action<Action> guard, Action refresh, IAdvisorMap? map = null)
    {
        preview = new AdvisorOperationPreview(map);
        this.conversation = conversation; this.operations = operations; this.text = text; this.refresh = refresh;
        overlay = Find<FrameworkElement>(root, "OperationReview");
        details = Find<TextBlock>(root, "OperationReviewText");
        execute = Find<Button>(root, "ExecuteProposal"); back = Find<Button>(root, "CloseProposal");
        back.Click += (_, _) => guard(Close);
        execute.Click += (_, _) => guard(() =>
        {
            if (!execute.IsEnabled || current == null) return;
            var service = operations() ?? throw new InvalidOperationException("The proposal's operation service is unavailable.");
            preview.Dispose();
            service.Approve(current);
            refresh();
            if (current.State == AdvisorProposalState.Registered) Close(); else Show(current);
        });
    }
    internal void Show(AdvisorOperationProposal proposal)
    {
        var service = operations() ?? throw new InvalidOperationException("The proposal's operation service is unavailable.");
        Close();
        current = proposal;
        details.Text = service.DescribeProposal(proposal, text().Language);
        if (proposal.ErrorCode != null)
            details.Text += "\n\n" + text().Format("operation.failure", proposal.ErrorCode, proposal.RegisteredCount, proposal.Orders.Count);
        execute.IsEnabled = proposal.State == AdvisorProposalState.Pending;
        try { preview.Show(service, proposal, text().Language); }
        catch { Close(); throw; }
        conversation.IsEnabled = false; overlay.Visibility = Visibility.Visible;
        back.Focus();
    }
    internal void Tick()
    {
        if (current == null) return;
        var service = operations();
        if (service == null || !ReferenceEquals(service.Proposal(current.Owner), current) || current.State == AdvisorProposalState.Cancelled)
        { Close(); return; }
        preview.Tick(service);
        execute.IsEnabled = current.State == AdvisorProposalState.Pending && service.Enabled;
    }
    internal void Close()
    {
        current = null;
        try { preview.Dispose(); }
        finally { overlay.Visibility = Visibility.Collapsed; conversation.IsEnabled = true; }
    }
    private static T Find<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T
        ?? throw new InvalidOperationException("The operation review control is missing: " + name);
}
