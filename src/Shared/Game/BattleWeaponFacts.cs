using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Callers establish ownership for native weapon state; other sides expose only map-disclosed tracking.
internal static class BattleWeaponFacts
{
    internal static bool IsKind(string kind) => kind is "missile" or "torpedo";
    internal static bool IsActiveOwn(object weapon) => IsKind(Code(Required(weapon, "_type"))) &&
        CallBool(weapon, "isLaunched") && !(bool)Required(weapon, "IsDestroyed") &&
        Code(Required(weapon, "_flightStage")) is not ("debris" or "terminated" or "selfdestruct");

    internal static (string Status, object? Target) OwnTracking(object weapon)
    {
        var seeker = Member(weapon, "_seeker");
        if (seeker == null || !CallBool(seeker, "isActive")) return ("inactive", null);
        var echo = Member(weapon, "CurrentStrongestTargetEcho");
        var target = echo == null ? null : Member(echo, "_objectBase");
        return target == null ? ("searching", null) : ("tracking", target);
    }

    internal static object? VisibleTrackedTarget(object mapUnit, bool destroyed, bool dormant)
    {
        if (destroyed || dormant) return null;
        var model = Member(mapUnit, "ObjectViewModel");
        if (model == null || model.GetType().GetProperty("LineToTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
            return null;
        var color = Required(model, "LineToTargetColor");
        if (Number(Required(color, "A")) <= 0) return null;
        var line = Rows(Member(model, "LineToTarget"));
        if (line.Length != 2 || JToken.DeepEquals(Position(line[0]), Position(line[1]))) return null;
        var weapon = Required(model, "WeaponBase");
        // Match the game's target-line disclosure test before reading its echo.
        if (!CallBool(weapon, "TargetedAtUnitWhichCanReportDetection")) return null;
        var echo = Member(weapon, "CurrentStrongestTargetEcho");
        return echo == null ? null : Member(echo, "_objectBase");
    }

    private static bool CallBool(object value, string name)
    {
        var method = value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(value.GetType().FullName, name);
        return (bool)method.Invoke(value, null)!;
    }
    private static JObject Position(object position)
    {
        var latitude = Number(Required(position, "Latitude"));
        if (latitude < -90 || latitude > 90) throw new InvalidOperationException("The snapshot contains an invalid geographical position.");
        // Preserve the existing target-line comparison when longitudes wrap across the date line.
        var longitude = Number(Required(position, "Longitude")) % 360;
        if (longitude >= 180) longitude -= 360;
        if (longitude < -180) longitude += 360;
        return new JObject { ["latitude_deg"] = latitude, ["longitude_deg"] = longitude };
    }
}
