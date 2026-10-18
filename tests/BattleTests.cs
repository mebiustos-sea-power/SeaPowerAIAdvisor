using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Battle capture excludes hidden objectives, future reports and actual enemy details", () =>
        {
            var f = new Fixture();
            var before = f.Capture().Copy().ToString();
            f.Secret.Value = "Changed actual enemy position, armament and orders";
            f.Hidden.Secret = "Changed hidden victory rule";
            f.Future.Secret = "Changed future intelligence";
            Require(f.Capture().Copy().ToString() == before, "Undisclosed information influenced the snapshot");
            var data = f.Capture().Copy();
            Require(((JArray)data["public_objectives"]!).Count == 1 && ((JArray)data["published_reports"]!).Count == 1, "Publication filters were bypassed");
            Require((string?)data["friendly_units"]![0]!["display_name"] == "ミッドウェイ" &&
                (string?)data["observed_contacts"]![0]!["display_name"] == "画面で識別した艦", "Displayed names were lost");
        });
        check("Launched own weapons do not interrupt platform capture or become enemy contacts", () =>
        {
            var f = new Fixture();
            var baseline = f.Capture().Copy();
            foreach (var kind in new[] { "Missile", "Torpedo", "ASROC", "RBU", "Bomb", "AerialRocket", "Chaff", "Noisemaker", "Decoy", "Projectile" })
                f.OwnWeapons.Add(new OwnWeapon(kind));
            var captured = f.Capture();
            Require(JToken.DeepEquals(baseline, captured.Copy()),
                "Launched weapons changed friendly platforms, observed contacts or current capabilities");
            var session = new BattleQuerySession(captured, "en", "What should I do next?");
            var data = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }));
            Require((string?)data["status"] == "ok" &&
                (int?)data["results"]![0]!["data"]!["units"]![0]!["weapons"]!["ammunition"]![0]!["remaining_count"] == 8,
                "A launched weapon prevented retrieval of the platform's current ammunition");
        });
        check("Missing parameters on an own platform remain an error instead of silently hiding the unit", () =>
        {
            var f = new Fixture();
            f.Own._obp = null!;
            try { _ = f.Capture(); throw new Exception("A platform with missing parameters was silently skipped"); }
            catch (InvalidOperationException e)
            {
                Require(e.Message == typeof(OwnUnit).FullName + "._obp is unavailable.", "The original platform error was hidden");
            }
        });
        check("XML and legacy briefings share the existing bounded, optional retrieval route", () =>
        {
            var fixture = new Fixture();
            var session = new BattleQuerySession(fixture.Capture(xmlBriefing: "XMLの公開説明"), "ja", "任務を確認して");
            Require(!session.InitialInput().ToString().Contains("XMLの公開説明", StringComparison.Ordinal), "XML briefing bypassed on-demand retrieval");
            var briefing = session.Execute(Request(session, new JObject { ["query"] = "get_briefing" }));
            Require((string?)briefing["results"]![0]!["data"]!["text"] == "公開済みの任務説明\n\nXMLの公開説明", "A briefing source was lost");
            var restored = new BattleQuerySession(fixture.Capture(xmlBriefing: "復元した公開説明", briefingFromBase: true), "ja", "任務を確認して");
            var restoredData = restored.Execute(Request(restored, new JObject { ["query"] = "get_briefing" }))["results"]![0]!["data"]!;
            Require((string?)restoredData["source"] == "base_mission_xml" && !string.IsNullOrEmpty((string?)restoredData["note"]),
                "The AI could not distinguish recovered mission-start briefing from current data");
            var off = new BattleQuerySession(fixture.Capture(xmlBriefing: "XMLの公開説明", briefingUnavailable: true), "ja", "任務を確認して",
                information: AdvisorInformation.Default.With(InformationKind.BattleBriefing, false));
            Require((string?)off.Execute(Request(off, new JObject { ["query"] = "get_briefing" }))["error"]!["code"] == "information_disabled",
                "XML briefing bypassed the user's exclusion");
            var failed = new BattleQuerySession(fixture.Capture(briefingUnavailable: true), "ja", "任務を確認して");
            Require((string?)failed.Execute(Request(failed, new JObject { ["query"] = "get_briefing" }))["error"]!["code"] == "briefing_unavailable" &&
                (string?)failed.Execute(Request(failed, new JObject { ["query"] = "list_units" }))["status"] == "ok",
                "A briefing failure was hidden or disabled unrelated situation queries");
        });
        check("Unknown contact position stays unknown and a captured battle snapshot remains detached", () =>
        {
            var f = new Fixture();
            f.Track.PositionEstimate = null;
            f.Track.Class = null;
            var captured = f.Capture();
            var data = captured.Copy();
            Require(data["observed_contacts"]![0]!["position"]!.Type == JTokenType.Null &&
                (string?)data["observed_contacts"]![0]!["position_source"] == "unknown", "Missing position was invented");
            Require((string?)data["observed_contacts"]![0]!["display_name"] == "Contact 7", "An unidentified name was disclosed");
            f.Own.Name.Value = "Renamed after capture";
            data["friendly_units"]![0]!["display_name"] = "Mutated consumer copy";
            Require((string?)captured.Copy()["friendly_units"]![0]!["display_name"] == "ミッドウェイ", "Snapshot retained mutable sources or consumer aliases");
            try { _ = f.Capture(true); throw new InvalidOperationException("Enemy-control disclosure was accepted"); }
            catch (SituationUnavailableException e) { Require(e.Code == "battle_disclosure", "Wrong refusal reason"); }
        });
        check("Battle queries support current IDs and pages while rejecting foreign IDs and bounded invalid calls", () =>
        {
            var session = new BattleQuerySession(new Fixture().Capture(), "fr", "次の行動は？");
            var first = session.InitialInput();
            Require((string?)first["mode"] == "tactical_battle" && (string?)first["reply_language"] == "fr" &&
                (string?)first["situation"]!["victory_conditions"]!["objectives"]![0]!["status"] == "pending", "Mandatory mission context is missing");
            var response = session.Execute(Request(session, new JObject { ["query"] = "list_units", ["limit"] = 1 }));
            var id = response["results"]![0]!["data"]!["items"]![0]!["id"]!.DeepClone();
            response = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray(id) }));
            Require((double?)response["results"]![0]!["data"]![0]!["speed_knots"] == 18, "Known own-unit detail was lost");
            response = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray("enemy_internal_id") }));
            Require((string?)response["error"]!["code"] == "unknown_id", "An undisclosed ID was accepted");
            var invalid = Request(session, new JObject { ["query"] = "list_contacts", ["limit"] = 21 });
            Require((string?)session.Execute(invalid)["error"]!["code"] == "invalid_arguments", "Oversized page accepted");
            session.Execute(new JObject { ["consultation_id"] = "previous-consultation", ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) });
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "catalog" }))["error"]!["code"] == "round_limit", "Invalid calls bypassed the round budget");
        });
        check("Own capabilities are batched, frozen, selectively returned and share weapon references", () =>
        {
            var f = new Fixture();
            f.Own._type = "Aircraft";
            f.Own.Compartments = null;
            var ship = new OwnUnit(); ship.Name.Value = "護衛艦"; f.OwnUnits.Add(ship);
            var session = new BattleQuerySession(f.Capture(), "ja", "燃料と兵装を比較して");
            f.Own._obp._currentFuelMass = 1;
            f.Own.AmmunitionAmountDictionary["test_missile"] = 0;
            f.PublicWeapons[0].MaxLaunchRange = new Distance(999);
            var initial = session.InitialInput();
            Require(initial["situation"]!["unit_capabilities"] == null && initial["situation"]!["weapon_references"] == null,
                "Large capability details were sent before the model requested them");
            var data = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1", "own_unit_2") }))["results"]![0]!["data"]!;
            var units = (JArray)data["units"]!;
            Require(units.Count == 2 && (double?)units[0]!["fuel"]!["remaining_mass_kg"] == 600 &&
                (double?)units[0]!["fuel"]!["percent_of_internal_capacity"] == 60 &&
                (string?)units[1]!["fuel"]!["status"] == "not_displayed_for_unit_type" &&
                units[1]!["fuel"]!["remaining_mass_kg"]!.Type == JTokenType.Null, "Fuel types or frozen values were lost");
            Require((int?)units[0]!["weapons"]!["ammunition"]![0]!["remaining_count"] == 8 &&
                (double?)units[1]!["damage"]!["hull_integrity_percent"] == 75 &&
                (string?)units[1]!["damage"]!["damaged_systems"]![0]!["damage_state"] == "severe" &&
                ((JArray)data["weapon_references"]!).Count == 1 &&
                (double?)data["weapon_references"]![0]!["maximum_launch_range_nm"] == 70,
                "Capabilities mixed current sources, lost damage or repeated a shared weapon definition");
            var fuel = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("fuel") }))["results"]![0]!["data"]!;
            Require(fuel["units"]![0]!["weapons"] == null && fuel["units"]![0]!["damage"] == null &&
                ((JArray)fuel["weapon_references"]!).Count == 0, "A fuel-only request returned unrelated details");
            var bad = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("enemy_loadout") }));
            Require((string?)bad["error"]!["code"] == "invalid_arguments", "An unsupported section was accepted");
        });
        check("Weapon definitions are delivered once across unit, aircraft and contact queries while evidence and new definitions remain available", () =>
        {
            var f = GroundFixture(out _);
            f.Own.AmmunitionAmountDictionary["test_missile"] = 0;
            var extra = new PublicWeapon { IniRef = "followup_weapon", MaxLaunchRange = new Distance(55) };
            f.PublicWeapons.Add(extra); f.PublicClasses[0].WeaponEntries.Add(extra);
            var snapshot = f.Capture();
            var original = snapshot.Copy().ToString();
            var session = new BattleQuerySession(snapshot, "ja", "Compare capabilities", 20);
            var first = session.Execute(Request(session,
                new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") },
                new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") }));
            var own = first["results"]![0]!["data"]!; var air = first["results"]![1]!["data"]!;
            var reference = own["weapon_references"]![0]!;
            Require(own["weapon_references"]!.Count() == 1 && !air["weapon_references"]!.HasValues &&
                (int?)own["units"]![0]!["weapons"]!["ammunition"]![0]!["remaining_count"] == 0 &&
                (string?)air["air_units"]![0]!["loadouts"]![0]!["ammunition"]![0]!["reference_id"] == (string?)reference["id"] &&
                (double?)reference["maximum_launch_range_nm"] == 70,
                "A same-batch duplicate survived or removing its definition changed ammunition or aircraft references");
            var threat = session.Execute(Request(session, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }))["results"]![0]!["data"]!;
            Require(threat["weapon_references"]!.Count() == 1 && (double?)threat["weapon_references"]![0]!["maximum_launch_range_nm"] == 55 &&
                threat["contacts"]![0]!["possible_weapon_reference_ids"]!.Count() == 2,
                "A later query lost a new weapon definition or repeated an already delivered definition");
            var repeated = session.Execute(Request(session,
                new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") },
                new JObject { ["query"] = "get_air_units", ["ids"] = new JArray("hosted_air_unit_1") },
                new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }));
            Require((string?)repeated["status"] == "ok" && repeated["results"]!.All(r => !r["data"]!["weapon_references"]!.HasValues) &&
                snapshot.Copy().ToString() == original, "Previously delivered definitions were repeated or the captured evidence was changed");
            var fresh = new BattleQuerySession(snapshot, "en", "New consultation");
            Require(fresh.Execute(Request(fresh, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }))["results"]![0]!["data"]!["weapon_references"]!.Count() == 1,
                "A new consultation incorrectly reused another consultation's delivered IDs");
        });
        check("Rejected query batches do not mark unreturned weapon definitions as delivered", () =>
        {
            foreach (var error in new[] { "unknown_id", "response_too_large" })
            {
                var data = new Fixture().Capture().Copy();
                if (error == "response_too_large") data["mission_briefing"] = new string('x', AdvisorQueryLimits.MaxResponseBytes);
                var session = new BattleQuerySession(new BattleSnapshot(data), "en", "Evidence", 20);
                var next = error == "unknown_id"
                    ? new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("not_a_current_contact") }
                    : new JObject { ["query"] = "get_briefing" };
                var rejected = session.Execute(Request(session,
                    new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }, next));
                Require((string?)rejected["error"]?["code"] == error && rejected["results"] == null,
                    "A rejected batch returned partial evidence or failed for the wrong reason");
                var retry = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }));
                Require((string?)retry["status"] == "ok" && retry["results"]![0]!["data"]!["weapon_references"]!.Count() == 1 && session.RoundsUsed == 2,
                    "An unsuccessful delivery suppressed the later first definition or reset retrieval limits");
            }
        });
        check("Enemy threat references use observed classes and public variants without reading actual enemy state", () =>
        {
            var f = new Fixture();
            f.PublicWeapons.Add(new PublicWeapon { IniRef = "future_missile", IocDate = 2030, MaxLaunchRange = new Distance(9999) });
            f.PublicWeapons.Add(new PublicWeapon { IniRef = "hidden_missile", HiddenInEncyclopedia = true, MaxLaunchRange = new Distance(9998) });
            f.PublicClasses[0].WeaponEntries.AddRange(f.PublicWeapons.Skip(1));
            f.PublicWeapons.Add(new PublicWeapon { IniRef = "alternate_missile", MaxLaunchRange = new Distance(55) });
            f.PublicClasses[0].DateBasedAlternatives["test_missile"] = new[] { (1980, "alternate_missile"), (2030, "future_missile") };
            var captured = f.Capture();
            f.Secret.Value = "Changed actual ammunition, damage, fuel and position";
            Require(f.Capture().Copy().ToString() == captured.Copy().ToString(), "Actual enemy state affected public threat references");
            var session = new BattleQuerySession(captured, "ja", "敵の脅威となる距離は？");
            var data = session.Execute(Request(session, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }))["results"]![0]!["data"]!;
            var contact = data["contacts"]![0]!;
            Require((string?)contact["class_display_name"] == "公開された艦級" &&
                (bool?)contact["actual_enemy_state_included"] == false &&
                ((JArray)contact["unknowns"]!).Values<string>().Contains("remaining_ammunition") &&
                (string?)contact["observed_posture"] == "unknown" &&
                ((JArray)contact["possible_weapon_reference_ids"]!).Count == 2 &&
                ((JArray)data["weapon_references"]!).Count == 2 &&
                ((JArray)data["weapon_references"]!).OfType<JObject>().Any(w => (double?)w["maximum_launch_range_nm"] == 70) &&
                ((JArray)data["weapon_references"]!).OfType<JObject>().Any(w => (double?)w["maximum_launch_range_nm"] == 55),
                "Reference candidates lost their evidence boundary or included hidden/future weapons");
            f.Track.Class = null;
            var unknown = new BattleQuerySession(f.Capture(), "en", "Threats?");
            var reply = unknown.Execute(Request(unknown, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }));
            Require((string?)reply["results"]![0]!["data"]!["contacts"]![0]!["status"] == "unidentified_class" &&
                ((JArray)reply["results"]![0]!["data"]!["weapon_references"]!).Count == 0, "An unknown class was guessed from the enemy object");
            var rejected = unknown.Execute(Request(unknown, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("own_unit_1") }));
            Require((string?)rejected["error"]!["code"] == "unknown_id", "Threat queries accepted IDs from the wrong scope");
        });
        check("Missing public ranges stay unknown and malformed current values surface instead of becoming zero", () =>
        {
            var f = new Fixture();
            f.PublicWeapons[0].MaxLaunchRange = new Distance(double.NaN);
            var snapshot = f.Capture().Copy();
            Require(snapshot["weapon_references"]![0]!["maximum_launch_range_nm"]!.Type == JTokenType.Null,
                "An unspecified encyclopedia range became a zero or an invented value");
            f.Ready = false;
            snapshot = f.Capture().Copy();
            Require((string?)snapshot["contact_threat_references"]![0]!["status"] == "encyclopedia_not_ready" &&
                (int?)snapshot["unit_capabilities"]![0]!["weapons"]!["ammunition"]![0]!["remaining_count"] == 8,
                "An unavailable reference library hid current own inventory");
            f.Own.AmmunitionAmountDictionary["test_missile"] = -1;
            try { _ = f.Capture(); throw new Exception("Negative inventory was accepted"); }
            catch (InvalidOperationException) { }
        });
        check("A Gemini battle consultation batches own capabilities with public enemy threat evidence", () =>
        {
            var session = new BattleQuerySession(new Fixture().Capture(), "ja", "自軍と敵の射程を比べて");
            var result = new GeminiAdvisor(new CapabilityTransport()).ConsultAsync(session,
                AdvisorSettings.Parse("{\"api_key\":\"mock-key\",\"model\":\"gemini-test\",\"log_ai_json\":false}"),
                Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 3 && result.QueryRounds == 2 && result.Queries.Contains("get_unit_capabilities") &&
                result.Queries.Contains("get_contact_threats"), "The new evidence queries did not share one retrieval round");
        });
        check("Battle exclusions gate metadata and every detail route without removing public objectives", () =>
        {
            var policy = AdvisorInformation.All;
            foreach (var item in AdvisorInformation.Items) policy = policy.With(item.Kind, false);
            var source = new BattleAdvisorSource(() => new Fixture().Capture());
            var session = source.CreateSession("ja", "What now?", 20, policy);
            var initial = session.InitialInput();
            Require(!initial["situation"]!["overview"]!.HasValues && !initial["knowledge_catalog"]!.HasValues &&
                initial["situation"]!["victory_conditions"]!["objectives"]!.HasValues, "Excluded metadata leaked or objectives were removed");
            var names = initial["query_tool"]!["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!.Values<string>();
            Require(names.SequenceEqual(new[] { "catalog" }), "The provider advertises disabled battle tools");
            foreach (var query in new[] { "list_units", "get_units", "get_unit_capabilities", "list_contacts", "get_contact_threats", "get_briefing", "list_reports", "get_reports" })
                Require((string?)session.Execute(Request(session, new JObject { ["query"] = query, ["ids"] = new JArray("own_unit_1") }))["error"]?["code"] == "information_disabled",
                    "Direct query bypassed disabled category: " + query);
            Require(!session.Execute(Request(session, new JObject { ["query"] = "catalog" }))["results"]![0]!["data"]!.HasValues,
                "The runtime catalogue disclosed excluded categories");
        });
        check("Battle sections and shared references respect independent own and contact permissions", () =>
        {
            var policy = AdvisorInformation.All.With(InformationKind.BattleWeapons, false).With(InformationKind.BattleDamage, false);
            var session = new BattleQuerySession(new Fixture().Capture(), "ja", "Fuel and threats", information: policy);
            var initial = session.InitialInput();
            var sections = initial["query_tool"]!["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["sections"]!["items"]!["enum"]!.Values<string>();
            Require(sections.SequenceEqual(new[] { "fuel" }), "The tool still offers disabled capability sections");
            var fuel = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }))["results"]![0]!["data"]!;
            Require(fuel["units"]![0]!["fuel"] != null && fuel["units"]![0]!["damage"] == null && fuel["units"]![0]!["weapons"] == null &&
                !fuel["weapon_references"]!.HasValues, "An omitted sections argument leaked disabled data or weapon references");
            var rejected = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("fuel", "weapons") }));
            Require((string?)rejected["error"]?["code"] == "information_disabled" && rejected["results"] == null, "Explicit disabled sections returned partial evidence");
            var threat = session.Execute(Request(session, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }));
            Require(threat["results"]![0]!["data"]!["weapon_references"]!.HasValues, "Disabling own weapons incorrectly disabled public enemy references");
            var noThreats = new BattleQuerySession(new Fixture().Capture(), "ja", "Contacts only", information: AdvisorInformation.All.With(InformationKind.BattleThreats, false));
            var contact = noThreats.Execute(Request(noThreats, new JObject { ["query"] = "list_contacts" }))["results"]![0]!["data"]!["items"]![0]!;
            Require(contact["class_display_name"] != null && contact["public_class_reference_available"] == null,
                "Disabling public references removed observed identification or leaked reference availability");
        });
        check("Neutral selection filters counts, pages and threat routes by displayed posture and preserves mandatory context", () =>
        {
            var data = new Fixture { ContactPosture = "Neutral" }.Capture().Copy();
            var neutral = (JObject)data["observed_contacts"]![0]!;
            var reference = (JObject)data["contact_threat_references"]![0]!;
            var template = (JObject)reference.DeepClone();
            reference["possible_weapon_reference_ids"] = new JArray("neutral_weapon_only");
            ((JArray)data["weapon_references"]!).Add(new JObject { ["id"] = "neutral_weapon_only", ["display_name"] = "Neutral-only public reference" });
            foreach (var (id, posture, kind) in new[] {
                ("observed_contact_2", "neutral", "air"), ("observed_contact_3", "unknown", "surface"),
                ("observed_contact_4", "allied", "surface"), ("observed_contact_5", "enemy", "surface") })
            {
                var contact = (JObject)neutral.DeepClone();
                contact["id"] = id; contact["posture"] = posture; contact["kind"] = kind;
                var threat = (JObject)template.DeepClone(); threat["id"] = id; threat["observed_posture"] = posture;
                ((JArray)data["observed_contacts"]!).Add(contact);
                ((JArray)data["contact_threat_references"]!).Add(threat);
            }
            var snapshot = new BattleSnapshot(data);
            var off = new BattleQuerySession(snapshot, "ja", "What now?", 20);
            var initial = off.InitialInput();
            Require((int?)initial["situation"]!["overview"]!["observed_contact_count"] == 3 &&
                (int?)initial["knowledge_catalog"]!["observed_contacts"]!["item_count"] == 3 &&
                JToken.DeepEquals(initial["situation"]!["victory_conditions"]!["objectives"], data["public_objectives"]),
                "Neutral counts were disclosed or public objectives were changed");
            var page = off.Execute(Request(off, new JObject { ["query"] = "list_contacts", ["limit"] = 1 }))["results"]![0]!["data"]!;
            Require((int?)page["total_count"] == 3 && (string?)page["items"]![0]!["id"] == "observed_contact_3" && (int?)page["next_offset"] == 1,
                "Neutral contacts were filtered after pagination or an unknown contact was removed");
            var next = off.Execute(Request(off, new JObject { ["query"] = "list_contacts", ["offset"] = 1, ["limit"] = 1 }))["results"]![0]!["data"]!;
            Require((string?)next["items"]![0]!["id"] == "observed_contact_4", "A retained allied contact was removed or pages skipped a row");
            var air = off.Execute(Request(off, new JObject { ["query"] = "list_contacts", ["kind"] = "air" }))["results"]![0]!["data"]!;
            Require((int?)air["total_count"] == 0 && !air["items"]!.HasValues, "A kind filter bypassed the neutral-aircraft exclusion");
            var threats = off.Execute(Request(off, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_3", "observed_contact_4", "observed_contact_5") }));
            Require((string?)threats["status"] == "ok" && threats["results"]![0]!["data"]!["contacts"]!.Count() == 3 &&
                threats["results"]![0]!["data"]!["weapon_references"]!.HasValues && !threats.ToString().Contains("neutral_weapon_only", StringComparison.Ordinal),
                "Retained threat references were lost or neutral-only weapon data was returned");
            var rejected = off.Execute(Request(off, new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_3", "observed_contact_1") }));
            Require((string?)rejected["error"]?["code"] == "unknown_id" && rejected["results"] == null,
                "Direct or mixed-ID requests recovered an excluded neutral contact");
            var briefing = off.Execute(Request(off, new JObject { ["query"] = "get_briefing" }))["results"]![0]!["data"]!["text"];
            Require(JToken.DeepEquals(briefing, data["mission_briefing"]), "Neutral filtering changed the briefing text");
            var on = new BattleQuerySession(snapshot, "en", "Show neutral contacts", information: AdvisorInformation.Default.With(InformationKind.BattleNeutralContacts, true));
            var restored = on.Execute(Request(on, new JObject { ["query"] = "list_contacts" },
                new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray("observed_contact_1") }));
            Require((int?)on.InitialInput()["situation"]!["overview"]!["observed_contact_count"] == 5 &&
                (int?)restored["results"]![0]!["data"]!["total_count"] == 5 &&
                restored["results"]![1]!["data"]!["weapon_references"]!.Any(w => (string?)w["id"] == "neutral_weapon_only") &&
                snapshot.Copy()["observed_contacts"]!.Count() == 5,
                "Enabling neutral data did not restore both kinds or filtering mutated the captured snapshot");
        });
        check("Gemini receives the restricted tool schema and a disabled request returns no data", () =>
        {
            var policy = AdvisorInformation.All.With(InformationKind.BattleWeapons, false);
            var session = new BattleQuerySession(new Fixture().Capture(), "ja", "Own fuel", information: policy);
            var transport = new RestrictedTransport();
            var result = new GeminiAdvisor(transport).ConsultAsync(session, AdvisorSettings.Parse("{\"api_key\":\"mock-information-key\"}"),
                Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 2 && result.QueryRounds == 1 && transport.Calls == 2, "Restricted consultation did not complete");
        });
        check("Gemini uses battle-specific tools through the common provider", () =>
        {
            var session = new BattleQuerySession(new Fixture().Capture(), "ja", "まず何をすべき？");
            var transport = new MockBattleTransport();
            var settings = AdvisorSettings.Parse("{\"api_key\":\"mock-local-key\",\"model\":\"gemini-3.5-flash-lite\",\"log_ai_json\":false}");
            var result = new GeminiAdvisor(transport).ConsultAsync(session, settings, Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 3 && result.QueryRounds == 2 && result.Queries.Contains("get_units") &&
                result.Text == "公開目標を優先し、接触を確認してください。", "Battle consultation did not complete");
        });
        RunGroundAir(check);
    }

    private static JObject Request(IAdvisorQuerySession session, params JObject[] queries) =>
        new() { ["consultation_id"] = session.Id, ["requests"] = new JArray(queries) };

    private sealed class RestrictedTransport : IGeminiTransport
    {
        internal int Calls;
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            Calls++;
            JObject part;
            if (Calls == 1)
            {
                var sections = body["tools"]![0]!["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["sections"]!["items"]!["enum"]!.Values<string>();
                Require(!sections.Contains("weapons") && sections.Contains("fuel"), "The actual wire request advertised a disabled section");
                part = new JObject { ["functionCall"] = new JObject { ["name"] = "query_situation", ["args"] = new JObject {
                    ["requests"] = new JArray(new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("fuel", "weapons") }) } } };
            }
            else
            {
                var result = InteractionFixture.Result(body);
                Require((string?)result["error"]?["code"] == "information_disabled" && result["results"] == null,
                    "The actual function response exposed disabled data");
                part = new JObject { ["text"] = "兵装情報が無効なため、その制約を考慮します。" };
            }
            return Task.FromResult(InteractionFixture.Reply(part));
        }
    }

    private sealed class MockBattleTransport : IGeminiTransport
    {
        private int calls;
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            calls++;
            if (calls == 1)
            {
                var input = InteractionFixture.Initial(body);
                Require((string?)input["mode"] == "tactical_battle" && (string?)input["reply_language"] == "ja", "Provider changed mode or reply language");
            }
            JObject part;
            if (calls == 1) part = Call(new JObject { ["query"] = "list_units" }, new JObject { ["query"] = "list_contacts" }, new JObject { ["query"] = "get_briefing" });
            else if (calls == 2)
            {
                var answer = InteractionFixture.Result(body);
                var id = answer["results"]![0]!["data"]!["items"]![0]!["id"]!.DeepClone();
                Require((string?)answer["results"]![2]!["data"]!["text"] == "公開済みの任務説明", "Briefing was not returned");
                part = Call(new JObject { ["query"] = "get_units", ["ids"] = new JArray(id) });
            }
            else part = new JObject { ["text"] = "公開目標を優先し、接触を確認してください。" };
            return Task.FromResult(InteractionFixture.Reply(part));
        }
        private static JObject Call(params JObject[] requests) => new()
        {
            ["functionCall"] = new JObject { ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(requests) } }
        };
    }

    private sealed class CapabilityTransport : IGeminiTransport
    {
        private int calls;
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            calls++;
            JObject part;
            if (calls == 1) part = Tool(new JObject { ["query"] = "list_units" }, new JObject { ["query"] = "list_contacts" });
            else if (calls == 2)
            {
                var data = InteractionFixture.Result(body)["results"]!;
                part = Tool(new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray(data[0]!["data"]!["items"]![0]!["id"]!.DeepClone()) },
                    new JObject { ["query"] = "get_contact_threats", ["ids"] = new JArray(data[1]!["data"]!["items"]![0]!["id"]!.DeepClone()) });
            }
            else
            {
                var data = InteractionFixture.Result(body)["results"]!;
                Require((int?)data[0]!["data"]!["units"]![0]!["weapons"]!["ammunition"]![0]!["remaining_count"] == 8 &&
                    (string?)data[1]!["data"]!["contacts"]![0]!["evidence_kind"] == "public_class_reference" &&
                    !data[1]!["data"]!["weapon_references"]!.HasValues &&
                    (double?)data[0]!["data"]!["weapon_references"]![0]!["maximum_launch_range_nm"] == 70 &&
                    (string?)data[1]!["data"]!["contacts"]![0]!["possible_weapon_reference_ids"]![0] ==
                    (string?)data[0]!["data"]!["weapon_references"]![0]!["id"],
                    "The provider did not receive inventory and sourced conditional threat evidence");
                part = new JObject { ["text"] = "公開性能に基づく推定です。敵の実際の搭載と残弾は不明です。" };
            }
            return Task.FromResult(InteractionFixture.Reply(part));
        }
        private static JObject Tool(params JObject[] requests) => new() { ["functionCall"] = new JObject {
            ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(requests) } } };
    }

    private sealed class Fixture
    {
        internal readonly OwnUnit Own = new();
        internal readonly SecretEnemy Secret = new();
        internal readonly Track Track;
        internal readonly HiddenObjective Hidden = new();
        internal readonly FutureReport Future = new();
        private readonly DateTime now = new(1985, 6, 1, 12, 0, 0);
        internal readonly List<OwnUnit> OwnUnits = new();
        internal readonly List<OwnWeapon> OwnWeapons = new();
        internal readonly List<PublicWeapon> PublicWeapons = new() { new PublicWeapon() };
        internal readonly List<PublicClass> PublicClasses = new();
        internal bool Ready = true;
        internal string ContactKind = "Surface";
        internal string ContactPosture = "Unknown";
        internal Fixture()
        {
            Track = new Track { Object = Secret };
            OwnUnits.Add(Own);
            PublicClasses.Add(new PublicClass { WeaponEntries = new List<PublicWeapon>(PublicWeapons) });
        }
        internal BattleSnapshot Capture(bool disclosure = false, string xmlBriefing = "", bool briefingUnavailable = false, bool briefingFromBase = false) => new BattleSituationReader().Capture(
            OwnUnits.Cast<object>().Concat(OwnWeapons).Select(u => (object)new { Unit = new { Object = u } }).Concat(new object[] {
                new { Unit = Track, ObjectViewModel = new { ObjectName = new Reactive<string>("画面で識別した艦") }, VisualType = ContactKind, VisualPosture = ContactPosture }
            }).ToArray(), new { TaskforceObjects = OwnUnits.Cast<object>().Concat(OwnWeapons).ToArray() },
            new object[] { new { IsHidden = false, Text = "輸送船を保護する", Status = 0 }, Hidden },
            new
            {
                MissionBriefing = new Reactive<string>("公開済みの任務説明"),
                Intelligence = new object[] { new { Time = now.AddMinutes(-1), IntelligenceText = "接触の報告" }, Future },
                AllIntelligence = new Forbidden()
            }, now, "Test scenario", disclosure, new BattleCapabilityReader(
                new BattleReferenceCatalog(PublicClasses, PublicWeapons, now.Year, Ready)), xmlBriefing, briefingUnavailable, briefingFromBase);
    }
    private sealed class Reactive<T>
    {
        public T Value { get; set; }
        internal Reactive(T value) { Value = value; }
    }
    private sealed class OwnUnit
    {
        public Reactive<string> Name { get; } = new("ミッドウェイ");
        public string _type { get; set; } = "Vessel";
        public Parameters _obp { get; set; } = new();
        public bool isWeapon() => false;
        public bool _unlimitedFuel => false;
        public object? Compartments { get; set; } = new { IntegrityPercentage = 75.0, FloodingPercentage = 10.0 };
        public bool IsDestroyed => false;
        public bool Disabled => false;
        public Reactive<double> FlightTimeLeftInSec { get; } = new(1200);
        public Reactive<string> WeaponState { get; } = new("tight");
        public Dictionary<string, int> AmmunitionAmountDictionary { get; } = new() { ["test_missile"] = 8 };
        public Dictionary<string, object> AmmunitionNameToAmmunitionDictionary { get; } = new() {
            ["test_missile"] = new { _ap = new { _displayedName = "公開ミサイル" } }
        };
        public Reactive<Geo> Position { get; } = new(new Geo(31, 131));
        public Reactive<double> Heading { get; } = new(270);
        public double _velocityInKnots { get; } = 18;
    }
    private sealed class OwnWeapon
    {
        public string _type { get; }
        public object? _obp => null;
        internal OwnWeapon(string kind) { _type = kind; }
        public bool isWeapon() => true;
    }
    private sealed class Parameters
    {
        public object? _flightDeck { get; set; }
        public string _landUnitSubType { get; set; } = "Installation";
        public double _currentFuelMass = 600;
        public double _maxFuel = 1200;
        public double _defaultMaxFuel = 1000;
        public object[] _systems = { new { CurrentDamageType = "no" }, new {
            CurrentDamageType = "Severe", DisplayName = new Reactive<string>("捜索レーダー"), Repairable = false } };
    }
    private sealed class Distance
    {
        public double InNauticalMiles { get; }
        internal Distance(double value) { InNauticalMiles = value; }
    }
    private sealed class PublicWeapon
    {
        public bool HiddenInEncyclopedia { get; set; }
        public string IniRef { get; set; } = "test_missile";
        public int VariantIndex => 0;
        public string DisplayName => "公開ミサイル";
        public string? ModSource => null;
        public string Type => "Missile";
        public string TargetType => "ASuW";
        public bool HasSecondaryTargetType => false;
        public string SecondaryTargetType => "UNKNOWN";
        public bool CanAttackSurface => true;
        public bool CanAttackAir => false;
        public string GuidanceType => "ActiveRadar";
        public object? Ap => new { IsFcrHorizonLimited = false, RequiresDirectLineOfSight = false };
        public int IocDate { get; set; } = 1970;
        public Distance MinLaunchRange => new(2);
        public Distance MaxLaunchRange { get; set; } = new(70);
        public Distance GunAaRange => new(double.NaN);
    }
    private sealed class PublicClass
    {
        public bool HiddenInEncyclopedia => false;
        public string IniRef => "test_class";
        public int ServiceEntryDate => 1960;
        public string DisplayClassName => "公開された艦級";
        public string DisplayName => "公開された艦級";
        public List<PublicWeapon> WeaponEntries { get; set; } = new();
        public Dictionary<string, (int, string)[]> DateBasedAlternatives { get; } = new();
        public object[] WeaponSystemEntries => Array.Empty<object>();
        public Dictionary<string, object[]> LoadoutWeaponSystemEntries => new();
    }
    private sealed class Geo
    {
        public double Latitude { get; }
        public double Longitude { get; }
        internal Geo(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; }
    }
    private sealed class Track
    {
        public object Object { get; set; } = null!;
        public object? PositionEstimate { get; set; } = new { Item1 = new Geo(32, 130) };
        public object? Class { get; set; } = new { Value = "test_class" };
        public object Position => throw new InvalidOperationException("Read a contact position outside its estimate");
        public bool HasLiveTrack => false;
        public Reactive<bool> Identified { get; } = new(true);
        public Reactive<bool> IsDormant { get; } = new(true);
        public int Id => 7;
    }
    private sealed class SecretEnemy
    {
        internal string Value = "Actual enemy secrets";
        public object Position => throw new InvalidOperationException("Read actual enemy position");
        public string Name => throw new InvalidOperationException("Read actual enemy name");
        public object _obp => throw new InvalidOperationException("Read actual enemy capabilities");
        public object _flightDeck => throw new InvalidOperationException("Read enemy stored aircraft");
        public object AmmunitionAmountDictionary => throw new InvalidOperationException("Read actual enemy ammunition");
        public object Compartments => throw new InvalidOperationException("Read actual enemy damage");
    }
    private sealed class HiddenObjective
    {
        internal string Secret = "Secret win trigger";
        public bool IsHidden => true;
        public string Text => throw new InvalidOperationException("Read a hidden objective");
    }
    private sealed class FutureReport
    {
        internal string Secret = "Future scripted report";
        public DateTime Time => new(1985, 6, 2);
        public string IntelligenceText => throw new InvalidOperationException("Read future intelligence");
    }
    private sealed class Forbidden { }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
