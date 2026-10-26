using System;
using System.Globalization;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

// Reads targets without issuing orders. The same ID mapping is used when constructing native waypoints.
internal static class BattleMovementPresets
{
    private static readonly (string Id, string Name)[] ShipSpeeds = {
        ("ahead_one_third", "Ahead 1/3"), ("ahead_two_thirds", "Ahead 2/3"), ("ahead_standard", "Ahead standard"),
        ("ahead_full", "Ahead full"), ("ahead_flank", "Ahead flank") };
    private static readonly (string Id, string Name)[] PlaneSpeeds = { ("loiter", "Loiter"), ("cruise", "Cruise"), ("max", "Max") };
    private static readonly (string Id, string Name, WaypointData.WaypointHeightState State)[] SubDepths = {
        ("surface", "Surface", WaypointData.WaypointHeightState.Surface),
        ("periscope", "Periscope depth", WaypointData.WaypointHeightState.Periscope),
        ("shallow", "Shallow", WaypointData.WaypointHeightState.VeryLow),
        ("above_layer", "Above thermocline", WaypointData.WaypointHeightState.Low),
        ("below_layer", "Below thermocline", WaypointData.WaypointHeightState.Medium),
        ("deep", "Deep", WaypointData.WaypointHeightState.High),
        ("very_deep", "Very deep", WaypointData.WaypointHeightState.VeryHigh) };
    private const double FeetPerUnity = 220.4727;
    private static (string Id, string Name)[] SpeedOptions(ObjectBase unit) => unit is Vessel || unit is Submarine ? ShipSpeeds
        : unit is Aircraft ? PlaneSpeeds : Array.Empty<(string, string)>();
    private static float[] AltitudeValues(ObjectBase unit) => unit is Aircraft plane ? plane.Ap._presetAltitudes
        : unit is Helicopter helicopter ? helicopter._hp._presetAltitudes : Array.Empty<float>();
    private static string AltitudeId(int index) => "preset_" + index.ToString(CultureInfo.InvariantCulture);
    internal static MovementPreset[] Speeds(ObjectBase unit) => SpeedOptions(unit).Select((option, index) =>
    {
        double value;
        if (unit is Aircraft plane)
        {
            // Native waypoint speed states 2/3/4 call setTelegraph(2/3/4), which selects Mach index 1/2/3.
            var mach = plane.Ap._speedValuesInMach;
            using var command = new ConstantMach(mach[Math.Min(index + 1, mach.Length - 1)], unit);
            value = command.GetDemandedSpeedInKnots(unit);
        }
        else
        {
            using var command = new TelegraphSpeed(unit, index + 1);
            value = command.SpeedInKnots;
        }
        return new MovementPreset(option.Id, option.Name, Math.Round(value, 2), "kt");
    }).ToArray();
    internal static MovementPreset[] Altitudes(ObjectBase unit) => AltitudeValues(unit).Select((value, index) =>
    {
        var feet = Math.Round(value * FeetPerUnity);
        return new MovementPreset(AltitudeId(index), feet.ToString(CultureInfo.InvariantCulture) + " ft", feet, "ft");
    }).ToArray();
    internal static MovementPreset[] Depths(ObjectBase unit) => unit is Submarine submarine
        ? SubDepths.Select((option, index) => new MovementPreset(option.Id, option.Name, submarine.GetPresetDepthFeet(index), "ft")).ToArray()
        : Array.Empty<MovementPreset>();
    internal static ObjectSpeed.SpeedSettings SpeedState(ObjectBase unit, string id)
    {
        var index = Array.FindIndex(SpeedOptions(unit), option => option.Id == id);
        if (index < 0) throw new OperationException(400, "movement_setting_not_supported");
        return (ObjectSpeed.SpeedSettings)(unit is Aircraft ? index + 2 : index + 1);
    }
    internal static float AltitudeValue(ObjectBase unit, string id)
    {
        var values = AltitudeValues(unit);
        for (var index = 0; index < values.Length; index++) if (AltitudeId(index) == id) return values[index];
        throw new OperationException(400, "movement_setting_not_supported");
    }
    internal static WaypointData.WaypointHeightState DepthState(ObjectBase unit, string id)
    {
        if (unit is Submarine)
            foreach (var option in SubDepths) if (option.Id == id) return option.State;
        throw new OperationException(400, "movement_setting_not_supported");
    }
}
