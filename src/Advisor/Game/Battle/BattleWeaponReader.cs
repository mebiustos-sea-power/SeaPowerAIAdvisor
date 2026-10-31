using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Own weapons are not platforms. Other weapons expose only the tracking relation shown on the player's map.
internal static class BattleWeaponReader
{
    internal static bool IsKind(string kind) => BattleWeaponFacts.IsKind(kind);

    internal static JObject? CaptureOwn(object weapon, string id)
    {
        var kind = Code(Required(weapon, "_type"));
        if (!BattleWeaponFacts.IsActiveOwn(weapon)) return null;
        return new JObject
        {
            ["id"] = id,
            ["display_name"] = (string)Required(Required(weapon, "Name"), "Value"),
            ["kind"] = kind,
            ["position"] = Position(Required(Required(weapon, "Position"), "Value")),
            ["heading_deg"] = Number(Required(Required(weapon, "Heading"), "Value")),
            ["speed_knots"] = Number(Required(weapon, "_velocityInKnots")),
            [BattleVerticalPosition.FieldName(kind)!] =
                BattleVerticalPosition.Read(kind, Required(Required(weapon, "Altitude"), "Value"))
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
        var tracking = BattleWeaponFacts.OwnTracking(weapon);
        if (tracking.Target == null) targeting["tracking_status"] = tracking.Status;
        else SetTrackedTarget(targeting, tracking.Target, resolve);
        row["targeting"] = targeting;
    }

    internal static void AddContactTarget(JObject row, object mapUnit, Func<object, string?> resolve)
    {
        var targeting = UnknownTracking();
        row["targeting"] = targeting;
        var target = BattleWeaponFacts.VisibleTrackedTarget(mapUnit, (bool)row["destroyed"]!, (bool)row["dormant"]!);
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
    private static JObject Position(object position) => AdvisorInputBuilder.Position(new MapPosition(
        Number(Required(position, "Latitude")), Number(Required(position, "Longitude"))));
}
