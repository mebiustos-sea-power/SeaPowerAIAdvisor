using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// The caller has matched this host to the player's map and taskforce. Never pass an enemy host here.
internal sealed class BattleAviationReader
{
    private readonly BattleReferenceCatalog references;
    private readonly int year;
    private int taskSequence;
    internal JArray Units { get; } = new();
    internal JArray Hosts { get; } = new();
    internal BattleAviationReader(BattleReferenceCatalog references, int year) { this.references = references; this.year = year; }

    internal void Capture(object deck, string hostId, string hostName)
    {
        var displayedTasks = Rows(Required(deck, "FlightDeckTasks"));
        var types = Rows(Required(deck, "_vehiclesOnBoard")).Where(t => Count(Required(t, "Numbers")) > 0 ||
            displayedTasks.Any(task => ReferenceEquals(Member(task, "VehicleType"), t))).ToArray();
        var ids = Enumerable.Range(0, types.Length).Select(i => "hosted_air_unit_" + (Units.Count + i + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            var loadouts = new JArray();
            var displayedLoadouts = Count(Required(type, "Numbers")) == 0 ? Array.Empty<object>() :
                Rows(Required(type, "Loadouts")).Where(l => Convert.ToInt32(Required(l, "ServiceEntryYear"), CultureInfo.InvariantCulture) <= year).ToArray();
            foreach (var loadout in displayedLoadouts)
            {
                var ammunition = new JArray(Rows(Required(loadout, "AmmunitionAmounts")).Select(pair => new JObject
                {
                    ["reference_id"] = references.RegisterWeapon((string)Required(pair, "Key")),
                    ["display_name"] = references.WeaponName((string)Required(pair, "Key")),
                    ["count_per_aircraft"] = Count(Required(pair, "Value"))
                }));
                loadouts.Add(new JObject
                {
                    ["display_name"] = (string)Required(loadout, "DisplayName"),
                    ["preparation_duration_seconds"] = Nonnegative(Required(loadout, "_readyUpDuration")),
                    ["displayed_stock_availability"] = Count(Required(Required(loadout, "Available"), "Value")),
                    ["stock_availability_is_launch_readiness"] = false,
                    ["ammunition"] = ammunition
                });
            }
            Units.Add(new JObject
            {
                ["id"] = ids[i], ["host_unit_id"] = hostId, ["host_display_name"] = hostName,
                ["display_name"] = (string)Required(type, "DisplayName"),
                ["kind"] = Code(Required(type, "_objectType")),
                ["source"] = "player_flight_deck_aircraft_selector_or_tasks",
                ["unassigned_aircraft_count"] = Count(Required(type, "Numbers")),
                ["count_meaning"] = "unassigned_inventory_not_total_aircraft_or_immediate_launch_readiness",
                ["squadrons"] = new JArray(Rows(Required(type, "Squadrons")).Where(s => Count(Required(s, "Numbers")) > 0).Select(s => new JObject
                {
                    ["display_name"] = (string)Required(s, "DisplayName"),
                    ["unassigned_aircraft_count"] = Count(Required(s, "Numbers"))
                })),
                ["loadouts"] = loadouts
            });
        }
        var tasks = new JArray();
        foreach (var task in displayedTasks)
        {
            var vehicle = Member(task, "VehicleType");
            var index = Array.FindIndex(types, t => ReferenceEquals(t, vehicle));
            var kind = task.GetType().Name;
            tasks.Add(new JObject
            {
                ["id"] = "air_task_" + (++taskSequence).ToString(CultureInfo.InvariantCulture),
                ["host_unit_id"] = hostId,
                ["air_unit_id"] = index < 0 ? JValue.CreateNull() : new JValue(ids[index]),
                ["task_kind"] = kind.ToLowerInvariant(),
                ["display_label"] = (string)Required(task, "FlightDeckTaskLabel"),
                ["aircraft_display_name"] = (string?)Member(task, "AircraftType") ?? "",
                ["squadron_display_name"] = (string?)Member(task, "Squadron") ?? "",
                ["displayed_status"] = (string?)Member(task, "Info") ?? "",
                ["assigned_aircraft_count"] = kind == "PendingLaunchTask" ? new JValue(Count(Required(task, "LaunchCount"))) : JValue.CreateNull()
            });
        }
        Hosts.Add(new JObject
        {
            ["host_unit_id"] = hostId, ["source"] = "player_flight_deck",
            ["launch_in_progress"] = (bool)Required(deck, "LaunchInProgress"),
            ["landing_in_progress"] = (bool)Required(deck, "LandingInProgress"),
            ["aircraft_currently_on_board"] = Count(Required(deck, "AircraftCurrentlyOnBoard")),
            ["maximum_aircraft_on_board"] = Count(Required(deck, "MaxAircraftOnBoard")),
            ["ground_crew_count"] = Count(Required(deck, "GroundCrewCount")),
            ["assigned_ground_crew"] = Count(Required(deck, "AssignedGroundCrew")),
            ["deck_parking_slots"] = Count(Required(deck, "DeckParkSlots")),
            ["assigned_deck_parking_slots"] = Count(Required(deck, "AssignedDeckParkSlots")),
            ["tasks"] = tasks,
            ["count_semantics"] = "Host occupancy, unassigned inventory, squadrons, tasks and airborne units overlap or describe different states. Do not add them into a fleet total.",
            ["current_sortie_feasibility_known"] = false
        });
    }
    private static int Count(object value)
    {
        var count = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (count < 0) throw new InvalidOperationException("Negative displayed aviation count.");
        return count;
    }
}
