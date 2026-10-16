using System;
using Noesis;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorTranscriptView
{
    private readonly StackPanel panel;
    private readonly Action<Action> guard;
    private readonly Action<AdvisorTurn> retry;
    private AdvisorTurn? waitingTurn;
    private TextBlock? waitingText;
    internal void UpdateProgress(AdvisorTurn turn, AdvisorText text)
    {
        if (ReferenceEquals(turn, waitingTurn) && waitingText != null) waitingText.Text = AdvisorProgress.Format(turn.Progress, text);
    }

    internal AdvisorTranscriptView(StackPanel panel, Action<Action> guard, Action<AdvisorTurn> retry)
    { this.panel = panel; this.guard = guard; this.retry = retry; }

    internal void Show(AdvisorConversation conversation, AdvisorText text, string advisor, string empty, AdvisorNotice? notice, AdvisorTextSize textSize)
    {
        var root = GUI.ParseXaml(AdvisorTranscript.Render(conversation, text, advisor, empty, notice, textSize)) as StackPanel
            ?? throw new InvalidOperationException("The Markdown conversation view is not a StackPanel.");
        waitingTurn = null; waitingText = null;
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            if (turn.State == AdvisorTurnState.Waiting)
            {
                waitingTurn = turn;
                waitingText = root.FindName("ProgressTurn" + i) as TextBlock
                    ?? throw new InvalidOperationException("The waiting progress label is missing.");
            }
            if (turn.Exchange != null)
            {
                var answer = turn.Exchange.Answer;
                var copy = root.FindName("CopyAnswer" + i) as Button
                    ?? throw new InvalidOperationException("The answer copy button is missing.");
                copy.Click += (_, _) => guard(() => UnityEngine.GUIUtility.systemCopyBuffer = answer);
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
}
