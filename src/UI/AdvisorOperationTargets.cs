using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Render selected execution targets from proposal data; command descriptions remain opaque.
internal static class AdvisorOperationTargets
{
    internal static XElement Render(IEnumerable<AdvisorOperationPlan> plans, AdvisorText text, Func<int, string?> unitName)
    {
        var selected = plans.ToArray();
        var policies = selected.SelectMany(p => p.ReservationUpdates.Cast<JObject>())
            .ToLookup(p => (string)p["target_ref"]!, StringComparer.Ordinal);
        var rows = Element("StackPanel");
        foreach (var target in selected.SelectMany(p => p.Orders).Select(o => o.Target).Distinct(StringComparer.Ordinal))
        {
            var row = Element("WrapPanel", new XAttribute("Margin", "0,0,0,4"),
                Element("TextBlock", Text("Text", Name(target, unitName)), new XAttribute("FontSize", "14"),
                    new XAttribute("FontWeight", "Bold"), new XAttribute("TextWrapping", "Wrap"), new XAttribute("MaxWidth", "280"),
                    new XAttribute("VerticalAlignment", "Center"), new XAttribute("Margin", "0,0,8,0")));
            foreach (var placement in policies[target].Select(p => (string)p["placement"]!).Distinct(StringComparer.Ordinal))
            {
                var (label, brush) = placement switch
                {
                    "append" => ("Append", "AdvisorReservationAppendBrush"),
                    "replace" => ("Replace", "AdvisorReservationReplaceBrush"),
                    _ => throw new InvalidOperationException("An approval target has an invalid reservation policy.")
                };
                row.Add(Element("Border", new XAttribute("Background", "{DynamicResource " + brush + "}"),
                    new XAttribute("CornerRadius", "3"), new XAttribute("Padding", "6,1"), new XAttribute("Margin", "0,0,6,2"),
                    new XAttribute("VerticalAlignment", "Center"), Element("TextBlock", Text("Text", label),
                        new XAttribute("FontSize", "12"), new XAttribute("FontWeight", "Bold"), new XAttribute("Foreground", "#FFFFFF"))));
            }
            rows.Add(row);
        }
        if (!rows.HasElements) rows.Add(Element("TextBlock", Text("Text", text["operation.no_targets"]), new XAttribute("FontSize", "14")));
        return Element("StackPanel",
            Element("TextBlock", Text("Text", text["operation.targets"]), new XAttribute("FontSize", "14"),
                new XAttribute("FontWeight", "Bold"), new XAttribute("Margin", "0,0,0,6")),
            Element("ScrollViewer", new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XAttribute("HorizontalScrollBarVisibility", "Disabled"), new XAttribute("MaxHeight", "80"), rows));
    }
    private static string Name(string target, Func<int, string?> unitName)
    {
        if (!target.StartsWith("unit:", StringComparison.Ordinal) ||
            !int.TryParse(target.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return target;
        var name = unitName(id);
        return string.IsNullOrWhiteSpace(name) ? target : name + " (" + id.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
