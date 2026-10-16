using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Reads the player's map projection and published mission UI, never scenario definitions or enemy objects.
internal sealed class BattleSituationReader
{
    internal BattleSnapshot Capture(object knownUnits, object player, object objectives, object eventLog,
        DateTime now, string missionName, bool enemyControl)
    {
        if (enemyControl) throw new SituationUnavailableException("battle_disclosure");
        var ownObjects = Sequence(Member(player, "TaskforceObjects")).ToArray();
        var units = new JArray();
        var contacts = new JArray();
        foreach (var mapUnit in Sequence(knownUnits))
        {
            var vehicle = Required(mapUnit, "Unit");
            // Compare references only; an observed enemy's Object is never dereferenced for details.
            var identity = Member(vehicle, "Object");
            if (identity != null && ownObjects.Any(o => ReferenceEquals(o, identity)))
            {
                units.Add(new JObject
                {
                    ["id"] = "own_unit_" + (units.Count + 1).ToString(CultureInfo.InvariantCulture),
                    ["display_name"] = Reactive<string>(identity, "Name"),
                    ["kind"] = Code(Member(identity, "_type")),
                    ["position"] = Position(Required(Required(identity, "Position"), "Value")),
                    ["heading_deg"] = Number(Required(Required(identity, "Heading"), "Value")),
                    ["speed_knots"] = Number(Required(identity, "_velocityInKnots"))
                });
                continue;
            }
            var positionEstimate = Member(vehicle, "PositionEstimate");
            var classKnown = Member(vehicle, "Class") != null;
            var viewModel = classKnown ? Member(mapUnit, "ObjectViewModel") : null;
            var displayName = classKnown && viewModel != null
                ? Reactive<string>(viewModel, "ObjectName")
                : "Contact " + Convert.ToString(Required(vehicle, "Id"), CultureInfo.InvariantCulture);
            contacts.Add(new JObject
            {
                ["id"] = "observed_contact_" + (contacts.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["display_name"] = displayName,
                ["kind"] = Code(Member(mapUnit, "VisualType")),
                ["posture"] = Code(Member(mapUnit, "VisualPosture")),
                ["identified"] = Reactive<bool>(vehicle, "Identified"),
                ["class_known"] = classKnown,
                ["position"] = positionEstimate != null ? Position(Required(positionEstimate, "Item1")) : JValue.CreateNull(),
                ["position_source"] = positionEstimate == null ? "unknown" : (bool)Required(vehicle, "HasLiveTrack") ? "observed_estimate" : "extrapolated_estimate",
                ["dormant"] = Reactive<bool>(vehicle, "IsDormant"),
                ["observation_time_utc"] = JValue.CreateNull(),
                ["position_error_nm"] = JValue.CreateNull()
            });
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
            ["mission_briefing"] = Reactive<string>(eventLog, "MissionBriefing"),
            ["friendly_units"] = units,
            ["observed_contacts"] = contacts,
            ["published_reports"] = reports
        });
    }

    private static JObject Position(object position) => AdvisorInputBuilder.Position(new MapPosition(
        Number(Required(position, "Latitude")), Number(Required(position, "Longitude"))));

    private static double Number(object value)
    {
        var result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(result) || double.IsInfinity(result)) throw new InvalidOperationException("Invalid displayed battle value.");
        return result;
    }

    private static string Code(object? value) => value != null
        ? value.ToString()!.ToLowerInvariant()
        : throw new InvalidOperationException("A displayed battle classification is missing.");
    private static T Reactive<T>(object value, string name) => (T)Required(Required(value, name), "Value");
    private static IEnumerable<object> Sequence(object? value) => (value as IEnumerable
        ?? throw new InvalidOperationException("A battle UI collection is unavailable.")).Cast<object>();
    private static object Required(object value, string name) => Member(value, name)
        ?? throw new InvalidOperationException(value.GetType().FullName + "." + name + " is unavailable.");

    internal static object? Member(object value, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = value.GetType();
        var property = type.GetProperty(name, flags);
        if (property != null) return property.GetValue(value);
        var field = type.GetField(name, flags) ?? throw new MissingMemberException(type.FullName, name);
        return field.GetValue(value);
    }
}
