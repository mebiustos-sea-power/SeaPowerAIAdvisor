using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Reads only a confirmed own unit's sensors. Fields and conversions follow SensorInfoTabViewModel.
internal sealed class BattleSensorReader
{
    private const double NauticalMilesPerWorldUnit = 0.03628513;
    private const double WorldUnitsPerFoot = 0.00453571;
    private readonly Dictionary<string, JObject> references = new(StringComparer.Ordinal);
    internal JArray References() => new(references.Values.Select(r => r.DeepClone()));

    internal (JArray State, JArray Performance) Capture(object parameters, string unitId)
    {
        var states = new JArray();
        var performance = new JArray();
        var systems = Member(parameters, "_sensorSystems");
        if (systems == null) return (states, performance);
        foreach (var system in Rows(systems))
        {
            var kind = Kind(system);
            if (kind == null) continue;
            var sensor = kind == "radar" ? Required(system, "_radar") : kind == "sonar" ? Required(system, "_sonar") : null;
            var definition = kind == "radar" ? Required(sensor!, "_rp") : kind == "sonar" ? Required(sensor!, "_sp") : null;
            if (kind == "sonar" && (bool)Required(definition!, "_isActiveIntercept")) continue;
            var id = unitId + "_sensor_" + (states.Count + 1).ToString(CultureInfo.InvariantCulture);
            var identity = new JObject
            {
                ["id"] = id,
                ["display_name"] = (string)Required(Required(system, "DisplayName"), "Value"),
                ["kind"] = kind
            };
            if (kind == "radar")
            {
                identity["role"] = Code(Required(definition!, "_role"));
                identity["function"] = Code(Required(system, "_type"));
            }
            else if (kind == "sonar") identity["role"] = Code(Required(definition!, "_type"));
            var state = (JObject)identity.DeepClone();
            state["on"] = (bool)Required(Required(system, "IsOn"), "Value");
            state["inoperable"] = (bool)Required(Required(system, "Inoperable"), "Value");
            if (kind == "sonar") state["active_mode"] = (bool)Required(Required(sensor!, "IsActive"), "Value");
            states.Add(state);

            var specification = (JObject)identity.DeepClone();
            specification.Remove("id");
            specification["reference_key"] = (string)Required(system, "_systemName");
            specification["source"] = "player_sensor_information";
            if (kind == "radar") Radar(specification, system, definition!);
            else if (kind == "sonar") Sonar(specification, system, definition!);
            else if (kind == "mad") specification["maximum_instrumented_range_nm"] =
                Nonnegative(Required(system, "MaxRangeInMeters")) * NauticalMilesPerWorldUnit;
            var key = specification.ToString(Formatting.None);
            if (!references.TryGetValue(key, out var reference))
            {
                reference = specification;
                reference["id"] = "sensor_reference_" + (references.Count + 1).ToString(CultureInfo.InvariantCulture);
                references.Add(key, reference);
            }
            identity["reference_id"] = reference["id"]!.DeepClone();
            performance.Add(identity);
        }
        return (states, performance);
    }

    private static void Radar(JObject row, object system, object parameters)
    {
        var band = Code(Required(parameters, "_frequency"));
        if (band != "undefined") row["frequency_band"] = band;
        row["maximum_instrumented_range_nm"] = Nonnegative(Required(parameters, "_maxRange")) * NauticalMilesPerWorldUnit;
        var maxAltitude = Number(Required(parameters, "_maxAltitude"));
        if (maxAltitude > 0 && Code(Required(system, "_type")) != "targeting")
        {
            row["minimum_altitude_ft"] = Math.Max(0, Number(Required(parameters, "_minAltitude"))) / WorldUnitsPerFoot;
            row["maximum_altitude_ft"] = maxAltitude / WorldUnitsPerFoot;
        }
    }

    private static void Sonar(JObject row, object system, object parameters)
    {
        var band = Code(Required(parameters, "_frequency"));
        if (band != "undefined") row["frequency_band"] = band;
        var power = Number(Required(parameters, "_outputPower"));
        var active = power > 0 && (bool)Required(system, "IsActiveSensor");
        row["supported_modes"] = (bool)Required(parameters, "_isActiveOnly") ? new JArray("active") :
            active ? new JArray("active", "passive") : new JArray("passive");
        row["gain_db"] = Number(Required(parameters, "_gain"));
        if (active) row["active_gain_db"] = Number(Required(parameters, "_activeGain"));
        if (power > 0) row["output_power_db"] = power;
        row["detection_threshold_db"] = Number(Required(parameters, "_detectionTreshold"));
    }

    private static string? Kind(object system) => Shared.UnitStatusFacts.SensorKind(system);

    // The map uses these same groups in MakeSourceDescription (R/E/S and the other displayed methods).
    internal static JToken DetectionMethods(object track)
    {
        var flags = Convert.ToUInt32(Required(track, "DetectingSensors"), CultureInfo.InvariantCulture);
        var methods = new JArray();
        foreach (var (mask, name) in new (uint, string)[]
        {
            (2, "visual"), (4, "tv_seeker"), (24, "radar"), (32, "active_radar_seeker"),
            (192, "sonar"), (512, "esm"), (1024, "mad")
        })
            if ((flags & mask) != 0) methods.Add(name);
        return methods.Count == 0 ? JValue.CreateNull() : methods;
    }
}
