using System;
using System.Collections.Generic;
using Noesis;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorTranscriptView
{
    private readonly StackPanel panel;
    private readonly Action<Action> guard;

    internal AdvisorTranscriptView(StackPanel panel, Action<Action> guard)
    { this.panel = panel; this.guard = guard; }

    internal void Show(IReadOnlyList<ChatExchange> exchanges, AdvisorText text, string advisor, string empty)
    {
        var root = GUI.ParseXaml(AdvisorTranscript.Render(exchanges, text["speaker.you"], advisor,
            text["action.copy"], text["transcript.copy_tip"], empty)) as StackPanel
            ?? throw new InvalidOperationException("The Markdown conversation view is not a StackPanel.");
        for (var i = 0; i < exchanges.Count; i++)
        {
            var answer = exchanges[i].Answer;
            var copy = root.FindName("CopyAnswer" + i) as Button
                ?? throw new InvalidOperationException("The answer copy button is missing.");
            copy.Click += (_, _) => guard(() => UnityEngine.GUIUtility.systemCopyBuffer = answer);
        }
        panel.Children.Clear();
        panel.Children.Add(root);
    }

    internal void Empty(string message)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
    }
}
