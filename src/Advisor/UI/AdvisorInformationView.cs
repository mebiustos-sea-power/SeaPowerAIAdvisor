using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Noesis;
using static SeaPowerAIAdvisor.Shared.UiMarkup;

namespace SeaPowerAIAdvisor;

// Information selection and read-only coverage UI. The saved immutable policy belongs to AdvisorOptions.
internal sealed class AdvisorInformationView
{
    private readonly StackPanel host;
    private readonly List<(InformationGroup Group, CheckBox Box)> groups = new();
    private readonly List<(InformationKind Kind, CheckBox Box)> items = new();
    internal AdvisorInformation Value { get; private set; } = AdvisorInformation.Default;
    internal AdvisorInformationView(StackPanel host, AdvisorText text, Action<Action> guard)
    {
        this.host = host;
        var ns = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml";
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", ns));
        var index = 0;
        bool? section = null;
        foreach (var group in AdvisorInformation.Groups)
        {
            if (section != group.Battle)
            {
                root.Add(Element("TextBlock",
                    new XAttribute("Text", "{DynamicResource Advisor.information." + (group.Battle ? "mission" : "campaign") + "}"),
                    new XAttribute("FontSize", "16"), new XAttribute("FontWeight", "Bold"),
                    new XAttribute("Margin", section.HasValue ? "0,24,0,0" : "0,16,0,0")));
                root.Add(Element("TextBlock",
                    new XAttribute("Text", "{DynamicResource Advisor.information." + (group.Battle ? "mission_description" : "campaign_description") + "}"),
                    new XAttribute("FontSize", "12"), new XAttribute("TextWrapping", "Wrap"),
                    new XAttribute("Opacity", "0.7"), new XAttribute("Margin", "0,6,0,0")));
                if (group.Battle)
                {
                    var fixedGroup = Element("StackPanel", new XAttribute(ns + "Name", "InformationFixed"));
                    fixedGroup.Add(FixedBox("InformationFixedGroup", "information.mission_basic", "0,12,0,6", true));
                    foreach (var key in new[] { "mission_context", "public_objectives", "mission_map_areas" })
                        fixedGroup.Add(FixedBox("InformationFixed_" + key, "information." + key, "20,4,0,4", false));
                    root.Add(fixedGroup);
                }
                else
                    root.Add(Element("TextBlock",
                        new XAttribute("Text", "{DynamicResource Advisor.information.mandatory}"),
                        new XAttribute("FontSize", "12"), new XAttribute("TextWrapping", "Wrap"),
                        new XAttribute("Opacity", "0.7"), new XAttribute("Margin", "0,6,0,0")));
                section = group.Battle;
            }
            root.Add(Box("InformationGroup" + index++, group.TextKey, "0,12,0,6", true));
            foreach (var kind in group.Items)
            {
                var depth = 1;
                var parent = AdvisorInformation.Item(kind).Requires;
                while (parent.HasValue) { depth++; parent = AdvisorInformation.Item(parent.Value).Requires; }
                var item = AdvisorInformation.Item(kind);
                var box = Box("InformationItem" + kind, item.TextKey, (20 * depth) + ",4,0,4", false);
                if (text.Contains(item.TextKey + "_tip"))
                    box.SetAttributeValue("ToolTip", "{DynamicResource Advisor." + item.TextKey + "_tip}");
                root.Add(box);
            }
        }
        var view = GUI.ParseXaml(root.ToString(SaveOptions.DisableFormatting)) as FrameworkElement
            ?? throw new InvalidOperationException("The information selection view is invalid.");
        host.Children.Add(view);
        var fixedView = view.FindName("InformationFixed") as FrameworkElement
            ?? throw new InvalidOperationException("The fixed information group is missing.");
        fixedView.Loaded += (_, _) => guard(() => UiResources.ApplyDisabledCheckBoxBrushes(fixedView));
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
        Value = value ?? AdvisorInformation.Default;
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
    // Fixed coverage is explanatory UI, not an editable permission or saved setting.
    private static XElement FixedBox(string name, string key, string margin, bool group)
    {
        var box = Box(name, key, margin, group);
        box.SetAttributeValue("IsChecked", "True");
        box.SetAttributeValue("IsEnabled", "False");
        box.Element(XName.Get("TextBlock", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"))!
            .SetAttributeValue("Foreground", "{DynamicResource Brush.Foreground.Highlight}");
        return box;
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
