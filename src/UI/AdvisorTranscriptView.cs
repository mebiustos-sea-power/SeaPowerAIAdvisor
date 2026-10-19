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

    internal AdvisorTranscriptView(StackPanel panel, Action<Action> guard, Action<AdvisorTurn> retry,
        Action<AdvisorTurn, ContactAssumption> reassess)
    { this.panel = panel; this.guard = guard; this.retry = retry; this.reassess = reassess; }

    internal void UpdateBusy(bool busy)
    { foreach (var button in reassessmentButtons) button.IsEnabled = !busy; }

    internal void Show(AdvisorConversation conversation, AdvisorText text, string advisor, string empty, AdvisorNotice? notice, AdvisorTextSize textSize)
    {
        var root = GUI.ParseXaml(AdvisorTranscript.Render(conversation, text, advisor, empty, notice, textSize)) as StackPanel
            ?? throw new InvalidOperationException("The Markdown conversation view is not a StackPanel.");
        waitingTurn = null; waitingText = null;
        copyButtons.Clear(); reassessmentButtons.Clear(); answerHeaders.Clear();
        var now = clock.Elapsed.TotalSeconds;
        foreach (var key in copiedUntil.Keys.Where(key => !conversation.Turns.Contains(key.Turn) || copiedUntil[key] <= now).ToArray())
            copiedUntil.Remove(key);
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            BindCopy(root, "CopyQuestion" + i, turn.Request.IsShortcut ? turn.Request.Text : turn.Request.DisplayText, turn, false);
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
