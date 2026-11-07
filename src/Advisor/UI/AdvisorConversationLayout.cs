using System.IO;
using System.Linq;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.Shared.UiMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorConversationLayout
{
    private static readonly XName Name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    // Place the sections before parsing so their bindings share the window's XAML namescope.
    internal static XElement Assemble(Stream source)
    {
        var window = XElement.Load(source, LoadOptions.PreserveWhitespace);
        var pane = window.Elements().Single(element => (string?)element.Attribute(Name) == "ConversationPane");
        var sections = pane.Elements().ToDictionary(element => (string)element.Attribute(Name)!);
        pane.RemoveNodes();

        var sidebar = Element("ScrollViewer",
            new XAttribute("VerticalScrollBarVisibility", "Auto"),
            new XAttribute("HorizontalScrollBarVisibility", "Disabled"),
            Element("StackPanel", new XAttribute("VerticalAlignment", "Top"),
                sections["ConsultationProfile"], sections["ConsultationShortcuts"]));

        var content = sections["ConsultationContent"];
        content.SetAttributeValue("Grid.Column", 2);
        var body = Element("Grid", Tracks("Column", "Width", "150", "16", "*"), sidebar, content);

        var composer = sections["ConsultationComposer"];
        composer.SetAttributeValue("Grid.Row", 1);
        var status = sections["Status"];
        status.SetAttributeValue("Grid.Row", 2);
        pane.Add(Tracks("Row", "Height", "*", "Auto", "Auto"), body, composer, status);
        return window;
    }

    private static XElement Tracks(string axis, string dimension, params string[] sizes) =>
        Element($"Grid.{axis}Definitions", sizes.Select(size => Element($"{axis}Definition", new XAttribute(dimension, size))));
}
