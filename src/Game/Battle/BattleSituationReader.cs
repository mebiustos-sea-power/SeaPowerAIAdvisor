using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Reads the player's map projection and published mission UI, never scenario definitions or enemy objects.
internal sealed class BattleSituationReader
{
    internal BattleSnapshot Capture(object knownUnits, object player, object objectives, object eventLog,
        DateTime now, string missionName, bool enemyControl, BattleCapabilityReader knowledge,
        string xmlBriefing = "", bool briefingUnavailable = false, bool briefingFromBase = false)
    {
        if (enemyControl) throw new SituationUnavailableException("battle_disclosure");
        var ownObjects = Sequence(Member(player, "TaskforceObjects")).ToArray();
        var units = new JArray();
        var contacts = new JArray();
        var capabilities = new JArray();
        var threats = new JArray();
        var bases = new JArray();
        var aviation = new BattleAviationReader(knowledge.References, now.Year);
        foreach (var mapUnit in Sequence(knownUnits))
        {
            var vehicle = Required(mapUnit, "Unit");
            // Compare references only; an observed enemy's Object is never dereferenced for details.
            var identity = Member(vehicle, "Object");
            if (identity != null && ownObjects.Any(o => ReferenceEquals(o, identity)))
            {
                // TaskforceObjects also contains launched weapons, which do not use platform capabilities.
                if (IsWeapon(identity)) continue;
                var kind = Code(Required(identity, "_type"));
                var parameters = Required(identity, "_obp");
                var subtype = kind == "landunit" ? Code(Required(parameters, "_landUnitSubType")) : null;
                var category = subtype == null ? "mobile_platform" :
                    new[] { "airbase", "port", "installation", "bridge", "oilrig" }.Contains(subtype) ? "base" : "ground_unit";
                var deck = Member(parameters, "_flightDeck");
                units.Add(new JObject
                {
                    ["id"] = "own_unit_" + (units.Count + 1).ToString(CultureInfo.InvariantCulture),
                    ["display_name"] = Reactive<string>(identity, "Name"),
                    ["kind"] = kind, ["unit_category"] = category,
                    ["ground_kind"] = subtype == null ? JValue.CreateNull() : new JValue(subtype),
                    ["position"] = Position(Required(Required(identity, "Position"), "Value")),
                    ["heading_deg"] = Number(Required(Required(identity, "Heading"), "Value")),
                    ["speed_knots"] = Number(Required(identity, "_velocityInKnots"))
                });
                var row = (JObject)units.Last!;
                capabilities.Add(knowledge.CaptureOwn(identity, (string)row["id"]!, (string)row["display_name"]!));
                if (category == "base")
                {
                    var b = (JObject)row.DeepClone();
                    b["has_aviation_facility"] = deck != null;
                    bases.Add(b);
                }
                if (deck != null) aviation.Capture(deck, (string)row["id"]!, (string)row["display_name"]!);
                continue;
            }
            var positionEstimate = Member(vehicle, "PositionEstimate");
            var classKnown = Member(vehicle, "Class") != null;
            var viewModel = classKnown ? Member(mapUnit, "ObjectViewModel") : null;
            var displayName = classKnown && viewModel != null
                ? Reactive<string>(viewModel, "ObjectName")
                : "Contact " + Convert.ToString(Required(vehicle, "Id"), CultureInfo.InvariantCulture);
            var identified = Reactive<bool>(vehicle, "Identified");
            var observedClass = identified && classKnown ? (string)Required(Required(vehicle, "Class"), "Value") : null;
            var reference = knowledge.References.ClassReference(observedClass);
            contacts.Add(new JObject
            {
                ["id"] = "observed_contact_" + (contacts.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["display_name"] = displayName,
                ["kind"] = Code(Member(mapUnit, "VisualType")),
                ["posture"] = Code(Member(mapUnit, "VisualPosture")),
                ["identified"] = identified,
                ["class_display_name"] = reference["class_display_name"]?.DeepClone() ?? JValue.CreateNull(),
                ["public_class_reference_available"] = (string?)reference["status"] == "available",
                ["class_known"] = classKnown,
                ["position"] = positionEstimate != null ? Position(Required(positionEstimate, "Item1")) : JValue.CreateNull(),
                ["position_source"] = positionEstimate == null ? "unknown" : (bool)Required(vehicle, "HasLiveTrack") ? "observed_estimate" : "extrapolated_estimate",
                ["dormant"] = Reactive<bool>(vehicle, "IsDormant"),
                ["observation_time_utc"] = JValue.CreateNull(),
                ["position_error_nm"] = JValue.CreateNull()
            });
            reference["id"] = contacts.Last!["id"]!.DeepClone();
            reference["display_name"] = displayName;
            reference["observed_posture"] = contacts.Last!["posture"]!.DeepClone();
            threats.Add(reference);
        }
        var visibleObjectives = new JArray();
        foreach (var objective in Sequence(objectives))
        {
            if ((bool)Required(objective, "IsHidden")) continue;
            var label = (string)Required(objective, "Text");
            if (string.IsNullOrWhiteSpace(label)) continue;
            var status = Convert.ToInt32(Required(objective, "Status"), CultureInfo.InvariantCulture) switch
            {
                0 => "pending", 1 => "completed", -1 => "failed", 2 => "cancelled",
                _ => throw new InvalidOperationException("Unknown displayed mission objective state.")
            };
            visibleObjectives.Add(new JObject { ["display_text"] = label, ["status"] = status });
        }
        var reports = new JArray();
        foreach (var report in Sequence(Member(eventLog, "Intelligence"))
            .Where(r => (DateTime)Required(r, "Time") <= now).OrderBy(r => (DateTime)Required(r, "Time")).TakeLast(10))
        {
            reports.Add(new JObject
            {
                ["id"] = "report_" + (reports.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["published_at_utc"] = AdvisorInputBuilder.Time((DateTime)Required(report, "Time")),
                ["text"] = (string)Required(report, "IntelligenceText")
            });
        }
        return new BattleSnapshot(new JObject
        {
            ["captured_at_utc"] = AdvisorInputBuilder.Time(now),
            ["mission_display_name"] = missionName,
            ["player_side"] = "player",
            ["public_objectives"] = visibleObjectives,
            ["mission_briefing"] = BattleBriefingReader.Combine(Reactive<string>(eventLog, "MissionBriefing"), xmlBriefing),
            ["mission_briefing_unavailable"] = briefingUnavailable,
            ["mission_briefing_source"] = string.IsNullOrWhiteSpace(xmlBriefing) ? "event_log" :
                briefingFromBase ? "base_mission_xml" : "mission_xml",
            ["friendly_units"] = units,
            ["unit_capabilities"] = capabilities,
            ["contact_threat_references"] = threats,
            ["friendly_bases"] = bases,
            ["hosted_air_units"] = aviation.Units,
            ["aviation_hosts"] = aviation.Hosts,
            ["weapon_references"] = knowledge.References.SnapshotWeapons(),
            ["observed_contacts"] = contacts,
            ["published_reports"] = reports
        });
    }

    private static bool IsWeapon(object unit)
    {
        var method = unit.GetType().GetMethod("isWeapon", Type.EmptyTypes)
            ?? throw new MissingMethodException(unit.GetType().FullName, "isWeapon");
        return (bool)method.Invoke(unit, null)!;
    }

    private static JObject Position(object position) => AdvisorInputBuilder.Position(new MapPosition(
        Number(Required(position, "Latitude")), Number(Required(position, "Longitude"))));

    private static T Reactive<T>(object value, string name) => (T)Required(Required(value, name), "Value");
    private static IEnumerable<object> Sequence(object? value) => Rows(value);
}
