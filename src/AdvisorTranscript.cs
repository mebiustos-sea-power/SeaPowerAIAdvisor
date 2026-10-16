using System;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorTranscript
{
    internal static string Render(AdvisorConversation conversation, AdvisorText text, string advisor, string empty,
        AdvisorNotice? configurationNotice = null)
    {
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"));
        if (configurationNotice != null) root.Add(Error(configurationNotice.Message(text)));
        if (conversation.Turns.Count == 0 && configurationNotice == null) root.Add(Plain(empty));
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            var row = Element("StackPanel", new XAttribute("Margin", "0,0,0,20"), Label(text["speaker.you"], "AdvisorPlayerNameBrush"),
                Plain(turn.Request.DisplayText));
            if (turn.State == AdvisorTurnState.Answered)
            {
                var exchange = turn.Exchange ?? throw new InvalidOperationException("The completed answer is missing.");
                row.Add(Element("Grid", new XAttribute("Margin", "0,6,0,6"),
                    Element("Grid.ColumnDefinitions", Element("ColumnDefinition", new XAttribute("Width", "*")),
                        Element("ColumnDefinition", new XAttribute("Width", "Auto"))),
                    Label(advisor, "AdvisorNameBrush"),
                    Action("CopyAnswer" + i, text["action.copy"], text["transcript.copy_tip"], 1)),
                    AdvisorMarkdown.Render(exchange.Answer));
            }
            else if (turn.State == AdvisorTurnState.Failed)
                row.Add(Error((turn.Error ?? throw new InvalidOperationException("The failed turn has no error.")).Message(text)));
            else row.Add(Plain(text[turn.State == AdvisorTurnState.Waiting ? "transcript.waiting" : "status.cancelled"]));
            if (conversation.CanRetry(turn)) row.Add(Action("RetryTurn" + i, text["action.retry"], text["transcript.retry_tip"]));
            root.Add(row);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Action(string name, string caption, string tip, int column = 0) => Element("Button",
        new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", name),
        new XAttribute("Grid.Column", column), new XAttribute("FontSize", "12"), new XAttribute("Padding", "8,2"),
        new XAttribute("Focusable", "False"), new XAttribute("HorizontalAlignment", "Left"),
        Text("Content", caption), Text("ToolTip", tip));
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
