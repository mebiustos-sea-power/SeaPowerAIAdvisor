using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using SeaPowerAIAdvisor;

internal static class CoordinateLinkTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal static void Run(Action<string, Action> check)
    {
        check("Coordinate links retain signed latitude and longitude across locales while preserving labels, other links and raw history", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                const string answer = "[**集合地点**](map:26.5,128.2) / [南西](MAP:-12.25,-45.5) / [原点](map:0,0) / [北端](map:90,180) / [南端](map:-90,-180) / [P-3C](unit:own_unit_1) / [RIM-66E](weapon:rim)";
                var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Position?"));
                turn.SetUnitReferences(new AdvisorUnitReferences(new[] { ("own_unit_1", 123) })); turn.Complete(answer);
                var coordinates = new AdvisorCoordinateLinks();
                var units = new AdvisorUnitLinks(_ => true, Array.Empty<(int, string)>());
                var weapons = new AdvisorWeaponLinks(key => key == "rim");
                var root = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Adviser", "empty",
                    links: units, weaponLinks: weapons, coordinateLinks: coordinates)).Root!;
                Require(coordinates.Links.Select(link => (link.Latitude, link.Longitude)).SequenceEqual(new[]
                    { (26.5, 128.2), (-12.25, -45.5), (0d, 0d), (90d, 180d), (-90d, -180d) }),
                    "A coordinate changed culture, order, sign or boundary values");
                Require(coordinates.Links.Select(link => link.Name).Distinct().Count() == 5 && units.Links.Count == 1 &&
                    units.Links[0].UnitId == 123 && weapons.Links.Count == 1, "Coordinate links collided with unit or weapon actions");
                var anchors = root.Descendants(Ns + "Hyperlink").ToArray();
                Require(anchors.Length == 7 && anchors[0].Descendants().Any(e => (string?)e.Attribute("FontWeight") == "Bold") &&
                    !anchors.Any(e => e.Descendants(Ns + "Hyperlink").Any()), "Coordinate labels lost emphasis or acquired nested actions");
                Require(!Visible(root).Contains("map:", StringComparison.OrdinalIgnoreCase) && chat.Exchanges[0].Answer == answer,
                    "A hidden destination leaked into the display or the original answer was rewritten");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        check("Invalid, unsupported and literal coordinate references remain noninteractive without unit fallback or external navigation", () =>
        {
            var destinations = new[] { "", "26.5", "26.5,128.2,0", "91,0", "0,181", "-91,0", "0,-181", "NaN,0", "0,Infinity", "1e999,0", "26N,128E", "26,128?x=1", "//example.invalid" };
            var answer = string.Join(" / ", destinations.Select(destination => "[7008](map:" + destination + ")")) +
                " / 7008 / `[座標](map:1,2)` / ![座標](map:3,4) / [site](https://example.invalid)\n\n```text\n[座標](map:5,6)\n```";
            var chat = new AdvisorConversation(); chat.TryAdd("Check coordinates", answer, out _);
            foreach (var supported in new[] { false, true })
            {
                var coordinates = supported ? new AdvisorCoordinateLinks() : null;
                var units = new AdvisorUnitLinks(id => id == 7008);
                var root = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Adviser", "empty",
                    links: units, coordinateLinks: coordinates)).Root!;
                Require((coordinates?.Links.Count ?? 0) == 0 && units.Links.Count == 1 && root.Descendants(Ns + "Hyperlink").Count() == 1,
                    "An invalid or literal coordinate became an action or rebound to a track number");
                Require(Visible(root).Contains("[座標](map:1,2)") && Visible(root).Contains("[座標](map:5,6)") &&
                    Visible(root).Contains("https://example.invalid") && !root.Descendants(Ns + "Image").Any() && chat.Exchanges[0].Answer == answer,
                    "A code example, image, external reference or original answer was altered");
            }
            var plain = AdvisorMarkdown.Render("[集合地点](map:26.5,128.2)");
            Require(Visible(plain) == "集合地点" && !plain.Descendants(Ns + "Hyperlink").Any(),
                "A view without map navigation acquired a coordinate action");
        });
    }
    private static string Visible(XElement element) => string.Concat(element.DescendantsAndSelf().Attributes("Text").Select(a =>
        a.Value.StartsWith("{}", StringComparison.Ordinal) ? a.Value.Substring(2) : a.Value));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
