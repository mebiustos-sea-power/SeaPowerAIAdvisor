using System;
using System.Xml.Linq;

namespace SeaPowerAIAdvisor;

// Game reference schemes belong to the adviser; the shared renderer knows no units, weapons or map coordinates.
internal static class AdvisorMarkdown
{
    internal static XElement Render(string markdown, Func<string, XElement, XElement>? unitLink = null,
        Func<string, XElement, XElement>? weaponLink = null, Func<string, XElement, XElement>? coordinateLink = null) =>
        MarkdownRenderer.Render(markdown, (destination, label) =>
            destination.StartsWith("unit:", StringComparison.OrdinalIgnoreCase) ? Reference(destination.Substring(5), label, unitLink)
            : destination.StartsWith("weapon:", StringComparison.OrdinalIgnoreCase) ? Reference(destination.Substring(7), label, weaponLink)
            : destination.StartsWith("map:", StringComparison.OrdinalIgnoreCase) ? Reference(destination.Substring(4), label, coordinateLink) : null);

    private static XElement Reference(string target, XElement label, Func<string, XElement, XElement>? render)
    {
        // Unresolved local labels must not fall through to unit-name or track-number matching.
        label.AddAnnotation(new UiMarkup.LocalReference());
        return render?.Invoke(target, label) ?? label;
    }
}
