using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Called only after the map object has been matched by reference to the player's taskforce.
internal sealed class BattleCapabilityReader
{
    internal BattleReferenceCatalog References { get; }
    internal BattleCapabilityReader(BattleReferenceCatalog references) { References = references; }

    internal JObject CaptureOwn(object unit, string id, string name)
    {
        var parameters = Required(unit, "_obp");
        var counts = Rows(Required(unit, "AmmunitionAmountDictionary"))
            .ToDictionary(p => (string)Required(p, "Key"), p => Count(Required(p, "Value")), StringComparer.Ordinal);
        var ammunition = Rows(Required(unit, "AmmunitionNameToAmmunitionDictionary"))
            .ToDictionary(p => (string)Required(p, "Key"), p => Required(p, "Value"), StringComparer.Ordinal);
        var weapons = new JArray();
        foreach (var key in counts.Keys.Union(ammunition.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var displayName = ammunition.TryGetValue(key, out var ammo)
                ? (string)Required(Required(ammo, "_ap"), "_displayedName") : References.WeaponName(key);
            weapons.Add(new JObject
            {
                ["display_name"] = displayName,
                ["reference_id"] = References.RegisterWeapon(key),
                ["remaining_count"] = counts.TryGetValue(key, out var count) ? count : 0
            });
        }
        return new JObject
        {
            ["id"] = id, ["display_name"] = name,
            ["fuel"] = Fuel(unit, parameters), ["damage"] = Damage(unit, parameters),
            ["weapons"] = new JObject
            {
                ["source"] = "player_unit_ammunition_display",
                ["count_meaning"] = "current_inventory_not_ready_to_fire_count",
                ["fire_control_state"] = Code(Required(Required(unit, "WeaponState"), "Value")),
                ["ammunition"] = weapons,
                ["attack_feasibility_known"] = false
            }
        };
    }

    private static JObject Fuel(object unit, object parameters)
    {
        var kind = Code(Required(unit, "_type"));
        if (kind != "aircraft" && kind != "helicopter")
            return new JObject { ["status"] = "not_displayed_for_unit_type", ["remaining_mass_kg"] = JValue.CreateNull() };
        var mass = Nonnegative(Required(parameters, "_currentFuelMass"));
        var capacity = Nonnegative(Required(parameters, "_maxFuel"));
        var internalCapacity = Nonnegative(Required(parameters, "_defaultMaxFuel"));
        return new JObject
        {
            ["status"] = (bool)Required(unit, "_unlimitedFuel") ? "unlimited" : "measured",
            ["source"] = "player_unit_fuel_display",
            ["remaining_mass_kg"] = mass, ["capacity_mass_kg"] = capacity,
            ["internal_capacity_mass_kg"] = internalCapacity,
            ["percent_of_internal_capacity"] = internalCapacity > 0 ? new JValue(100 * mass / internalCapacity) : JValue.CreateNull(),
            ["estimated_flight_time_left_seconds"] = Math.Max(0, Number(Required(Required(unit, "FlightTimeLeftInSec"), "Value")))
        };
    }

    private static JObject Damage(object unit, object parameters)
    {
        var compartments = Member(unit, "Compartments");
        var damaged = new JArray();
        foreach (var system in Rows(Required(parameters, "_systems")))
        {
            var state = Code(Required(system, "CurrentDamageType"));
            if (state == "no") continue;
            damaged.Add(new JObject
            {
                ["display_name"] = (string)Required(Required(system, "DisplayName"), "Value"),
                ["damage_state"] = state, ["repairable"] = (bool)Required(system, "Repairable")
            });
        }
        return new JObject
        {
            ["source"] = "player_unit_damage_control",
            ["destroyed"] = (bool)Required(unit, "IsDestroyed"), ["disabled"] = (bool)Required(unit, "Disabled"),
            ["hull_status"] = compartments == null ? "not_applicable" : "measured",
            ["hull_integrity_percent"] = compartments == null ? JValue.CreateNull() : new JValue(Percent(Required(compartments, "IntegrityPercentage"))),
            ["flooding_percent"] = compartments == null ? JValue.CreateNull() : new JValue(Percent(Required(compartments, "FloodingPercentage"))),
            ["damaged_systems"] = damaged
        };
    }

    private static double Percent(object value)
    {
        var number = Nonnegative(value);
        if (number > 100.001) throw new InvalidOperationException("Invalid battle damage percentage.");
        return Math.Min(100, number);
    }
    private static int Count(object value)
    {
        var count = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (count < 0) throw new InvalidOperationException("Negative displayed ammunition count.");
        return count;
    }
}
