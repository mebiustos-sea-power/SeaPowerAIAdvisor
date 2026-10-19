using System;
using System.Linq;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorTranscript
{
    internal static string Render(AdvisorConversation conversation, AdvisorText text, string advisor, string empty,
        AdvisorNotice? configurationNotice = null, AdvisorTextSize textSize = AdvisorTypography.DefaultSize)
    {
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"));
        var busy = conversation.Turns.Any(t => t.State == AdvisorTurnState.Waiting);
        if (configurationNotice != null) root.Add(Error(configurationNotice.Message(text)));
        if (conversation.Turns.Count == 0 && configurationNotice == null) root.Add(Plain(empty));
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            var copyCaption = turn.Request.IsShortcut ? "action.copy_prompt" : "action.copy";
            var copyTip = turn.Request.IsShortcut ? "transcript.copy_prompt_tip" : "transcript.copy_question_tip";
            var row = Element("StackPanel", new XAttribute("Margin", "0,0,0,20"),
                Header(text["speaker.you"], "AdvisorPlayerNameBrush", "CopyQuestion" + i, text, copyTip, "0,0,0,6", copyCaption),
                Plain(turn.Request.DisplayText));
            if (turn.State == AdvisorTurnState.Answered)
            {
                var exchange = turn.Exchange ?? throw new InvalidOperationException("The completed answer is missing.");
                row.Add(Header(turn.AdvisorName.Length == 0 ? advisor : turn.AdvisorName, "AdvisorNameBrush",
                    "CopyAnswer" + i, text, "transcript.copy_tip", "0,6,0,6"), AdvisorMarkdown.Render(exchange.Answer));
                if (BattleContactAssessment.CanReassess(turn))
                {
                    var actions = Element("WrapPanel", new XAttribute("Margin", "0,6,0,0"));
                    foreach (var assumption in new[] { ContactAssumption.Friendly, ContactAssumption.Enemy })
                    {
                        var button = Action("Reassess" + assumption + i,
                            text["contact.reassess_" + BattleContactAssessment.AffiliationCode(assumption)], null);
                        button.Add(new XAttribute("Margin", "0,0,6,6"), new XAttribute("IsEnabled", !busy));
                        actions.Add(button);
                    }
                    row.Add(actions);
                }
            }
            else if (turn.State == AdvisorTurnState.Failed)
                row.Add(Error((turn.Error ?? throw new InvalidOperationException("The failed turn has no error.")).Message(text)));
            else if (turn.State == AdvisorTurnState.Waiting)
            {
                var waiting = Plain(AdvisorProgress.Format(turn.Progress, text));
                waiting.Add(new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", "ProgressTurn" + i));
                row.Add(waiting);
            }
            else row.Add(Plain(text["status.cancelled"]));
            if (conversation.CanRetry(turn)) row.Add(Action("RetryTurn" + i, text["action.retry"], text["transcript.retry_tip"]));
            root.Add(row);
        }
        AdvisorTypography.Apply(root, textSize);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Header(string speaker, string brush, string copyName, AdvisorText text, string tipKey, string margin, string captionKey = "action.copy") =>
        Element("Grid", new XAttribute("Margin", margin),
            Element("Grid.ColumnDefinitions", Element("ColumnDefinition", new XAttribute("Width", "*")),
                Element("ColumnDefinition", new XAttribute("Width", "Auto"))),
            Label(speaker, brush), Action(copyName, text[captionKey], text[tipKey], 1));

    private static XElement Action(string name, string caption, string? tip, int column = 0)
    {
        var button = Element("Button", new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", name),
            new XAttribute("Grid.Column", column), new XAttribute("FontSize", "12"), new XAttribute("Padding", "8,2"),
            new XAttribute("Focusable", "False"), new XAttribute("HorizontalAlignment", "Left"), Text("Content", caption));
        if (tip != null) button.Add(Text("ToolTip", tip));
        return button;
    }
    private static XElement Error(string value)
    {
        var element = Plain(value);
        element.Add(new XAttribute("Foreground", "{DynamicResource AdvisorErrorBrush}"));
        return element;
    }
    private static XElement Label(string value, string brush) => Element("TextBlock", Text("Text", value),
        new XAttribute("Foreground", "{DynamicResource " + brush + "}"),
        new XAttribute("FontWeight", "Bold"), new XAttribute("FontSize", "14"),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("Margin", "0,0,0,5"));
    private static XElement Plain(string value) => Element("TextBlock", Text("Text", value),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("FontSize", "14"), new XAttribute("Margin", "0,0,0,8"));
}
