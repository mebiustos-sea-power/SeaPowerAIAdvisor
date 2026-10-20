using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Public encyclopedia definitions only. This class never receives an enemy unit or track object.
internal sealed class BattleReferenceCatalog
{
    private readonly Dictionary<string, object[]> classes;
    private readonly Dictionary<string, object> weapons;
    private readonly Dictionary<string, JObject> registered = new(StringComparer.Ordinal);
    private readonly int year;
    private readonly bool ready;
    internal BattleReferenceCatalog(IEnumerable<object> classes, IEnumerable<object> weapons, int year, bool ready)
    {
        this.year = year; this.ready = ready;
        this.classes = classes.Where(Public).GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        this.weapons = weapons.Where(Public).GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => Convert.ToInt32(Required(e, "VariantIndex"))).First(), StringComparer.Ordinal);
    }
    private static bool Public(object entry) => !(bool)Required(entry, "HiddenInEncyclopedia");
    private static string Key(object entry) => (string)Required(entry, "IniRef");
    internal static object? FindPublicWeapon(IEnumerable<object> entries, string key) =>
        entries.Where(Public).Where(entry => Key(entry) == key)
            .OrderBy(entry => Convert.ToInt32(Required(entry, "VariantIndex"))).FirstOrDefault();
    internal string WeaponName(string key) => weapons.TryGetValue(key, out var entry) ? (string)Required(entry, "DisplayName") : key;
    internal JArray SnapshotWeapons() => new(registered.Values.Select(r => r.DeepClone()));

    internal string RegisterWeapon(string key)
    {
        if (registered.TryGetValue(key, out var saved)) return (string)saved["id"]!;
        var id = "weapon_reference_" + (registered.Count + 1);
        var row = new JObject { ["id"] = id, ["reference_key"] = key, ["source"] = "reference_specifications" };
        if (!ready || !weapons.TryGetValue(key, out var entry))
        {
            row["status"] = ready ? "no_public_entry" : "reference_unavailable";
            registered.Add(key, row);
            return id;
        }
        row["status"] = "available";
        row["display_name"] = (string)Required(entry, "DisplayName");
        row["type"] = Code(Required(entry, "Type"));
        row["primary_target"] = Code(Required(entry, "TargetType"));
        row["secondary_target"] = (bool)Required(entry, "HasSecondaryTargetType") ? new JValue(Code(Required(entry, "SecondaryTargetType"))) : JValue.CreateNull();
        row["can_attack_surface"] = (bool)Required(entry, "CanAttackSurface");
        row["can_attack_air"] = (bool)Required(entry, "CanAttackAir");
        row["minimum_launch_range_nm"] = Distance(entry, "MinLaunchRange");
        row["maximum_launch_range_nm"] = Distance(entry, "MaxLaunchRange");
        row["gun_anti_air_range_nm"] = Distance(entry, "GunAaRange");
        row["guidance"] = Code(Required(entry, "GuidanceType"));
        var parameters = Member(entry, "Ap");
        row["fire_control_horizon_limited"] = parameters == null ? JValue.CreateNull() : new JValue((bool)Required(parameters, "IsFcrHorizonLimited"));
        row["requires_direct_line_of_sight"] = parameters == null ? JValue.CreateNull() : new JValue((bool)Required(parameters, "RequiresDirectLineOfSight"));
        row["secondary_target_range_nm"] = JValue.CreateNull();
        row["service_entry_year"] = Convert.ToInt32(Required(entry, "IocDate"));
        row["range_meaning"] = "reference_launch_envelope_not_current_engagement_feasibility";
        registered.Add(key, row);
        return id;
    }

    internal JObject ClassReference(string? observedClass)
    {
        var result = new JObject
        {
            ["source"] = "reference_specifications", ["evidence_kind"] = "public_class_reference",
            ["actual_enemy_state_included"] = false,
            ["unknowns"] = new JArray("actual_loadout", "remaining_ammunition", "damage", "fuel", "readiness", "fire_control", "current_attack_feasibility")
        };
        if (observedClass == null) { result["status"] = "unidentified_class"; return result; }
        if (!ready) { result["status"] = "reference_unavailable"; return result; }
        if (!classes.TryGetValue(observedClass, out var entries)) { result["status"] = "no_public_class_entry"; return result; }
        var eligible = entries.Where(e => InService(e, "ServiceEntryDate")).ToArray();
        if (eligible.Length == 0) { result["status"] = "no_public_variant_for_date"; return result; }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in eligible)
        {
            foreach (var weapon in Rows(Required(entry, "WeaponEntries")))
                if (Public(weapon) && InService(weapon, "IocDate")) keys.Add(Key(weapon));
            foreach (var pair in Rows(Required(entry, "DateBasedAlternatives")))
                foreach (var alternative in Rows(Required(pair, "Value")))
                    if (Convert.ToInt32(Required(alternative, "Item1")) <= year)
                    {
                        var key = (string)Required(alternative, "Item2");
                        if (weapons.TryGetValue(key, out var weapon) && InService(weapon, "IocDate")) keys.Add(key);
                    }
            var mounts = Rows(Required(entry, "WeaponSystemEntries")).ToList();
            foreach (var pair in Rows(Required(entry, "LoadoutWeaponSystemEntries")))
                mounts.AddRange(Rows(Required(pair, "Value")));
            foreach (var mount in mounts)
                foreach (var reference in Rows(Required(mount, "AmmoRefs")))
                {
                    var key = (string)reference;
                    if (weapons.TryGetValue(key, out var weapon) && InService(weapon, "IocDate")) keys.Add(key);
                }
        }
        result["status"] = "available";
        var label = (string?)Member(eligible[0], "DisplayClassName");
        result["class_display_name"] = string.IsNullOrWhiteSpace(label) ? (string)Required(eligible[0], "DisplayName") : label;
        result["reference_key"] = observedClass;
        result["reference_variant_count"] = eligible.Length;
        result["armament_meaning"] = "possible_public_configurations_not_observed_armament";
        result["armament_coverage"] = "available_public_references_not_an_exhaustive_guarantee";
        result["possible_weapon_reference_ids"] = new JArray(keys.OrderBy(k => k, StringComparer.Ordinal).Select(RegisterWeapon));
        return result;
    }

    private bool InService(object entry, string field)
    {
        var firstYear = Convert.ToInt32(Required(entry, field));
        return firstYear <= 0 || firstYear <= year;
    }
    private static JToken Distance(object entry, string field)
    {
        var raw = Convert.ToDouble(Required(Required(entry, field), "InNauticalMiles"), System.Globalization.CultureInfo.InvariantCulture);
        // The encyclopedia uses Length.NaN for a range that is not specified or not applicable.
        if (double.IsNaN(raw)) return JValue.CreateNull();
        var distance = Number(raw);
        if (distance < 0) throw new InvalidOperationException("Negative encyclopedia range.");
        return new JValue(distance);
    }
}
