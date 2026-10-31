using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor.Crew;

// Projects player-known contacts and groups active weapons without sending weapon motion or capabilities.
internal static class CrewContactReader
{
    internal static (JArray Contacts, JArray LaunchedWeapons) Capture(IEnumerable<object> knownUnits, IEnumerable<object> playerObjects)
    {
        var map = knownUnits.ToArray(); var own = playerObjects.ToArray();
        var identities = new List<(object Object, JObject Reference)>();
        foreach (var mapUnit in map)
        {
            var track = Required(mapUnit, "Unit"); var identity = Member(track, "Object");
            if (identity == null) continue;
            var friendly = own.Any(o => ReferenceEquals(o, identity));
            var id = Convert.ToInt32(Required(track, "Id"), CultureInfo.InvariantCulture);
            identities.Add((identity, new JObject
            {
                ["id"] = "unit:" + id.ToString(CultureInfo.InvariantCulture),
                ["display_name"] = friendly ? (string)Required(Required(identity, "Name"), "Value") : "Contact " + id.ToString(CultureInfo.InvariantCulture),
                ["kind"] = Code(Required(mapUnit, "VisualType")),
                ["posture"] = friendly ? "friendly" : Code(Required(mapUnit, "VisualPosture"))
            }));
        }
        JObject? Resolve(object? identity) => identity == null ? null : identities.FirstOrDefault(p => ReferenceEquals(p.Object, identity)).Reference;
        var contacts = new JArray();
        var weapons = new List<(string Posture, string Kind, JObject? Launcher, JObject? Target)>();
        foreach (var mapUnit in map)
        {
            var track = Required(mapUnit, "Unit"); var identity = Member(track, "Object");
            if (identity != null && own.Any(o => ReferenceEquals(o, identity))) continue;
            var posture = Code(Required(mapUnit, "VisualPosture")); if (posture is not ("enemy" or "unknown")) continue;
            var kind = Code(Required(mapUnit, "VisualType"));
            var dormant = (bool)Required(Required(track, "IsDormant"), "Value");
            if (BattleWeaponFacts.IsKind(kind))
            {
                var destroyed = (bool)Required(Required(track, "BaseObject"), "IsDestroyed");
                if (!destroyed && !dormant)
                    weapons.Add((posture, kind, null, Resolve(BattleWeaponFacts.VisibleTrackedTarget(mapUnit, destroyed, dormant))));
                continue;
            }
            var position = Member(track, "PositionEstimate"); var identified = (bool)Required(Required(track, "Identified"), "Value");
            var observedClass = Member(track, "Class");
            var row = new JObject
            {
                ["track_number"] = Convert.ToInt32(Required(track, "Id"), CultureInfo.InvariantCulture), ["posture"] = posture, ["kind"] = kind,
                ["class_identified"] = identified, ["class_name"] = identified && observedClass != null ? (string)Required(observedClass, "Value") : null,
                ["position"] = position == null ? JValue.CreateNull() : Position(Required(position, "Item1")),
                ["position_source"] = position == null ? "unknown" : (bool)Required(track, "HasLiveTrack") ? "observed_estimate" : "extrapolated_estimate",
                ["dormant"] = dormant
            };
            BattleContactMotionReader.CopyTo(row, track); contacts.Add(row);
        }
        foreach (var weapon in own)
        {
            if (!BattleWeaponFacts.IsActiveOwn(weapon)) continue;
            weapons.Add(("friendly", Code(Required(weapon, "_type")), Resolve(Member(weapon, "_launchPlatform")),
                Resolve(BattleWeaponFacts.OwnTracking(weapon).Target)));
        }
        var groups = weapons.GroupBy(w => (w.Posture, w.Kind, Launcher: (string?)w.Launcher?["id"], Target: (string?)w.Target?["id"]));
        var launched = new JArray(groups.Select(group =>
        {
            var first = group.First();
            return new JObject
            {
                ["posture"] = first.Posture, ["kind"] = first.Kind, ["count"] = group.Count(),
                ["launch_platform"] = first.Launcher?.DeepClone() ?? JValue.CreateNull(),
                ["tracked_target"] = first.Target?.DeepClone() ?? JValue.CreateNull()
            };
        }));
        return (contacts, launched);
    }

    private static JObject Position(object position) => new()
    { ["latitude_deg"] = Number(Required(position, "Latitude")), ["longitude_deg"] = Number(Required(position, "Longitude")) };
}
