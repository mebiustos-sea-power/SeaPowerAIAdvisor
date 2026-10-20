using System.Globalization;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorUnitBadge
{
    internal static string Caption(int id, string name)
    {
        var number = id.ToString(CultureInfo.InvariantCulture);
        return name == "Contact " + number ? number : name;
    }

    internal static XElement Render(int id, string name, AdvisorUnitLinks? links)
    {
        var label = Element("Run", Text("Text", Caption(id, name)));
        return Element("Border", new XAttribute("MinHeight", "22"), new XAttribute("CornerRadius", "3"),
            new XAttribute("Background", "{DynamicResource AdvisorConversationBackgroundBrush}"),
            new XAttribute("BorderBrush", "#607080"), new XAttribute("BorderThickness", "1"),
            new XAttribute("Padding", "6,1"), new XAttribute("Margin", "0,0,6,2"),
            Element("TextBlock", new XAttribute("FontSize", "12"), new XAttribute("MaxWidth", "220"),
                new XAttribute("TextTrimming", "CharacterEllipsis"), new XAttribute("VerticalAlignment", "Center"),
                links?.BadgeReference(id, label) ?? label));
    }
}
