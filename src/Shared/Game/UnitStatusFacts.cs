using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor.Shared;

// Detached player-unit facts. Callers establish ownership before reading a native object.
internal static class UnitStatusFacts
{
    internal static JObject Fuel(object unit, object parameters)
    {
        var kind = Code(Required(unit, "_type"));
        if (kind != "aircraft" && kind != "helicopter")
            return new JObject { ["status"] = "not_displayed_for_unit_type", ["remaining_mass_kg"] = JValue.CreateNull() };
        var mass = Nonnegative(Required(parameters, "_currentFuelMass"));
        var capacity = Nonnegative(Required(parameters, "_maxFuel"));
        var internalCapacity = Nonnegative(Required(parameters, "_defaultMaxFuel"));
        var fuel = new JObject
        {
            ["status"] = (bool)Required(unit, "_unlimitedFuel") ? "unlimited" : "measured",
            ["source"] = "player_unit_fuel_display",
            ["remaining_mass_kg"] = mass, ["capacity_mass_kg"] = capacity,
            ["internal_capacity_mass_kg"] = internalCapacity,
            ["percent_of_internal_capacity"] = internalCapacity > 0 ? new JValue(100 * mass / internalCapacity) : JValue.CreateNull(),
            ["estimated_flight_time_left_seconds"] = Math.Max(0, Number(Required(Required(unit, "FlightTimeLeftInSec"), "Value")))
        };
        return fuel;
    }

    internal static JObject Damage(object unit, object parameters)
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
    internal static JArray Sensors(object parameters)
    {
        var rows = new JArray();
        foreach (var system in Rows(Required(parameters, "_systems")))
        {
            var kind = SensorKind(system); if (kind == null) continue;
            var sonar = kind == "sonar" ? Required(system, "_sonar") : null;
            if (sonar != null && (bool)Required(Required(sonar, "_sp"), "_isActiveIntercept")) continue;
            var row = new JObject { ["display_name"] = (string)Required(Required(system, "DisplayName"), "Value"), ["kind"] = kind,
                ["on"] = (bool)Required(Required(system, "IsOn"), "Value"), ["inoperable"] = (bool)Required(Required(system, "Inoperable"), "Value") };
            if (sonar != null) row["active_mode"] = (bool)Required(Required(sonar, "IsActive"), "Value");
            rows.Add(row);
        }
        return rows;
    }
    internal static string? SensorKind(object system)
    {
        for (var type = system.GetType(); type != null; type = type.BaseType)
            switch (type.Name)
            {
                case "SensorSystemRadar": return "radar";
                case "SensorSystemSonar": return "sonar";
                case "SensorSystemVisual": return "visual";
                case "SensorSystemESM": return "esm";
                case "SensorSystemMAD": return "mad";
            }
        return null;
    }
}
