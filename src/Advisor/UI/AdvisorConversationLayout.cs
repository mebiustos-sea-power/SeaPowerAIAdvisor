using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.Shared.UiMarkup;
using Section = SeaPowerAIAdvisor.AdvisorLayoutDefinition.Section;

namespace SeaPowerAIAdvisor;

internal static class AdvisorConversationLayout
{
    private static readonly XName Name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    // Place the sections before parsing so their bindings share the window's XAML namescope.
    internal static XElement Assemble(Stream source, AdvisorLayoutDefinition layout)
    {
        var window = XElement.Load(source, LoadOptions.PreserveWhitespace);
        var pane = window.Elements().Single(element => (string?)element.Attribute(Name) == "ConversationPane");
        var parts = pane.Elements().ToDictionary(element => (string)element.Attribute(Name)!);
        var sections = new Dictionary<Section, XElement>
        {
            [Section.Profile] = parts["ConsultationProfile"],
            [Section.Shortcuts] = parts["ConsultationShortcuts"],
            [Section.Conversation] = parts["ConsultationContent"],
            [Section.Composer] = parts["ConsultationComposer"],
            [Section.Status] = parts["Status"]
        };
        pane.RemoveNodes();
        pane.Add(Tracks("Row", "Height", layout.Rows.Select(row => row.Height)));
        for (var rowIndex = 0; rowIndex < layout.Rows.Count; rowIndex++)
        {
            var row = RenderRow(layout.Rows[rowIndex], sections);
            if (rowIndex > 0) row.SetAttributeValue("Grid.Row", rowIndex);
            pane.Add(row);
        }
        return window;
    }

    private static XElement RenderRow(AdvisorLayoutDefinition.Row row, IReadOnlyDictionary<Section, XElement> sections)
    {
        if (row.Columns is [{ Width: "*", Sections.Count: > 0 } singleColumn])
            return RenderColumn(singleColumn, sections);

        var grid = Element("Grid", Tracks("Column", "Width", row.Columns.Select(column => column.Width)));
        for (var columnIndex = 0; columnIndex < row.Columns.Count; columnIndex++)
        {
            var column = row.Columns[columnIndex];
            if (column.Sections.Count == 0) continue;
            var content = RenderColumn(column, sections);
            if (columnIndex > 0) content.SetAttributeValue("Grid.Column", columnIndex);
            grid.Add(content);
        }
        return grid;
    }

    private static XElement RenderColumn(AdvisorLayoutDefinition.Column column, IReadOnlyDictionary<Section, XElement> sections)
    {
        if (!column.Scroll && column.Sections.Count == 1) return sections[column.Sections[0]];

        var stack = Element("StackPanel", new XAttribute("VerticalAlignment", "Top"),
            column.Sections.Select(section => sections[section]));
        return column.Scroll
            ? Element("ScrollViewer", new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XAttribute("HorizontalScrollBarVisibility", "Disabled"), stack)
            : stack;
    }

    private static XElement Tracks(string axis, string dimension, IEnumerable<string> sizes) =>
        Element($"Grid.{axis}Definitions", sizes.Select(size => Element($"{axis}Definition", new XAttribute(dimension, size))));
}
