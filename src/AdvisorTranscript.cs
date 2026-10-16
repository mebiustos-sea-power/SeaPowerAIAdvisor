using System.Collections.Generic;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorTranscript
{
    internal static string Render(IReadOnlyList<ChatExchange> exchanges, string player, string advisor,
        string copy, string copyTip, string empty)
    {
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"));
        if (exchanges.Count == 0) root.Add(Plain(empty));
        for (var i = 0; i < exchanges.Count; i++)
        {
            var exchange = exchanges[i];
            root.Add(Element("StackPanel", new XAttribute("Margin", "0,0,0,20"), Label(player),
                Plain(exchange.DisplayQuestion),
                Element("Grid", new XAttribute("Margin", "0,6,0,6"),
                    Element("Grid.ColumnDefinitions", Element("ColumnDefinition", new XAttribute("Width", "*")),
                        Element("ColumnDefinition", new XAttribute("Width", "Auto"))),
                    Label(advisor),
                    Element("Button", new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", "CopyAnswer" + i),
                        new XAttribute("Grid.Column", "1"), new XAttribute("FontSize", "12"),
                        new XAttribute("Padding", "8,2"), new XAttribute("Focusable", "False"),
                        Text("Content", copy), Text("ToolTip", copyTip))),
                AdvisorMarkdown.Render(exchange.Answer)));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Label(string value) => Element("TextBlock", Text("Text", value),
        new XAttribute("FontWeight", "Bold"), new XAttribute("FontSize", "14"),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("Margin", "0,0,0,5"));
    private static XElement Plain(string value) => Element("TextBlock", Text("Text", value),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("FontSize", "14"), new XAttribute("Margin", "0,0,0,8"));
}
