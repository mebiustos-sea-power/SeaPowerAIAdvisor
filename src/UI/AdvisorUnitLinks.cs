using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Decorate only generated text; the original question, Markdown and AI history stay unchanged.
internal sealed class AdvisorUnitLinks
{
    private const string TrackNumber = @"(?<![0-9])7[0-9]{3}(?![0-9])";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Func<int, bool> known;
    private readonly Dictionary<string, HashSet<int>> ownNames = new(StringComparer.Ordinal);
    private readonly Regex mentions;
    private readonly bool linkOwnUnits;
    private sealed class ExplicitReference { }
    private readonly List<(string Name, int UnitId)> links = new();
    internal IReadOnlyList<(string Name, int UnitId)> Links => links;

    internal AdvisorUnitLinks(Func<int, bool> known, IEnumerable<(int Id, string Name)>? ownUnits = null, bool linkContacts = true, bool linkOwnUnits = true)
    {
        this.known = known; this.linkOwnUnits = linkOwnUnits;
        foreach (var unit in (linkOwnUnits ? ownUnits : null) ?? Array.Empty<(int, string)>())
        {
            var name = unit.Name.Trim();
            AddName(name, unit.Id);
            for (var i = name.Length - 1; i >= 0; i--)
                if (char.IsWhiteSpace(name[i]))
                {
                    AddName(name.Substring(0, i).TrimEnd(), unit.Id);
                    break;
                }
        }
        // Include ambiguous names in the matcher so their shorter substrings cannot become links.
        var patterns = ownNames.Keys.OrderByDescending(name => name.Length).ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => (IsWord(name[0]) ? @"(?<![A-Za-z0-9_])" : "") + Regex.Escape(name) +
                (IsWord(name[name.Length - 1]) ? @"(?![A-Za-z0-9_])" : ""));
        mentions = new Regex(string.Join("|", patterns.Append(linkContacts ? TrackNumber : "(?!)")), RegexOptions.CultureInvariant);
    }

    internal XElement Reference(string id, XElement label, AdvisorUnitReferences references)
    {
        // An explicit destination must never be retargeted by the text-matching fallback.
        label.AddAnnotation(new ExplicitReference());
        var target = references.Resolve(id);
        return linkOwnUnits && target.HasValue && known(target.Value) ? Link(target.Value, true, label) : label;
    }

    internal XElement Decorate(XElement content)
    {
        var fields = content.DescendantsAndSelf().Where(e => e.Name.LocalName is "Run" or "TextBlock")
            .Where(e => !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Hyperlink" || a.Annotation<ExplicitReference>() != null))
            .Select(e => e.Attribute("Text")).Where(a => a != null).Cast<XAttribute>().ToArray();
        foreach (var field in fields)
        {
            var value = field.Value.StartsWith("{}", StringComparison.Ordinal) ? field.Value.Substring(2) : field.Value;
            var pieces = new List<XElement>();
            var offset = 0;
            foreach (Match match in mentions.Matches(value))
            {
                int id;
                var own = ownNames.TryGetValue(match.Value, out var candidates);
                if (own)
                {
                    if (candidates!.Count != 1) continue;
                    id = candidates.Single();
                }
                else if (!int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                if (!known(id)) continue;
                if (match.Index > offset) pieces.Add(Element("Run", Text("Text", value.Substring(offset, match.Index - offset))));
                pieces.Add(Link(id, own, Element("Run", Text("Text", match.Value))));
                offset = match.Index + match.Length;
            }
            if (pieces.Count == 0) continue;
            if (offset < value.Length) pieces.Add(Element("Run", Text("Text", value.Substring(offset))));
            var owner = field.Parent!;
            field.Remove();
            if (owner.Name.LocalName == "Run")
                owner.ReplaceWith(Element("Span", owner.Attributes().ToArray(), pieces));
            else owner.Add(pieces);
        }
        return content;
    }

    private XElement Link(int id, bool own, XElement label)
    {
        var name = "AdvisorUnit" + links.Count.ToString(CultureInfo.InvariantCulture);
        links.Add((name, id));
        return Element("Hyperlink", new XAttribute(Xaml + "Name", name),
            new XAttribute("Style", own ? "{DynamicResource AdvisorOwnUnitLinkStyle}" : "{DynamicResource AdvisorTrackLinkStyle}"),
            new XAttribute("TextDecorations", "None"), new XAttribute("Focusable", "False"), label);
    }

    private void AddName(string name, int id)
    {
        if (name.Length == 0) return;
        if (!ownNames.TryGetValue(name, out var ids)) ownNames.Add(name, ids = new HashSet<int>());
        ids.Add(id);
    }

    private static bool IsWord(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}
