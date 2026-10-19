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
        foreach (var collection in new[] { "hosted_air_units", "aviation_hosts" })
            foreach (var row in ((JArray)snapshot[collection]!).OfType<JObject>().Where(r =>
                !information.Allows(InformationKind.BattleAviation) || !ids.Contains((string)r["host_unit_id"]!)).ToArray()) row.Remove();
        if (!information.Allows(InformationKind.BattleWeapons))
            foreach (var row in ((JArray)snapshot["hosted_air_units"]!).OfType<JObject>()) row.Remove("loadouts");
        if (!information.Allows(InformationKind.BattleAviation))
            foreach (var row in ((JArray)snapshot["friendly_bases"]!).OfType<JObject>()) row.Remove("has_aviation_facility");
        var contacts = (JArray)snapshot["observed_contacts"]!;
        foreach (var contact in contacts.OfType<JObject>().Where(c =>
            !information.Allows(InformationKind.BattleContacts) ||
            ((string?)c["posture"] == "neutral" && !information.Allows(InformationKind.BattleNeutralContacts)) ||
            ((bool?)c["destroyed"] == true && !information.Allows(InformationKind.BattleDestroyedContacts))).ToArray()) contact.Remove();
        var contactIds = contacts.OfType<JObject>().Select(c => (string)c["id"]!).ToHashSet(System.StringComparer.Ordinal);
        foreach (var reference in ((JArray)snapshot["contact_threat_references"]!).OfType<JObject>()
            .Where(r => !contactIds.Contains((string)r["id"]!)).ToArray()) reference.Remove();
    }
    private static bool Permitted(JObject unit, AdvisorInformation information) =>
        information.Allows(InformationKind.BattleUnits) && ((string?)unit["unit_category"] switch
        {
            "ground_unit" => information.Allows(InformationKind.BattleGroundUnits),
            "base" => information.Allows(InformationKind.BattleBases),
            _ => true
        });
}
