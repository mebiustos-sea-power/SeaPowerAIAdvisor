using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SeaPowerAIAdvisor;

internal enum AdvisorTextSize { Small, Standard, Large }

// Scale only locally generated conversation text. Layout dimensions and message content stay unchanged.
internal static class AdvisorTypography
{
    internal const AdvisorTextSize DefaultSize = AdvisorTextSize.Standard;
    internal static float FontSize(AdvisorTextSize size, double standard) => (float)Math.Round(standard * (size switch
    {
        AdvisorTextSize.Small => 0.85,
        AdvisorTextSize.Standard => 1.0,
        AdvisorTextSize.Large => 1.2,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    }), MidpointRounding.AwayFromZero);

    internal static void Apply(XElement root, AdvisorTextSize size)
    {
        foreach (var attribute in root.DescendantsAndSelf().Attributes()
            .Where(a => a.Name.LocalName is "FontSize" or "TextElement.FontSize"))
            attribute.Value = FontSize(size, double.Parse(attribute.Value, CultureInfo.InvariantCulture))
                .ToString(CultureInfo.InvariantCulture);
    }
}
