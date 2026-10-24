using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class LocationDraftTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal static void Run(Action<string, Action> check)
    {
        check("Draft location labels follow the remaining tail, preserve middle gaps and restart when empty", () =>
        {
            var draft = new AdvisorDraft();
            var a = draft.AddLocation(new AdvisorMapCoordinate(26.5, 127.25), "地点{0}");
            var b = draft.AddLocation(new AdvisorMapCoordinate(-12.25, -45.5), "地点{0}");
            draft.RemoveLocation(a);
            var c = draft.AddLocation(a.Coordinate, "地点{0}");
            Require(a.Label == "地点A" && b.Label == "地点B" && c.Label == "地点C" &&
                draft.Locations.SequenceEqual(new[] { b, c }), "Removing a location renumbered survivors or reused its label");
            draft.RemoveLocation(c);
            var reused = draft.AddLocation(b.Coordinate, "Point {0}");
            Require(reused.Label == "Point C" && draft.Locations.SequenceEqual(new[] { b, reused }),
                "Removing the last location did not reuse its suffix independently of the label language");
            for (var i = 3; i <= 26; i++) draft.AddLocation(a.Coordinate, "地点{0}");
            Require(draft.Locations.Last().Label == "地点AA", "Location labels collided after Z");
            draft.RemoveLocation(draft.Locations.Last());
            Require(draft.AddLocation(b.Coordinate, "Point {0}").Label == "Point AA", "The suffix after Z was not reusable");
            draft.Add(1005, "Orion");
            foreach (var location in draft.Locations.ToArray()) draft.RemoveLocation(location);
            Require(draft.AddLocation(a.Coordinate, "地点{0}").Label == "地点A", "Removing every location did not restart from A");
            draft.Clear();
            Require(draft.AddLocation(b.Coordinate, "Point {0}").Label == "Point A", "A cleared draft retained the previous counter");
        });
        check("Unit and coordinate references freeze together for sending, copying, retrying and request overrides", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var draft = new AdvisorDraft(); draft.Add(1005, "Orion");
                var location = draft.AddLocation(new AdvisorMapCoordinate(-26.125, 127.375), "Point {0}");
                var request = draft.Prepare("Patrol near Point A.");
                var changed = request.WithOperations(false).WithSituationDelivery(AdvisorSituationDelivery.Upfront);
                var chat = new AdvisorConversation(); var turn = chat.Begin(changed); turn.Complete("Acknowledged.");
                draft.RemoveLocation(location); draft.Clear();
                Require(request.Locations.Single() == location && changed.Locations.Single() == location &&
                    changed.Units.Single().Id == 1005 && changed.DisplayBody == "Patrol near Point A." &&
                    changed.Text.Contains("Point A (latitude: -26.125, longitude: 127.375)") &&
                    chat.Exchanges.Single().Question == changed.Text && request.ConsumesDraft("Patrol near Point A."),
                    "Draft edits, a locale or request overrides changed the submitted references or copyable question");
                Require(changed.Text.Contains("(unit_id: 1005)") && !changed.DisplayText.Contains("latitude:") &&
                    draft.Prepare("").Locations.Count == 0, "Coordinates leaked into visible question text or survived clearing");
                var replacement = draft.AddLocation(new AdvisorMapCoordinate(30, 130), "Point {0}");
                Require(replacement.Label == location.Label && replacement.Coordinate.Latitude != location.Coordinate.Latitude &&
                    changed.Locations.Single() == location && changed.Text.Contains("latitude: -26.125, longitude: 127.375"),
                    "Reusing a removed location label changed the submitted history coordinate");
                var only = new AdvisorDraft(); only.AddLocation(location.Coordinate, "Point {0}");
                Require(AdvisorConversation.Validate(only.Prepare("").Text, out _) && only.Prepare("").DisplayBody == "",
                    "A coordinates-only request was rejected or duplicated into its body");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        check("History location badges retain exact clickable coordinates alongside unit badges and typed text", () =>
        {
            var draft = new AdvisorDraft(); draft.Add(1005, "Orion");
            var a = draft.AddLocation(new AdvisorMapCoordinate(26.5, 127.25), "Point {0}");
            var chat = new AdvisorConversation(); chat.Begin(draft.Prepare("Question")).Complete("Answer"); draft.Clear();
            var coordinates = new AdvisorCoordinateLinks();
            var root = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Adviser", "empty",
                coordinateLinks: coordinates)).Root!;
            var badges = root.Elements().Single().Elements(Ns + "WrapPanel").First();
            Require(badges.Elements(Ns + "Border").Count() == 2 && !badges.Descendants(Ns + "Button").Any() &&
                coordinates.Links.Single().Latitude == a.Coordinate.Latitude && coordinates.Links.Single().Longitude == a.Coordinate.Longitude &&
                root.Descendants(Ns + "TextBlock").Any(e => (string?)e.Attribute("Text") == "Question"),
                "History lost a location, acquired editing controls or confused a coordinate with a unit identity");
            var plain = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Adviser", "empty")).Root!;
            Require(!plain.Descendants(Ns + "Hyperlink").Any() && chat.Turns[0].Request.Locations.Single() == a,
                "A view without map support invented an action or removed the stored location");
        });
        check("Draft map markers change only with references or visibility and preserve unrelated map content", () =>
        {
            var map = new Map(); using var unrelated = map.Draw(Array.Empty<AdvisorMapDrawing>());
            using var preview = new AdvisorDraftMapPreview(map);
            var draft = new AdvisorDraft(); var a = draft.AddLocation(new AdvisorMapCoordinate(26.5, 127.25), "Point {0}");
            preview.Update(draft.Locations, false); Require(map.Draws == 1, "A hidden editor drew draft markers");
            preview.Update(draft.Locations, true);
            for (var i = 0; i < 10; i++) preview.Update(draft.Locations, true);
            Require(map.Draws == 2 && map.Active == 2 && map.Last.Single().Label == a.Label &&
                map.Last.Single().Coordinates[0].Latitude == a.Coordinate.Latitude, "An unchanged draft was redrawn or lost its coordinate");
            draft.AddLocation(new AdvisorMapCoordinate(26.75, 127.5), "Point {0}"); preview.Update(draft.Locations, true);
            Require(map.Active == 2 && map.Last.Count == 2, "Adding a badge leaked its former overlay");
            draft.RemoveLocation(a); preview.Update(draft.Locations, true);
            Require(map.Active == 2 && map.Last.Single().Label == "Point B", "Removing a badge removed another point or left a stale marker");
            preview.Update(draft.Locations, false); Require(map.Active == 1, "Hiding the editor removed unrelated content or kept draft markers");
            preview.Update(draft.Locations, true); Require(map.Active == 2, "Reopening lost the draft markers");
            draft.Clear(); preview.Update(draft.Locations, true); preview.Dispose();
            Require(map.Active == 1, "Clearing or disposing the draft removed unrelated drawings or leaked markers");
        });
    }
    private sealed class Map : IAdvisorMap
    {
        internal int Active, Draws;
        internal IReadOnlyList<AdvisorMapDrawing> Last = Array.Empty<AdvisorMapDrawing>();
        public void Center(AdvisorMapCoordinate location) => throw new InvalidOperationException("Draft markers must not move the map");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings)
        { Active++; Draws++; Last = drawings; return new Lease(this); }
        private sealed class Lease : IDisposable
        {
            private Map? owner;
            internal Lease(Map owner) => this.owner = owner;
            public void Dispose() { if (owner != null) owner.Active--; owner = null; }
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
