using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// A paired performance reference, independent of the aircraft's current altitude and speed.
internal static class BattleAircraftCruise
{
    internal static JObject Capture(object parameters, Func<float, float> speedOfSound)
    {
        var altitude = Nonnegative(Required(parameters, "_cruiseAltitudeFeet"));
        var speeds = Rows(Required(parameters, "_speedValuesInMach"));
        // Use the native Cruise preset; the separate _cruiseMach field can differ for knots-based definitions.
        if (speeds.Length < 3) throw new InvalidOperationException("Aircraft cruise speed preset is unavailable.");
        var mach = Nonnegative(speeds[2]);
        var sound = Nonnegative(speedOfSound((float)(altitude * 0.3048)));
        if (mach == 0 || sound == 0) throw new InvalidOperationException("Invalid aircraft cruise speed.");
        return new JObject
        {
            ["altitude_ft"] = Math.Round(altitude, 2),
            ["speed_knots"] = Math.Round(Nonnegative(mach * sound * 1.94384), 2)
        };
    }
}
