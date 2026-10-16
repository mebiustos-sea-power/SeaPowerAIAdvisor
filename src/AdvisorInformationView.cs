using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Noesis;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Owns only the unsaved selection UI. The saved immutable policy belongs to AdvisorOptions.
internal sealed class AdvisorInformationView
{
    private readonly StackPanel host;
    private readonly List<(InformationGroup Group, CheckBox Box)> groups = new();
    private readonly List<(InformationKind Kind, CheckBox Box)> items = new();
    internal AdvisorInformation Value { get; private set; } = AdvisorInformation.All;
    internal AdvisorInformationView(StackPanel host, Action<Action> guard)
    {
        this.host = host;
        var ns = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml";
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", ns));
        var index = 0;
        foreach (var group in AdvisorInformation.Groups)
        {
            root.Add(Box("InformationGroup" + index++, group.TextKey, "0,12,0,6", true));
            foreach (var kind in group.Items)
            {
                var depth = 1;
                var parent = AdvisorInformation.Item(kind).Requires;
                while (parent.HasValue) { depth++; parent = AdvisorInformation.Item(parent.Value).Requires; }
                root.Add(Box("InformationItem" + kind, AdvisorInformation.Item(kind).TextKey, (20 * depth) + ",4,0,4", false));
            }
        }
        var view = GUI.ParseXaml(root.ToString(SaveOptions.DisableFormatting)) as FrameworkElement
            ?? throw new InvalidOperationException("The information selection view is invalid.");
        host.Children.Add(view);
        index = 0;
        foreach (var group in AdvisorInformation.Groups)
        {
            var box = Find(view, "InformationGroup" + index++);
            groups.Add((group, box));
            box.Click += (_, _) => guard(() => { Value = Value.With(group, Value.State(group) != true); Refresh(); });
            foreach (var kind in group.Items)
            {
                var item = Find(view, "InformationItem" + kind);
                items.Add((kind, item));
                item.Click += (_, _) => guard(() => { Value = Value.With(kind, item.IsChecked == true); Refresh(); });
            }
        }
        Reset(null);
    }
    internal void Reset(AdvisorInformation? value)
    {
        Value = value ?? AdvisorInformation.All;
        host.IsEnabled = value != null;
        Refresh();
    }
    internal void Focus() => items[0].Box.Focus();
    private void Refresh()
    {
        foreach (var entry in groups) entry.Box.IsChecked = Value.State(entry.Group);
        foreach (var entry in items)
        {
            entry.Box.IsChecked = Value.Allows(entry.Kind);
            entry.Box.IsEnabled = Value.CanEdit(entry.Kind);
        }
    }
    private static XElement Box(string name, string key, string margin, bool group) => Element("CheckBox",
        new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", name),
        new XAttribute("Margin", margin), new XAttribute("IsThreeState", group ? "True" : "False"),
        new XAttribute("HorizontalContentAlignment", "Stretch"),
        Element("TextBlock", new XAttribute("Text", "{DynamicResource Advisor." + key + "}"),
            new XAttribute("TextWrapping", "Wrap"), new XAttribute("FontWeight", group ? "Bold" : "Normal")));
    private static CheckBox Find(FrameworkElement root, string name) => root.FindName(name) as CheckBox
        ?? throw new InvalidOperationException("An information checkbox is missing: " + name);
}
