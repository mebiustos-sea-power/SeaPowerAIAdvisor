using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Own weapons are not platforms. Other weapons expose only the tracking relation shown on the player's map.
internal static class BattleWeaponReader
{
    internal static bool IsKind(string kind) => kind is "missile" or "torpedo";

    internal static JObject? CaptureOwn(object weapon, string id)
    {
        var kind = Code(Required(weapon, "_type"));
        if (!IsKind(kind) || !CallBool(weapon, "isLaunched") || (bool)Required(weapon, "IsDestroyed") ||
            Code(Required(weapon, "_flightStage")) is "debris" or "terminated" or "selfdestruct") return null;
        return new JObject
        {
            ["id"] = id,
            ["display_name"] = (string)Required(Required(weapon, "Name"), "Value"),
            ["kind"] = kind,
            ["position"] = Position(Required(Required(weapon, "Position"), "Value")),
            ["heading_deg"] = Number(Required(Required(weapon, "Heading"), "Value")),
            ["speed_knots"] = Number(Required(weapon, "_velocityInKnots")),
            [kind == "torpedo" ? "depth_ft" : "altitude_ft"] =
                Number(Required(Required(weapon, "Altitude"), "Value")) * (kind == "torpedo" ? -1 : 1)
        };
    }

    internal static void AddOwnTargets(JObject row, object weapon, Func<object, string?> resolve)
    {
        var launcher = Member(weapon, "_launchPlatform");
        row["launch_platform_id"] = Reference(launcher, resolve);
        var targeting = UnknownTracking();
        var commanded = Member(weapon, "CurrentIntendedTargetObject");
        var commandedId = commanded == null ? null : resolve(commanded);
        targeting["commanded_target_status"] = commanded == null ? "none" : commandedId == null ? "unknown" : "known";
        targeting["commanded_target_id"] = commandedId == null ? JValue.CreateNull() : new JValue(commandedId);
        // A positional order is separate from a target's actual position.
        var commandPosition = commanded == null ? Member(weapon, "CommandPosition") : null;
        targeting["commanded_position"] = commandPosition == null ? JValue.CreateNull() : Position(commandPosition);
        var seeker = Member(weapon, "_seeker");
        if (seeker == null || !CallBool(seeker, "isActive")) targeting["tracking_status"] = "inactive";
        else
        {
            var echo = Member(weapon, "CurrentStrongestTargetEcho");
            var target = echo == null ? null : Member(echo, "_objectBase");
            if (target == null) targeting["tracking_status"] = "searching";
            else SetTrackedTarget(targeting, target, resolve);
        }
        row["targeting"] = targeting;
    }

    internal static void AddContactTarget(JObject row, object mapUnit, Func<object, string?> resolve)
    {
        var targeting = UnknownTracking();
        row["targeting"] = targeting;
        if ((bool)row["destroyed"]! || (bool)row["dormant"]!) return;
        var model = Member(mapUnit, "ObjectViewModel");
        if (model == null || model.GetType().GetProperty("LineToTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
            return;
        var color = Required(model, "LineToTargetColor");
        if (Number(Required(color, "A")) <= 0) return;
        var line = Rows(Member(model, "LineToTarget"));
        if (line.Length != 2 || JToken.DeepEquals(Position(line[0]), Position(line[1]))) return;
        var weapon = Required(model, "WeaponBase");
        // This is the game's target-line disclosure test. Never read another side's commanded target.
        if (!CallBool(weapon, "TargetedAtUnitWhichCanReportDetection")) return;
        var echo = Member(weapon, "CurrentStrongestTargetEcho");
        var target = echo == null ? null : Member(echo, "_objectBase");
        if (target != null) SetTrackedTarget(targeting, target, resolve);
    }

    private static JObject UnknownTracking() => new()
    {
        ["tracking_status"] = "unknown", ["tracked_target_id"] = JValue.CreateNull()
    };
    private static void SetTrackedTarget(JObject targeting, object target, Func<object, string?> resolve)
    {
        var id = resolve(target);
        if (id == null) return;
        targeting["tracking_status"] = "tracking";
        targeting["tracked_target_id"] = id;
    }
    private static JToken Reference(object? target, Func<object, string?> resolve)
    {
        var id = target == null ? null : resolve(target);
        return id == null ? JValue.CreateNull() : new JValue(id);
    }
    private static bool CallBool(object value, string name)
    {
        var method = value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(value.GetType().FullName, name);
        return (bool)method.Invoke(value, null)!;
    }
    private static JObject Position(object position) => AdvisorInputBuilder.Position(new MapPosition(
        Number(Required(position, "Latitude")), Number(Required(position, "Longitude"))));
}
