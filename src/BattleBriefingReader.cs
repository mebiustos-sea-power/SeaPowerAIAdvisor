using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SeaPowerAIAdvisor;

// Resolves only the public left briefing pane, using the game's language and file-selection rules.
internal static class BattleBriefingReader
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const int MaximumCharacters = 2_097_152;

    internal static (string Text, bool FromBase) Read(string relativeMission, string language,
        Func<string, string, string, string> setting, Func<string, string?> resolveFile,
        Func<string, string> readFile, Func<BattleBriefingTemplate?>? baseTemplate = null)
    {
        if (string.IsNullOrWhiteSpace(language)) language = "en";
        var pane = ResolvePane(relativeMission, language, setting, resolveFile);
        if (pane.Path != null) return (Text(readFile(pane.Path)), false);

        // Saved generated missions may retain paths into a temporary directory that no longer exists.
        // Their BaseFile identifies the public source mission; never infer it from a mission number.
        var original = baseTemplate?.Invoke();
        if (original != null)
        {
            var sourcePane = ResolvePane(original.RelativeMission, language, original.Setting, resolveFile);
            if (sourcePane.Path == null)
            {
                if (!pane.Configured && !sourcePane.Configured) return ("", false);
                throw new FileNotFoundException("The original mission's public briefing pane is missing.", original.RelativeMission);
            }
            var text = Text(readFile(sourcePane.Path));
            foreach (var token in TaskForceTokens)
                if (text.Contains(token, StringComparison.Ordinal))
                    throw new NotSupportedException("The original briefing requires unavailable generation-time context: " + token);
            return (text, true);
        }
        if (pane.Configured)
            throw new FileNotFoundException("The configured public briefing pane is missing.");
        return ("", false); // Missions without an XML pane continue to use their event-log briefing.
    }

    // These are the tokens replaced by the game's CreateTaskForceTokens/ReplaceBriefingTokens.
    // Current unit names cannot establish the historical roster or selected unit used at generation.
    private static readonly string[] TaskForceTokens =
    {
        "{TaskForceName}", "{FlagshipName}", "{SelectedUnitName}", "{TaskForceFlagshipName}",
        "{TaskForceFlagshipFullName}", "{TaskForceVesselSummaryRuns}"
    };

    private static (string? Path, bool Configured) ResolvePane(string relativeMission, string language,
        Func<string, string, string, string> setting, Func<string, string?> resolveFile)
    {
        var defaultDirectory = string.IsNullOrEmpty(relativeMission) ? "" : relativeMission.Replace(".ini", "") + "_briefing";
        var directory = setting("Language_" + language, "MissionBriefingAssetsDirectory", defaultDirectory);
        if (string.IsNullOrEmpty(directory)) directory = setting("Language_en", "MissionBriefingAssetsDirectory", defaultDirectory);
        var selected = setting("Language_" + language, "MissionBriefingLeftPane", directory + "/BriefingText_" + language + ".xml");
        var path = string.IsNullOrEmpty(selected) ? null : resolveFile(selected);
        if (string.IsNullOrEmpty(path))
        {
            selected = setting("Language_en", "MissionBriefingLeftPane", directory + "/BriefingText_en.xml");
            path = string.IsNullOrEmpty(selected) ? null : resolveFile(selected);
        }
        var configured = !string.IsNullOrEmpty(setting("Language_" + language, "MissionBriefingLeftPane", "")) ||
            !string.IsNullOrEmpty(setting("Language_en", "MissionBriefingLeftPane", ""));
        return (string.IsNullOrEmpty(path) ? null : path, configured);
    }

    internal static string Combine(string legacy, string xml) => string.IsNullOrWhiteSpace(xml) || legacy.Trim() == xml.Trim()
        ? legacy : string.IsNullOrWhiteSpace(legacy) ? xml : legacy + "\n\n" + xml;

    internal static string Text(string xml)
    {
        if (xml.Length > MaximumCharacters) throw new InvalidDataException("The public briefing is too large.");
        xml = Regex.Replace(xml, @"^\s*<\?xml[^?]*\?>", "");
        // XML normally folds attribute newlines; the game's rich briefing loader preserves them.
        xml = Regex.Replace(xml, "(?<prefix>\\b(?:Text|Content)\\s*=\\s*)(?<quote>[\"'])(?<value>[\\s\\S]*?)\\k<quote>",
            m => m.Groups["prefix"].Value + m.Groups["quote"].Value +
                m.Groups["value"].Value.Replace("\r\n", "&#xA;").Replace("\r", "&#xA;").Replace("\n", "&#xA;") + m.Groups["quote"].Value);
        using var source = new StringReader("<Grid xmlns=\"" + Presentation +
            "\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:noesis=\"clr-namespace:NoesisGUIExtensions;assembly=Noesis.GUI.Extensions\">" + xml + "</Grid>");
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters + 512 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        // Static extraction must not guess the result of styles, triggers or data bindings.
        if (document.Descendants().Any(e => new[] { "Style", "ControlTemplate", "DataTemplate", "Storyboard" }.Contains(e.Name.LocalName) ||
            e.Name.LocalName.EndsWith("Trigger", StringComparison.Ordinal) ||
            new[] { ".Visibility", ".Opacity", ".Clip", ".RenderTransform", ".LayoutTransform" }.Any(p => e.Name.LocalName.EndsWith(p, StringComparison.Ordinal))))
            throw new NotSupportedException("The public briefing uses conditional presentation.");
        var blocks = new List<string>();
        Visit(document.Root!, blocks);
        return string.Join("\n", blocks);
    }

    private static bool Visible(XElement element)
    {
        var visibility = element.Attribute("Visibility")?.Value;
        if (visibility != null && visibility != "Visible")
        {
            if (visibility == "Hidden" || visibility == "Collapsed") return false;
            throw new NotSupportedException("The public briefing has unresolved visibility.");
        }
        foreach (var property in new[] { "Opacity", "Width", "Height", "MaxWidth", "MaxHeight", "FontSize" })
        {
            var value = element.Attribute(property)?.Value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 0) return false;
            if (value != null && property != "FontSize" && value.StartsWith("{", StringComparison.Ordinal))
                throw new NotSupportedException("The public briefing has unresolved layout or opacity.");
        }
        if (element.Attribute("Style") != null)
            throw new NotSupportedException("The public briefing uses an unresolved style.");
        return true;
    }

    private static void Visit(XElement element, List<string> blocks)
    {
        if (!Visible(element)) return;
        var name = element.Name.LocalName;
        if (name.EndsWith(".Resources", StringComparison.Ordinal) || name.EndsWith(".DataContext", StringComparison.Ordinal) ||
            name.EndsWith(".RowDefinitions", StringComparison.Ordinal) || name.EndsWith(".ColumnDefinitions", StringComparison.Ordinal) || name == "Image") return;
        if (element.Name.NamespaceName != Presentation) throw new NotSupportedException("Unsupported public briefing element: " + name);
        if (name == "TextBlock" || name == "Label")
        {
            var text = new StringBuilder();
            var literal = element.Attribute(name == "Label" ? "Content" : "Text")?.Value;
            if (literal != null) text.Append(Literal(literal));
            else Inline(element, text);
            var block = string.Join("\n", text.ToString().Split('\n').Select(line => line.Trim())).Trim();
            if (block.Length > 0) blocks.Add(block);
            return;
        }
        if (!new[] { "Grid", "StackPanel", "WrapPanel", "DockPanel", "Canvas", "Border", "ScrollViewer",
            "Grid.Children", "StackPanel.Children", "Border.Child", "ScrollViewer.Content" }.Contains(name))
            throw new NotSupportedException("Unsupported public briefing element: " + name);
        foreach (var child in element.Elements()) Visit(child, blocks);
    }

    private static void Inline(XElement parent, StringBuilder text)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText raw) { text.Append(Regex.Replace(raw.Value, @"\s+", " ")); continue; }
            if (!(node is XElement child) || !Visible(child)) continue;
            var name = child.Name.LocalName;
            if (child.Name.NamespaceName != Presentation) throw new NotSupportedException("Unsupported public briefing inline.");
            if (name == "LineBreak") { text.Append('\n'); continue; }
            if (name.EndsWith(".Resources", StringComparison.Ordinal)) continue;
            if (!new[] { "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "TextBlock.Inlines" }.Contains(name))
                throw new NotSupportedException("Unsupported public briefing inline: " + name);
            var literal = child.Attribute("Text")?.Value;
            if (literal != null) text.Append(Literal(literal));
            else Inline(child, text);
        }
    }

    private static string Literal(string text)
    {
        if (text.StartsWith("{}", StringComparison.Ordinal)) return text.Substring(2);
        if (text.StartsWith("{", StringComparison.Ordinal)) throw new NotSupportedException("The public briefing has unresolved text.");
        return text;
    }
}

// A short-lived view of the source mission's public briefing settings, used only during capture.
internal sealed class BattleBriefingTemplate
{
    internal string RelativeMission { get; }
    internal Func<string, string, string, string> Setting { get; }
    internal BattleBriefingTemplate(string relativeMission, Func<string, string, string, string> setting)
    { RelativeMission = relativeMission; Setting = setting; }
}
