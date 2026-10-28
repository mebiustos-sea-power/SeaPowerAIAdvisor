using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.Shared.UiMarkup;

namespace SeaPowerAIAdvisor;

// Reuse stable encyclopedia keys; name completion is limited to each rendered answer.
internal sealed class AdvisorWeaponLinks
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Func<string, bool> known;
    private readonly List<(string Name, string ReferenceKey)> links = new();
    internal IReadOnlyList<(string Name, string ReferenceKey)> Links => links;
    private sealed class ReferenceKey
    {
        internal string Value { get; }
        internal ReferenceKey(string value) { Value = value; }
    }

    internal AdvisorWeaponLinks(Func<string, bool> known) { this.known = known; }

    internal XElement Reference(string key, XElement label) =>
        string.IsNullOrWhiteSpace(key) || !known(key) ? label : Link(key, label);

    internal XElement Decorate(XElement content)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in content.DescendantsAndSelf())
        {
            var reference = element.Annotation<ReferenceKey>();
            if (reference == null) continue;
            var name = string.Concat(element.DescendantsAndSelf().Attributes("Text").Select(a => Literal(a.Value)));
            if (name.Length > 0 && !names.ContainsKey(name)) names.Add(name, reference.Value);
        }
        if (names.Count == 0) return content;
        var patterns = names.Keys.OrderByDescending(name => name.Length).ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => (IsWord(name[0]) ? @"(?<![A-Za-z0-9_-])" : "") + Regex.Escape(name) +
                (IsWord(name[name.Length - 1]) ? @"(?![A-Za-z0-9_-])" : ""));
        var mentions = new Regex(string.Join("|", patterns), RegexOptions.CultureInvariant);
        var fields = content.DescendantsAndSelf().Where(e => e.Name.LocalName is "Run" or "TextBlock")
            .Where(e => !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Hyperlink" ||
                a.Annotation<LocalReference>() != null || a.Annotation<LiteralContent>() != null))
            .Select(e => e.Attribute("Text")).Where(a => a != null).Cast<XAttribute>().ToArray();
        foreach (var field in fields)
        {
            var value = Literal(field.Value);
            var pieces = new List<XElement>();
            var offset = 0;
            foreach (Match match in mentions.Matches(value))
            {
                if (match.Index > offset) pieces.Add(Element("Run", Text("Text", value.Substring(offset, match.Index - offset))));
                pieces.Add(Link(names[match.Value], Element("Run", Text("Text", match.Value))));
                offset = match.Index + match.Length;
            }
            if (pieces.Count == 0) continue;
            if (offset < value.Length) pieces.Add(Element("Run", Text("Text", value.Substring(offset))));
            var owner = field.Parent!;
            field.Remove();
            if (owner.Name.LocalName == "Run") owner.ReplaceWith(Element("Span", owner.Attributes().ToArray(), pieces));
            else owner.Add(pieces);
        }
        return content;
    }

    private XElement Link(string key, XElement label)
    {
        var name = "AdvisorWeapon" + links.Count.ToString(CultureInfo.InvariantCulture);
        links.Add((name, key));
        var link = Element("Hyperlink", new XAttribute(Xaml + "Name", name),
            new XAttribute("Style", "{DynamicResource AdvisorWeaponLinkStyle}"),
            new XAttribute("TextDecorations", "Underline"), new XAttribute("Focusable", "False"), label);
        link.AddAnnotation(new ReferenceKey(key));
        return link;
    }

    private static string Literal(string value) => value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value;
    private static bool IsWord(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
}
