using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

// Map destinations carry latitude and longitude; displayed labels remain ordinary Markdown.
internal sealed class AdvisorCoordinateLinks
{
    private readonly List<(string Name, double Latitude, double Longitude)> links = new();
    internal IReadOnlyList<(string Name, double Latitude, double Longitude)> Links => links;

    internal XElement Reference(string destination, XElement label) => Reference(destination, label, false);
    internal XElement BadgeReference(string destination, XElement label) => Reference(destination, label, true);
    private XElement Reference(string destination, XElement label, bool badge)
    {
        var parts = destination.Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
            double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < -90 || latitude > 90 ||
            double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < -180 || longitude > 180)
            return label;
        var name = "AdvisorCoordinate" + links.Count.ToString(CultureInfo.InvariantCulture);
        links.Add((name, latitude, longitude));
        return Element("Hyperlink",
            new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", name),
            new XAttribute("Style", badge ? "{DynamicResource AdvisorPlainLinkStyle}" : "{DynamicResource AdvisorWeaponLinkStyle}"),
            new XAttribute("TextDecorations", badge ? "None" : "Underline"), new XAttribute("Focusable", "False"), label);
    }
}
