using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Decorate only generated text; the original question, Markdown and AI history stay unchanged.
internal sealed class AdvisorTrackLinks
{
    private static readonly Regex Number = new(@"(?<![0-9])7[0-9]{3}(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Func<int, bool> known;
    private readonly List<(string Name, int Number)> links = new();
    internal IReadOnlyList<(string Name, int Number)> Links => links;

    internal AdvisorTrackLinks(Func<int, bool> known) => this.known = known;

    internal XElement Decorate(XElement content)
    {
        var fields = content.DescendantsAndSelf().Where(e => e.Name.LocalName is "Run" or "TextBlock")
            .Select(e => e.Attribute("Text")).Where(a => a != null).Cast<XAttribute>().ToArray();
        foreach (var field in fields)
        {
            var value = field.Value.StartsWith("{}", StringComparison.Ordinal) ? field.Value.Substring(2) : field.Value;
            var pieces = new List<XElement>();
            var offset = 0;
            foreach (Match match in Number.Matches(value))
            {
                var number = int.Parse(match.Value, CultureInfo.InvariantCulture);
                if (!known(number)) continue;
                if (match.Index > offset) pieces.Add(Element("Run", Text("Text", value.Substring(offset, match.Index - offset))));
                var name = "AdvisorTrack" + links.Count.ToString(CultureInfo.InvariantCulture);
                pieces.Add(Element("Hyperlink", new XAttribute(Xaml + "Name", name),
                    new XAttribute("Style", "{DynamicResource AdvisorTrackLinkStyle}"),
                    new XAttribute("TextDecorations", "None"), new XAttribute("Focusable", "False"),
                    Element("Run", Text("Text", match.Value))));
                links.Add((name, number));
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

}
