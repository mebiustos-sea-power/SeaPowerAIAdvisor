using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Copies only currently player-owned bases and player-side ground units. No inspector or cheat UI is invoked.
internal static class CampaignGroundReader
{
    internal static CampaignGroundSnapshot Capture(object sim, object[] ownForces)
    {
        var state = Read<object>(sim, "State");
        var definition = Read<object>(state, "Definition");
        var side = Read<string>(state, "PlayerSideId");
        var catalogue = Read<object>(sim, "Catalog");
        var definitions = Rows(Read<object>(definition, "bases"));
        var owners = Read<IDictionary>(state, "BaseOwner");
        var stocks = Read<IDictionary>(state, "Stocks");
        var garrisons = Read<IDictionary>(state, "Garrisons");
        var bases = new JArray();
        for (var i = 0; i < definitions.Length; i++)
        {
            var b = definitions[i];
            var id = Read<string>(b, "id");
            if (!owners.Contains(id) || (string?)owners[id] != side) continue;
            var stock = stocks.Contains(id) ? stocks[id] : null;
            var ammunition = new JArray();
            if (stock != null)
                foreach (DictionaryEntry pair in Read<IDictionary>(stock, "Ammo"))
                    ammunition.Add(new JObject
                    {
                        ["display_name"] = (string)Call(catalogue, "AmmoName", (string)pair.Key)!,
                        ["count"] = Nonnegative(pair.Value!)
                    });
            bases.Add(new JObject
            {
                ["id"] = SnapshotIds.Base(i), ["display_name"] = Read<string>(b, "name"),
                ["source"] = "player_owned_campaign_base",
                ["aircraft_capacity"] = Nonnegative(Read<int>(b, "aircraftCapacity")),
                ["has_shipyard"] = Read<bool>(b, "shipyard"),
                ["garrison_strength"] = garrisons.Contains(id) ? new JValue(Nonnegative(garrisons[id]!)) : JValue.CreateNull(),
                ["garrison_strength_meaning"] = "campaign_strength_not_a_count_of_tactical_units",
                ["stocks"] = new JObject
                {
                    ["status"] = stock == null ? "not_recorded" : "known",
                    ["ship_fuel_tonnes"] = stock == null ? JValue.CreateNull() : new JValue(Nonnegative(Read<double>(stock, "ShipFuelT"))),
                    ["aviation_fuel_tonnes"] = stock == null ? JValue.CreateNull() : new JValue(Nonnegative(Read<double>(stock, "JetFuelT"))),
                    ["ammunition"] = stock == null ? JValue.CreateNull() : ammunition
                }
            });
        }
        var ground = new JArray();
        foreach (var unit in Rows(Read<object>(state, "GroundUnits")))
        {
            if (Read<string>(unit, "SideId") != side) continue;
            var baseId = Value(unit, "BaseId") as string;
            if (string.IsNullOrEmpty(baseId)) baseId = null;
            var baseIndex = baseId == null ? -1 : Array.FindIndex(definitions, b => Read<string>(b, "id") == baseId);
            if (baseId != null && baseIndex < 0) throw new InvalidOperationException("An own ground unit has an unknown base.");
            var shipId = Read<int>(unit, "ShipId");
            string? shipReference = null, forceReference = null;
            if (shipId != 0)
            {
                for (var f = 0; f < ownForces.Length; f++)
                {
                    var ships = Rows(Read<object>(ownForces[f], "Ships"));
                    var shipIndex = Array.FindIndex(ships, h => Read<int>(h, "Id") == shipId);
                    if (shipIndex < 0) continue;
                    shipReference = SnapshotIds.Ship(f, shipIndex); forceReference = SnapshotIds.Force(f); break;
                }
                if (shipReference == null) throw new InvalidOperationException("An embarked own ground unit has no own ship.");
            }
            ground.Add(new JObject
            {
                ["id"] = "ground_unit_" + (ground.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["display_name"] = Read<string>(unit, "Name"), ["source"] = "player_campaign_ground_unit",
                ["strength"] = Nonnegative(Read<double>(unit, "Strength")),
                ["maximum_strength"] = Nonnegative(Read<double>(unit, "MaxStrength")),
                ["base_id"] = baseIndex < 0 ? JValue.CreateNull() : new JValue(SnapshotIds.Base(baseIndex)),
                ["force_id"] = forceReference == null ? JValue.CreateNull() : new JValue(forceReference),
                ["ship_id"] = shipReference == null ? JValue.CreateNull() : new JValue(shipReference),
                ["embarked"] = shipId != 0,
                ["strength_meaning"] = "campaign_strength_not_a_count_of_tactical_units"
            });
        }
        return new CampaignGroundSnapshot(bases, ground);
    }
    private static double Nonnegative(object value)
    {
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            throw new InvalidOperationException("Invalid player campaign ground/base value.");
        return number;
    }
    private static object? Value(object source, string name) => (source.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingFieldException(source.GetType().FullName, name)).GetValue(source);
    private static T Read<T>(object source, string name) => (T)(Value(source, name)
        ?? throw new InvalidOperationException(source.GetType().FullName + "." + name + " is null."));
    private static object[] Rows(object value) => ((IEnumerable)value).Cast<object>().ToArray();
    private static object? Call(object source, string name, params object[] args) =>
        (source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(source.GetType().FullName, name)).Invoke(source, args);
}

internal sealed class CampaignGroundSnapshot
{
    private readonly JArray bases, units;
    internal static readonly CampaignGroundSnapshot Empty = new(new JArray(), new JArray());
    internal CampaignGroundSnapshot(JArray bases, JArray units)
    { this.bases = (JArray)bases.DeepClone(); this.units = (JArray)units.DeepClone(); }
    internal JArray Bases() => (JArray)bases.DeepClone();
    internal JArray Units() => (JArray)units.DeepClone();
}
