using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Called only after the map object has been matched by reference to the player's taskforce.
internal sealed class BattleCapabilityReader
{
    internal BattleReferenceCatalog References { get; }
    private readonly BattleSensorReader sensors = new();
    private readonly Func<float, float> speedOfSound;
    internal JArray SensorReferences() => sensors.References();
    internal BattleCapabilityReader(BattleReferenceCatalog references, Func<float, float> speedOfSound)
    { References = references; this.speedOfSound = speedOfSound; }

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
        var sensorData = sensors.Capture(parameters, id);
        return new JObject
        {
            ["id"] = id, ["display_name"] = name,
            ["sensors"] = sensorData.State, ["sensor_performance"] = sensorData.Performance,
            ["fuel"] = Fuel(unit, parameters), ["damage"] = UnitStatusFacts.Damage(unit, parameters),
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

    private JObject Fuel(object unit, object parameters)
    {
        var fuel = UnitStatusFacts.Fuel(unit, parameters);
        if (Code(Required(unit, "_type")) == "aircraft") fuel["economic_cruise"] = BattleAircraftCruise.Capture(parameters, speedOfSound);
        return fuel;
    }
    private static int Count(object value)
    {
        var count = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (count < 0) throw new InvalidOperationException("Negative displayed ammunition count.");
        return count;
    }
}
