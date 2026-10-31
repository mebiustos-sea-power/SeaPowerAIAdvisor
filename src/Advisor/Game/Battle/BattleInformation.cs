using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Apply unit and contact permissions once to the detached snapshot. All query routes use this projection.
internal static class BattleInformation
{
    internal static void Apply(JObject snapshot, AdvisorInformation information)
    {
        var units = (JArray)snapshot["friendly_units"]!;
        foreach (var unit in units.OfType<JObject>().Where(u => !Permitted(u, information)).ToArray()) unit.Remove();
        var ids = units.OfType<JObject>().Select(u => (string)u["id"]!).ToHashSet(System.StringComparer.Ordinal);
        foreach (var collection in new[] { "unit_capabilities", "friendly_bases" })
            foreach (var row in ((JArray)snapshot[collection]!).OfType<JObject>().Where(r => !ids.Contains((string)r["id"]!)).ToArray()) row.Remove();
        FilterMovement(snapshot, ids, information.Allows(InformationKind.BattleMovementPlans));
        foreach (var collection in new[] { "hosted_air_units", "aviation_hosts" })
            foreach (var row in ((JArray)snapshot[collection]!).OfType<JObject>().Where(r =>
                !information.Allows(InformationKind.BattleAviation) || !ids.Contains((string)r["host_unit_id"]!)).ToArray()) row.Remove();
        if (!information.Allows(InformationKind.BattleWeapons))
            foreach (var row in ((JArray)snapshot["hosted_air_units"]!).OfType<JObject>()) row.Remove("loadouts");
        if (!information.Allows(InformationKind.BattleAviation))
            foreach (var row in ((JArray)snapshot["friendly_bases"]!).OfType<JObject>()) row.Remove("has_aviation_facility");
        foreach (var capability in ((JArray)snapshot["unit_capabilities"]!).OfType<JObject>())
        {
            if (!information.Allows(InformationKind.BattleSensors)) capability.Remove("sensors");
            if (!information.Allows(InformationKind.BattleSensorPerformance)) capability.Remove("sensor_performance");
        }
        var sensorIds = ((JArray)snapshot["unit_capabilities"]!).OfType<JObject>()
            .SelectMany(c => c["sensor_performance"] as JArray ?? new JArray())
            .Select(s => (string)s["reference_id"]!).ToHashSet(System.StringComparer.Ordinal);
        foreach (var reference in ((JArray)snapshot["sensor_references"]!).OfType<JObject>()
            .Where(s => !sensorIds.Contains((string)s["id"]!)).ToArray()) reference.Remove();
        var contacts = (JArray)snapshot["observed_contacts"]!;
        foreach (var contact in contacts.OfType<JObject>().Where(c =>
            !information.Allows(InformationKind.BattleContacts) ||
            (BattleWeaponReader.IsKind((string)c["kind"]!) && !information.Allows(InformationKind.BattleWeaponContacts)) ||
            ((string?)c["posture"] == "neutral" && !information.Allows(InformationKind.BattleNeutralContacts)) ||
            ((bool?)c["destroyed"] == true && !information.Allows(InformationKind.BattleDestroyedContacts))).ToArray()) contact.Remove();
        if (!information.Allows(InformationKind.BattleUnits) || !information.Allows(InformationKind.BattleWeapons))
            foreach (var contact in contacts.OfType<JObject>()) contact.Remove("has_in_range_attack_option");
        var weapons = (JArray)snapshot["friendly_launched_weapons"]!;
        if (!information.Allows(InformationKind.BattleLaunchedWeapons)) weapons.Clear();
        var visibleIds = ids.Concat(contacts.OfType<JObject>().Select(c => (string)c["id"]!))
            .Concat(weapons.OfType<JObject>().Select(w => (string)w["id"]!)).ToHashSet(System.StringComparer.Ordinal);
        foreach (var row in weapons.Concat(contacts).OfType<JObject>())
        {
            if (row["launch_platform_id"]?.Type == JTokenType.String && !visibleIds.Contains((string)row["launch_platform_id"]!))
                row["launch_platform_id"] = JValue.CreateNull();
            if (row["targeting"] is not JObject targeting) continue;
            foreach (var key in new[] { "tracked_target_id", "commanded_target_id" })
            {
                if (targeting[key]?.Type != JTokenType.String || visibleIds.Contains((string)targeting[key]!)) continue;
                targeting[key] = JValue.CreateNull();
                targeting[key == "tracked_target_id" ? "tracking_status" : "commanded_target_status"] = "unknown";
            }
        }
        var contactIds = contacts.OfType<JObject>().Select(c => (string)c["id"]!).ToHashSet(System.StringComparer.Ordinal);
        foreach (var reference in ((JArray)snapshot["contact_threat_references"]!).OfType<JObject>()
            .Where(r => !contactIds.Contains((string)r["id"]!)).ToArray()) reference.Remove();
    }
    internal static string UnitCategory(string? landSubtype) => landSubtype == null ? "mobile_platform" :
        new[] { "airbase", "port", "installation", "bridge", "oilrig" }.Contains(landSubtype) ? "base" : "ground_unit";
    private static bool Permitted(JObject unit, AdvisorInformation information) => Permitted((string?)unit["unit_category"], information);
    internal static bool Permitted(string? category, AdvisorInformation information) =>
        information.Allows(InformationKind.BattleUnits) && (category switch
        {
            "ground_unit" => information.Allows(InformationKind.BattleGroundUnits),
            "base" => information.Allows(InformationKind.BattleBases),
            _ => true
        });
    private static void FilterMovement(JObject snapshot, HashSet<string> allowedIds, bool enabled)
    {
        var plans = (JArray)snapshot["movement_plans"]!;
        var routes = (JArray)snapshot["movement_routes"]!;
        if (!enabled) { plans.Clear(); routes.Clear(); return; }
        foreach (var row in plans.OfType<JObject>().Where(p => !allowedIds.Contains((string)p["id"]!)).ToArray()) row.Remove();
        foreach (var row in routes.OfType<JObject>().Where(p => !allowedIds.Contains((string)p["id"]!)).ToArray()) row.Remove();
        foreach (var plan in plans.OfType<JObject>())
        {
            HideReference(plan, "formation_leader_id", allowedIds);
            HideReference(plan, "route_unit_id", allowedIds);
        }
        foreach (var point in routes.OfType<JObject>().SelectMany(r => (JArray)r["waypoints"]!).OfType<JObject>())
            foreach (var key in new[] { "relative_to_unit_id", "target_unit_id" })
                if (HideReference(point, key, allowedIds)) point["position"] = JValue.CreateNull();
    }
    private static bool HideReference(JObject row, string key, HashSet<string> ids)
    {
        if (row[key]?.Type != JTokenType.String || ids.Contains((string)row[key]!)) return false;
        row[key] = JValue.CreateNull();
        return true;
    }
}
