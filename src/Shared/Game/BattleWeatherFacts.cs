using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor.Shared;

// Mission-wide current conditions; no forecast, wave-height estimate or per-unit duplication.
internal static class BattleWeatherFacts
{
    internal static JObject Capture(object environment)
    {
        var force = (int)Required(environment, "SeaState");
        if (force < 0 || force > 12) throw new InvalidOperationException("Invalid game wind-force value.");
        var precipitation = new JArray();
        if ((bool)Required(environment, "IsRaining")) precipitation.Add("rain");
        if ((bool)Required(environment, "IsSnowing")) precipitation.Add("snow");
        return new JObject
        {
            ["wind_force_beaufort"] = force, ["precipitation"] = precipitation,
            ["fog"] = (bool)Required(environment, "IsFog"), ["lightning"] = (bool)Required(environment, "IsLightning")
        };
    }
}
