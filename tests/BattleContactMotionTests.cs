using System;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunContactMotion(Action<string, Action> check)
    {
        check("Own unit details include frozen vertical status in feet and respect the existing own-unit permission", () =>
        {
            foreach (var (kind, altitude, key, expected) in new[] {
                ("Submarine", -250f, "depth_ft", 250d),
                ("Aircraft", 12345f, "altitude_ft", 12345d),
                ("Helicopter", 50f, "altitude_ft", 50d),
                ("Biologic", -75f, "depth_ft", 75d) })
            {
                var f = new Fixture();
                f.Own._type = kind;
                f.Own.Altitude.Value = altitude;
                var snapshot = f.Capture();
                f.Own.Altitude.Value = -600;
                var session = new BattleQuerySession(snapshot, "en", "What is this unit's altitude or depth?");
                var data = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray("own_unit_1") }));
                var row = data["results"]![0]!["data"]![0]!;
                Require((double?)row[key] == expected && row[key == "depth_ft" ? "altitude_ft" : "depth_ft"] == null,
                    "Own vertical status was missing, in the wrong units/axis or read after capture");
                var excluded = new BattleQuerySession(snapshot, "en", "Vertical status",
                    information: AdvisorInformation.Default.With(InformationKind.BattleUnits, false));
                var blocked = excluded.Execute(Request(excluded, new JObject { ["query"] = "get_units", ["ids"] = new JArray("own_unit_1") }));
                Require((string?)blocked["error"]?["code"] == "information_disabled" && blocked["results"] == null,
                    "Vertical status bypassed the own-unit information setting");
            }
            var surface = new Fixture().Capture().Copy()["friendly_units"]![0]!;
            Require(surface["depth_ft"] == null && surface["altitude_ft"] == null,
                "Surface units acquired inapplicable vertical fields");
            var surfacedSubmarine = new Fixture(); surfacedSubmarine.Own._type = "Submarine";
            Require((double?)surfacedSubmarine.Capture().Copy()["friendly_units"]![0]!["depth_ft"] == 0,
                "A surfaced submarine's known zero depth became unavailable");
        });
        check("Contact motion uses displayed units without a selected UI and remains frozen behind contact permissions", () =>
        {
            var f = new Fixture { ContactDisplay = null };
            f.Track.Class = null;
            f.Track.Identified.Value = false;
            f.Track.UnityVelocityVector = new ContactVelocity(-1, 0, 0);
            f.Secret.ForbidAltitudeRead = true;
            var captured = f.Capture();
            f.Track.UnityVelocityVector = new ContactVelocity(0, 0, 0);
            var session = new BattleQuerySession(captured, "en", "How is this contact moving?");
            var data = session.Execute(Request(session, new JObject { ["query"] = "list_contacts" }));
            var row = data["results"]![0]!["data"]!["items"]![0]!;
            Require(Math.Abs((double)row["speed_knots"]! - 130.62618) < 0.002 &&
                Math.Abs((double)row["heading_deg"]! - 270) < 0.001,
                "Contact motion used live/actual target values, the wrong units or a reversed heading");
            Require(row["altitude_ft"] == null && row["depth_ft"] == null &&
                (bool?)row["identified"] == false, "A surface contact disclosed vertical status or became identified");
            var excluded = new BattleQuerySession(captured, "en", "Contact motion",
                information: AdvisorInformation.Default.With(InformationKind.BattleContacts, false));
            var blocked = excluded.Execute(Request(excluded, new JObject { ["query"] = "list_contacts" }));
            Require((string?)blocked["error"]?["code"] == "information_disabled" && blocked["results"] == null &&
                excluded.InitialInput()["situation"]!["overview"]!["observed_contact_count"] == null,
                "Contact motion bypassed the existing contact permission");
        });
        check("Airborne and underwater contacts receive displayed vertical status before identification", () =>
        {
            foreach (var (kind, altitude, key, expected) in new[] {
                ("Air", 12345f, "altitude_ft", 12345f),
                ("Helicopter", 50f, "altitude_ft", 50f),
                ("Submarine", -250f, "depth_ft", 250f),
                ("Missile", 125f, "altitude_ft", 125f),
                ("Torpedo", -150f, "depth_ft", 150f),
                ("Torpedo", 25f, "depth_ft", 0f),
                ("Biologic", -75f, "depth_ft", 75f) })
            {
                var f = new Fixture { ContactKind = kind };
                f.Track.Class = null;
                f.Track.Identified.Value = false;
                f.Secret.Altitude = new Reactive<float>(altitude);
                var session = new BattleQuerySession(f.Capture(), "en", "Inspect this contact");
                var row = session.Execute(Request(session, new JObject { ["query"] = "list_contacts" }))
                    ["results"]![0]!["data"]!["items"]![0]!;
                Require((float?)row[key] == expected && row[key == "depth_ft" ? "altitude_ft" : "depth_ft"] == null,
                    "Contact vertical status used the wrong unit, sign or displayed category");
                Require((float?)row["speed_knots"] == 0 && (float?)row["heading_deg"] == 0,
                    "Displayed zero motion was confused with unavailable motion");
            }
        });
        check("Unavailable contact motion remains unknown and malformed displayed values stay visible as errors", () =>
        {
            foreach (var (kind, key) in new[] {
                ("Air", "altitude_ft"), ("Helicopter", "altitude_ft"), ("Missile", "altitude_ft"),
                ("Submarine", "depth_ft"), ("Torpedo", "depth_ft"), ("Biologic", "depth_ft"), ("Surface", "") })
            {
                var f = new Fixture { ContactKind = kind };
                f.Track.UnityVelocityVector = null;
                f.Secret.Altitude = null;
                var row = f.Capture().Copy()["observed_contacts"]![0]!;
                Require(row["speed_knots"]!.Type == JTokenType.Null && row["heading_deg"]!.Type == JTokenType.Null,
                    "Unavailable motion was invented or read from the actual target");
                if (key.Length > 0) Require(row[key]!.Type == JTokenType.Null,
                    "Unknown altitude/depth was converted to sea level");
            }
            var invalid = new Fixture { ContactKind = "Air" };
            invalid.Secret.Altitude = new Reactive<float>(float.NaN);
            try { _ = invalid.Capture(); throw new Exception("Invalid displayed altitude was hidden"); }
            catch (InvalidOperationException e)
            { Require(e.Message == "Invalid battle knowledge value.", "The displayed-data error was replaced by a fallback"); }
            invalid.Secret.Altitude = new Reactive<float>(0);
            invalid.Track.UnityVelocityVector = new ContactVelocity(float.NaN, 0, 0);
            try { _ = invalid.Capture(); throw new Exception("Invalid contact velocity was hidden"); }
            catch (InvalidOperationException e)
            { Require(e.Message == "Invalid battle knowledge value.", "The velocity error was replaced by a fallback"); }
        });
    }

    private sealed class ContactVelocity
    {
        public float x, y, z;
        internal ContactVelocity(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}
