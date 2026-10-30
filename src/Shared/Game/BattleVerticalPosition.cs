using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Game display values are feet; torpedo depth is clamped to the waterline while airborne.
internal static class BattleVerticalPosition
{
    internal static string? FieldName(string kind) => kind switch
    {
        "air" or "aircraft" or "helicopter" or "missile" => "altitude_ft",
        "submarine" or "torpedo" or "biologic" => "depth_ft",
        _ => null
    };

    internal static JToken Read(string kind, object? altitude)
    {
        var field = FieldName(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));
        if (altitude == null) return JValue.CreateNull();
        var value = Number(altitude);
        if (field == "depth_ft") value = -value;
        if (kind == "torpedo") value = Math.Max(0, value);
        return new JValue(value);
    }
}
