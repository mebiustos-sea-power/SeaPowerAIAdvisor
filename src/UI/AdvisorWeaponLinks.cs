using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Stable encyclopedia keys need no consultation-local lookup or name-matching fallback.
internal sealed class AdvisorWeaponLinks
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Func<string, bool> known;
    private readonly List<(string Name, string ReferenceKey)> links = new();
    internal IReadOnlyList<(string Name, string ReferenceKey)> Links => links;

    internal AdvisorWeaponLinks(Func<string, bool> known) { this.known = known; }

    internal XElement Reference(string key, XElement label)
    {
        if (string.IsNullOrWhiteSpace(key) || !known(key)) return label;
        var name = "AdvisorWeapon" + links.Count.ToString(CultureInfo.InvariantCulture);
        links.Add((name, key));
        return Element("Hyperlink", new XAttribute(Xaml + "Name", name),
            new XAttribute("Style", "{DynamicResource AdvisorWeaponLinkStyle}"),
            new XAttribute("TextDecorations", "Underline"), new XAttribute("Focusable", "False"), label);
    }
}
