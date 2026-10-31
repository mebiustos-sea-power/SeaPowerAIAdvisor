using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Reads the player's map projection and published mission UI, never scenario definitions.
internal sealed class BattleSituationReader
{
    internal BattleSnapshot Capture(object knownUnits, object player, object objectives, object missionAreas, object eventLog,
        DateTime now, string missionName, bool enemyControl, BattleCapabilityReader knowledge,
        string xmlBriefing = "", bool briefingUnavailable = false, bool briefingFromBase = false,
        IReadOnlyDictionary<string, bool>? attackRanges = null)
    {
        if (enemyControl) throw new SituationUnavailableException("battle_disclosure");
        var ownObjects = Sequence(Member(player, "TaskforceObjects")).ToArray();
        var units = new JArray();
        var unitReferences = new List<(string Id, int UnitId)>();
        var contacts = new JArray();
        var launchedWeapons = new JArray();
        var identities = new List<(object Object, string Id)>();
        var movementUnits = new List<(object Object, string Id)>();
        var ownWeapons = new List<(object Object, JObject Row)>();
        var ownWeaponIds = new List<(object Object, int UnitId)>();
        var weaponContacts = new List<(object MapUnit, JObject Row)>();
        var capabilities = new JArray();
        var threats = new JArray();
        var bases = new JArray();
        var aviation = new BattleAviationReader(knowledge.References, now.Year);
        foreach (var mapUnit in Sequence(knownUnits))
        {
            var vehicle = Required(mapUnit, "Unit");
            // Compare identity before reading own-unit capabilities; contacts below use map/display values.
            var identity = Member(vehicle, "Object");
            if (identity != null && ownObjects.Any(o => ReferenceEquals(o, identity)))
            {
                // Launched weapons are captured separately; they do not use platform capabilities.
                if (IsWeapon(identity))
                {
                    ownWeaponIds.Add((identity, Convert.ToInt32(Required(vehicle, "Id"), CultureInfo.InvariantCulture)));
                    continue;
                }
                var kind = Code(Required(identity, "_type"));
                var parameters = Required(identity, "_obp");
                var subtype = kind == "landunit" ? Code(Required(parameters, "_landUnitSubType")) : null;
                var category = BattleInformation.UnitCategory(subtype);
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
                var verticalField = BattleVerticalPosition.FieldName(kind);
                if (verticalField != null)
                    row[verticalField] = BattleVerticalPosition.Read(kind, Required(Required(identity, "Altitude"), "Value"));
                unitReferences.Add(((string)row["id"]!, Convert.ToInt32(Required(vehicle, "Id"), CultureInfo.InvariantCulture)));
                identities.Add((identity, (string)row["id"]!));
                movementUnits.Add((identity, (string)row["id"]!));
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
            // MapContactVisuals.Resolve reads this same flag to display destroyed contacts.
            var destroyed = (bool)Required(Required(vehicle, "BaseObject"), "IsDestroyed");
            var positionEstimate = Member(vehicle, "PositionEstimate");
            var classKnown = Member(vehicle, "Class") != null;
            var contactKind = Code(Member(mapUnit, "VisualType"));
            var displayName = ContactDisplayName(mapUnit);
            var identified = Reactive<bool>(vehicle, "Identified");
            var observedClass = identified && classKnown ? (string)Required(Required(vehicle, "Class"), "Value") : null;
            var reference = knowledge.References.ClassReference(observedClass);
            contacts.Add(new JObject
            {
                ["id"] = "observed_contact_" + (contacts.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["track_number"] = Convert.ToInt32(Required(vehicle, "Id"), CultureInfo.InvariantCulture),
                ["display_name"] = displayName,
                ["kind"] = contactKind,
                ["posture"] = Code(Member(mapUnit, "VisualPosture")),
                ["identified"] = identified,
                ["class_display_name"] = reference["class_display_name"]?.DeepClone() ?? JValue.CreateNull(),
                ["public_class_reference_available"] = (string?)reference["status"] == "available",
                ["class_known"] = classKnown,
                ["position"] = positionEstimate != null ? Position(Required(positionEstimate, "Item1")) : JValue.CreateNull(),
                ["position_source"] = positionEstimate == null ? "unknown" : (bool)Required(vehicle, "HasLiveTrack") ? "observed_estimate" : "extrapolated_estimate",
                ["detection_methods"] = BattleSensorReader.DetectionMethods(vehicle),
                ["dormant"] = Reactive<bool>(vehicle, "IsDormant"),
                ["destroyed"] = destroyed,
                ["observation_time_utc"] = JValue.CreateNull(),
                ["position_error_nm"] = JValue.CreateNull()
            });
            var contact = (JObject)contacts.Last!;
            if (attackRanges != null && attackRanges.TryGetValue("unit:" + Convert.ToInt32(Required(vehicle, "Id"), CultureInfo.InvariantCulture), out var inRange))
                contact["has_in_range_attack_option"] = inRange;
            unitReferences.Add(((string)contact["id"]!, (int)contact["track_number"]!));
            if (identity != null) identities.Add((identity, (string)contact["id"]!));
            if (BattleWeaponReader.IsKind(contactKind)) weaponContacts.Add((mapUnit, contact));
            BattleContactMotionReader.CopyTo(contact, vehicle);
            reference["id"] = contacts.Last!["id"]!.DeepClone();
            reference["display_name"] = displayName;
            reference["observed_posture"] = contacts.Last!["posture"]!.DeepClone();
            reference["destroyed"] = destroyed;
            threats.Add(reference);
        }
        var movement = BattleMovementReader.Capture(movementUnits);
        foreach (var weapon in ownObjects.Where(IsWeapon))
        {
            var row = BattleWeaponReader.CaptureOwn(weapon, "own_weapon_" + (launchedWeapons.Count + 1).ToString(CultureInfo.InvariantCulture));
            if (row == null) continue;
            launchedWeapons.Add(row);
            var knownWeapon = ownWeaponIds.FirstOrDefault(item => ReferenceEquals(item.Object, weapon));
            if (knownWeapon.Object != null) unitReferences.Add(((string)row["id"]!, knownWeapon.UnitId));
            identities.Add((weapon, (string)row["id"]!));
            ownWeapons.Add((weapon, row));
        }
        string? Resolve(object target) => identities.FirstOrDefault(i => ReferenceEquals(i.Object, target)).Id;
        foreach (var weapon in ownWeapons) BattleWeaponReader.AddOwnTargets(weapon.Row, weapon.Object, Resolve);
        foreach (var contact in weaponContacts) BattleWeaponReader.AddContactTarget(contact.Row, contact.MapUnit, Resolve);
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
            ["mission_map_areas"] = BattleMissionAreaReader.Capture(missionAreas),
            ["mission_briefing"] = BattleBriefingReader.Combine(Reactive<string>(eventLog, "MissionBriefing"), xmlBriefing),
            ["mission_briefing_unavailable"] = briefingUnavailable,
            ["mission_briefing_source"] = string.IsNullOrWhiteSpace(xmlBriefing) ? "event_log" :
                briefingFromBase ? "base_mission_xml" : "mission_xml",
            ["friendly_units"] = units,
            ["friendly_launched_weapons"] = launchedWeapons,
            ["unit_capabilities"] = capabilities,
            ["movement_plans"] = movement.Plans,
            ["movement_routes"] = movement.Routes,
            ["contact_threat_references"] = threats,
            ["friendly_bases"] = bases,
            ["hosted_air_units"] = aviation.Units,
            ["aviation_hosts"] = aviation.Hosts,
            ["weapon_references"] = knowledge.References.SnapshotWeapons(),
            ["sensor_references"] = knowledge.SensorReferences(),
            ["observed_contacts"] = contacts,
            ["published_reports"] = reports
        }, new AdvisorUnitReferences(unitReferences));
    }

    internal static string ContactDisplayName(object mapUnit)
    {
        var vehicle = Required(mapUnit, "Unit");
        var model = Member(vehicle, "Class") != null && !BattleWeaponReader.IsKind(Code(Member(mapUnit, "VisualType")))
            ? Member(mapUnit, "ObjectViewModel") : null;
        return model != null ? Reactive<string>(model, "ObjectName")
            : "Contact " + Convert.ToString(Required(vehicle, "Id"), CultureInfo.InvariantCulture);
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
