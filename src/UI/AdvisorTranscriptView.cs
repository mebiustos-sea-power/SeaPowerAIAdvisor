using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorTranscriptView
{
    private readonly StackPanel panel;
    private readonly Action<Action> guard;
    private readonly Action<AdvisorTurn> retry;
    private readonly Action<AdvisorTurn, ContactAssumption> reassess;
    private readonly Action<int> insertUnit;
    private readonly Action<AdvisorTurn>? reviewProposal;
    private readonly IAdvisorUnitNavigation? navigation;
    private readonly IAdvisorWeaponNavigation? weapons;
    private readonly IAdvisorMapNavigation? map;
    private readonly List<Button> reassessmentButtons = new();
    private readonly Dictionary<AdvisorTurn, FrameworkElement> answerHeaders = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<(AdvisorTurn Turn, bool Answer), double> copiedUntil = new();
    private readonly List<CopyBinding> copyButtons = new();
    private AdvisorTurn? waitingTurn;
    private TextBlock? waitingText;
    internal void UpdateProgress(AdvisorTurn turn, AdvisorText text)
    {
        if (ReferenceEquals(turn, waitingTurn) && waitingText != null) waitingText.Text = AdvisorProgress.Format(turn.Progress, text);
    }

    internal void Tick()
    {
        if (copiedUntil.Count == 0) return;
        var now = clock.Elapsed.TotalSeconds;
        foreach (var binding in copyButtons)
            if (copiedUntil.TryGetValue(binding.Key, out var until) && now >= until)
            { copiedUntil.Remove(binding.Key); binding.SetCopied(false); }
    }

    internal AdvisorTranscriptView(StackPanel panel, ScrollViewer scroll, Action<Action> guard, Action<AdvisorTurn> retry,
        Action<AdvisorTurn, ContactAssumption> reassess, Action<int> insertUnit, IAdvisorUnitNavigation? navigation = null, IAdvisorWeaponNavigation? weapons = null, IAdvisorMapNavigation? map = null, Action<AdvisorTurn>? reviewProposal = null)
    {
        this.reviewProposal = reviewProposal;
        this.panel = panel; this.guard = guard; this.retry = retry; this.reassess = reassess; this.insertUnit = insertUnit; this.navigation = navigation; this.weapons = weapons; this.map = map;
        panel.PreviewMouseWheel += (_, e) =>
        {
            if (e.Orientation != Orientation.Vertical) return;
            e.Handled = true;
            // Route vertical input past the table/code horizontal viewers to the transcript's native handler.
            guard(() => scroll.RaiseEvent(new MouseWheelEventArgs(scroll, UIElement.MouseWheelEvent, e.Delta, e.Orientation)));
        };
    }

    internal void UpdateBusy(bool busy)
    { foreach (var button in reassessmentButtons) button.IsEnabled = !busy; }

    internal void Show(AdvisorConversation conversation, AdvisorText text, string advisor, string empty, AdvisorNotice? notice, AdvisorTextSize textSize, bool linkOwnUnits, bool linkContacts)
    {
        var links = navigation == null || (!linkOwnUnits && !linkContacts) ? null :
            new AdvisorUnitLinks(navigation.Contains, navigation.OwnUnits(), linkContacts, linkOwnUnits);
        var weaponLinks = weapons == null ? null : new AdvisorWeaponLinks(weapons.Contains);
        var coordinates = map == null ? null : new AdvisorCoordinateLinks();
        var markup = AdvisorTranscript.Render(conversation, text, advisor, empty, notice, textSize, links, weaponLinks, coordinates);
        var root = GUI.ParseXaml(markup) as StackPanel
            ?? throw new InvalidOperationException("The Markdown conversation view is not a StackPanel.");
        if (links != null)
            foreach (var item in links.Links)
            {
                var link = root.FindName(item.Name) as Hyperlink
                    ?? throw new InvalidOperationException("The conversation unit link is missing.");
                link.Click += (_, e) =>
                {
                    e.Handled = true;
                    guard(() =>
                    {
                        if ((panel.Keyboard.Modifiers & ModifierKeys.Control) != 0) insertUnit(item.UnitId);
                        else navigation!.Focus(item.UnitId);
                    });
                };
                link.PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; guard(() => navigation!.OpenMenu(item.UnitId)); };
            }
        if (weaponLinks != null)
            foreach (var item in weaponLinks.Links)
            {
                var link = root.FindName(item.Name) as Hyperlink
                    ?? throw new InvalidOperationException("The conversation weapon link is missing.");
                link.Click += (_, e) => e.Handled = true;
                link.PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; guard(() => weapons!.OpenMenu(item.ReferenceKey)); };
            }
        if (coordinates != null)
            foreach (var item in coordinates.Links)
            {
                var link = root.FindName(item.Name) as Hyperlink
                    ?? throw new InvalidOperationException("The conversation coordinate link is missing.");
                link.Click += (_, e) => { e.Handled = true; guard(() => map!.Focus(item.Latitude, item.Longitude)); };
            }
        waitingTurn = null; waitingText = null;
        copyButtons.Clear(); reassessmentButtons.Clear(); answerHeaders.Clear();
        var now = clock.Elapsed.TotalSeconds;
        foreach (var key in copiedUntil.Keys.Where(key => !conversation.Turns.Contains(key.Turn) || copiedUntil[key] <= now).ToArray())
            copiedUntil.Remove(key);
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            BindCopy(root, "CopyQuestion" + i, turn.Request.Text, turn, false);
            if (turn.State == AdvisorTurnState.Waiting || turn.State == AdvisorTurnState.Answered)
                answerHeaders.Add(turn, root.FindName("AnswerHeader" + i) as FrameworkElement
                    ?? throw new InvalidOperationException("The advisor response header is missing."));
            if (turn.State == AdvisorTurnState.Waiting)
            {
                waitingTurn = turn;
                waitingText = root.FindName("ProgressTurn" + i) as TextBlock
                    ?? throw new InvalidOperationException("The waiting progress label is missing.");
            }
            if (turn.Exchange != null)
                BindCopy(root, "CopyAnswer" + i, turn.Exchange.Answer, turn, true);
            if (BattleContactAssessment.CanReassess(turn))
                foreach (var assumption in new[] { ContactAssumption.Friendly, ContactAssumption.Enemy })
                {
                    var button = root.FindName("Reassess" + assumption + i) as Button
                        ?? throw new InvalidOperationException("The contact reassessment button is missing.");
                    reassessmentButtons.Add(button);
                    button.Click += (_, _) => guard(() => { if (button.IsEnabled) reassess(turn, assumption); });
                }
            if (turn.State != AdvisorTurnState.Incomplete && turn.Proposal is AdvisorOperationProposal proposal && proposal.Orders.Count > 0 && proposal.State != AdvisorProposalState.Collecting)
            {
                var button = root.FindName("ReviewProposal" + i) as Button
                    ?? throw new InvalidOperationException("The proposal review button is missing.");
                if (proposal.State is AdvisorProposalState.Pending or AdvisorProposalState.Partial or AdvisorProposalState.Rejected or AdvisorProposalState.Unknown)
                    reassessmentButtons.Add(button);
                button.Click += (_, _) => guard(() => { if (button.IsEnabled) reviewProposal?.Invoke(turn); });
            }
            if (conversation.CanRetry(turn))
            {
                var button = root.FindName("RetryTurn" + i) as Button
                    ?? throw new InvalidOperationException("The retry button is missing.");
                button.Click += (_, _) => guard(() => retry(turn));
            }
        }
        panel.Children.Clear();
        panel.Children.Add(root);
    }

    internal void ScrollToAnswer(AdvisorTurn turn, ScrollViewer scroll)
    {
        if (!answerHeaders.TryGetValue(turn, out var header))
            throw new InvalidOperationException("The answer scroll target is missing.");
        var origin = new Vector4(0, 0, 0, 1) * header.TransformToAncestor(panel);
        scroll.ScrollToVerticalOffset(origin.Y, false);
    }

    private void BindCopy(FrameworkElement root, string name, string value, AdvisorTurn turn, bool answer)
    {
        var button = root.FindName(name) as Button
            ?? throw new InvalidOperationException("The conversation copy button is missing: " + name);
        var binding = new CopyBinding(button, (turn, answer));
        copyButtons.Add(binding);
        binding.SetCopied(copiedUntil.ContainsKey(binding.Key));
        binding.Button.Click += (_, _) => guard(() =>
        {
            if (!binding.Button.IsEnabled) return;
            UnityEngine.GUIUtility.systemCopyBuffer = value;
            copiedUntil[binding.Key] = clock.Elapsed.TotalSeconds + 2;
            binding.SetCopied(true);
        });
    }

    private sealed class CopyBinding
    {
        internal Button Button { get; }
        internal (AdvisorTurn Turn, bool Answer) Key { get; }
        internal CopyBinding(Button button, (AdvisorTurn Turn, bool Answer) key)
        { Button = button; Key = key; }
        internal void SetCopied(bool value) => Button.IsEnabled = !value;
    }
}
