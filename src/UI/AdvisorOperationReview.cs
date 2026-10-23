using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// Operations supply descriptions and map data; this view owns navigation, selection controls and the preview lease.
internal sealed class AdvisorOperationReview
{
    private readonly FrameworkElement overlay, conversation, navigation, selection;
    private readonly TextBlock details, counter, selectedCount, failure, targets;
    private readonly ScrollViewer detailScroll, targetScroll;
    private readonly CheckBox include;
    private readonly Button execute, back, previous, next;
    private readonly Func<AdvisorOperations?> operations;
    private readonly Func<AdvisorText> text;
    private readonly Action refresh;
    private readonly AdvisorOperationPreview preview;
    private readonly IAdvisorUnitNavigation? unitNavigation;
    private AdvisorOperationReviewState? review;
    private bool updating;
    internal bool IsOpen => review != null;
    internal AdvisorOperationReview(FrameworkElement root, FrameworkElement conversation, Func<AdvisorOperations?> operations,
        Func<AdvisorText> text, Action<Action> guard, Action refresh, IAdvisorMap? map = null, IAdvisorUnitNavigation? unitNavigation = null)
    {
        preview = new AdvisorOperationPreview(map); this.unitNavigation = unitNavigation;
        this.conversation = conversation; this.operations = operations; this.text = text; this.refresh = refresh;
        overlay = Find<FrameworkElement>(root, "OperationReview");
        navigation = Find<FrameworkElement>(root, "OperationPlanNavigation");
        selection = Find<FrameworkElement>(root, "OperationPlanSelection");
        counter = Find<TextBlock>(root, "OperationPlanCounter");
        selectedCount = Find<TextBlock>(root, "OperationPlanSelectedCount");
        include = Find<CheckBox>(root, "IncludeOperationPlan");
        details = Find<TextBlock>(root, "OperationReviewText");
        detailScroll = Find<ScrollViewer>(root, "OperationReviewScroll");
        failure = Find<TextBlock>(root, "OperationReviewFailure");
        targets = Find<TextBlock>(root, "OperationReviewTargets");
        targetScroll = Find<ScrollViewer>(root, "OperationReviewTargetsScroll");
        execute = Find<Button>(root, "ExecuteProposal"); back = Find<Button>(root, "CloseProposal");
        previous = Find<Button>(root, "PreviousOperationPlan"); next = Find<Button>(root, "NextOperationPlan");
        back.Click += (_, _) => guard(Close);
        previous.Click += (_, _) => guard(() => { if (previous.IsEnabled) Move(-1); });
        next.Click += (_, _) => guard(() => { if (next.IsEnabled) Move(1); });
        void Select(bool value)
        {
            if (updating || review == null || !include.IsEnabled) return;
            review.IncludeCurrent(value); UpdateTargets(); UpdateState(Service());
        }
        include.Checked += (_, _) => guard(() => Select(true));
        include.Unchecked += (_, _) => guard(() => Select(false));
        execute.Click += (_, _) => guard(() =>
        {
            if (!execute.IsEnabled || review == null) return;
            var service = Service(); var proposal = review.Proposal;
            preview.Dispose();
            service.Approve(proposal, review.SelectedPlanIds);
            refresh();
            if (proposal.State == AdvisorProposalState.Registered) Close();
            else
            {
                UpdateState(service);
                // A failed preflight has no native side effects; retain the displayed plan and choices.
                if (proposal.State == AdvisorProposalState.Pending) Inspect();
            }
        });
    }
    internal void Show(AdvisorOperationProposal proposal, string? planId = null)
    {
        Close(); review = new AdvisorOperationReviewState(proposal, planId);
        navigation.Visibility = review.CanNavigate ? Visibility.Visible : Visibility.Collapsed;
        selection.Visibility = review.Single ? Visibility.Collapsed : Visibility.Visible;
        try { UpdateState(Service()); Inspect(); }
        catch { Close(); throw; }
        conversation.IsEnabled = false; overlay.Visibility = Visibility.Visible; back.Focus();
    }
    private AdvisorOperations Service() => operations() ?? throw new InvalidOperationException("The proposal's operation service is unavailable.");
    private void Move(int direction)
    {
        if (review == null || !review.Move(direction)) return;
        Inspect(); UpdateState(Service());
    }
    private void Inspect()
    {
        if (review == null) return;
        var service = Service(); var plan = review.Current;
        counter.Text = text().Format("operation.plan_counter", review.Index + 1, review.Proposal.Plans.Count);
        details.Text = (plan.Title.Length == 0 ? "" : plan.Title + "\n" + plan.Summary + "\n\n") + service.DescribeProposal(review.Proposal, text().RequestedLanguage, plan.Id);
        detailScroll.ScrollToVerticalOffset(0, false);
        UpdateTargets();
        try { preview.Show(service, review.Proposal, text().RequestedLanguage, plan.Id); }
        catch { Close(); throw; }
    }
    private void UpdateTargets()
    {
        if (review == null) return;
        targets.Text = review.FormatTargets(text(), id => unitNavigation?.GetUnit(id)?.Name);
        targetScroll.ScrollToVerticalOffset(0, false);
    }
    private void UpdateState(AdvisorOperations service)
    {
        if (review == null) return;
        var proposal = review.Proposal;
        var pending = proposal.State == AdvisorProposalState.Pending && service.Enabled;
        previous.IsEnabled = next.IsEnabled = pending && review.CanNavigate;
        include.IsEnabled = pending && !review.Single;
        updating = true;
        try { include.IsChecked = review.IncludesCurrent; }
        finally { updating = false; }
        var count = review.SelectedPlanIds.Count;
        selectedCount.Text = text().Format("operation.selected_count", count);
        execute.IsEnabled = pending && count > 0;
        failure.Text = proposal.ErrorCode == null ? "" : text().Format("operation.failure", proposal.ErrorCode,
            proposal.RegisteredCount, proposal.ExecutionOrders.Count == 0 ? proposal.Orders.Count : proposal.ExecutionOrders.Count);
        failure.Visibility = proposal.ErrorCode == null ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void Tick()
    {
        if (review == null) return;
        var service = operations(); var proposal = review.Proposal;
        if (service == null || !ReferenceEquals(service.Proposal(proposal.Owner), proposal) || proposal.State == AdvisorProposalState.Cancelled)
        { Close(); return; }
        preview.Tick(service); UpdateState(service);
    }
    internal void Close()
    {
        review = null;
        try { preview.Dispose(); }
        finally { overlay.Visibility = Visibility.Collapsed; conversation.IsEnabled = true; }
    }
    private static T Find<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T
        ?? throw new InvalidOperationException("The operation review control is missing: " + name);
}
