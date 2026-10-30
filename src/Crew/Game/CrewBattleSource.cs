using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI.ViewModels;
using SeaPowerAIAdvisor.Shared;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor.Crew;

// All native reads happen on the game thread and begin at the player's currently known map.
internal sealed class CrewBattleSource
{
    private readonly MainGameViewModel model;
    internal CrewBattleSource(MainGameViewModel model) => this.model = model;
    private (int Id, ObjectBase Unit)[] OwnUnits()
    {
        var own = Globals._playerTaskforce?.TaskforceObjects;
        if (own == null || model.Map == null) return Array.Empty<(int, ObjectBase)>();
        return model.Map.MapKnownUnits.AllUnits.Where(m => m.Unit.Object != null && own.Any(o => ReferenceEquals(o, m.Unit.Object)))
            .Select(m => (m.Unit.Id, m.Unit.Object)).Where(p => Code(Required(p.Object, "_type")) is ("vessel" or "submarine" or "aircraft" or "helicopter") && !(bool)Required(p.Object, "IsDestroyed"))
            .Select(p => (p.Id, p.Object)).ToArray();
    }
    internal IReadOnlyList<CrewTarget> Targets()
    {
        var own = OwnUnits(); var result = new List<CrewTarget>();
        foreach (var unit in own)
        {
            if (unit.Unit.Formation != null && !unit.Unit.IsFormationLeader) continue;
            var members = unit.Unit.Formation == null ? new[] { unit.Id } : own.Where(m => ReferenceEquals(m.Unit.Formation, unit.Unit.Formation)).Select(m => m.Id).OrderBy(id => id).ToArray();
            var kind = Code(Required(unit.Unit, "_type"));
            result.Add(new CrewTarget("unit:" + unit.Id.ToString(CultureInfo.InvariantCulture), unit.Unit.Name.Value, unit.Unit.Nation.Value,
                kind is "aircraft" or "helicopter" ? "pilot" : "captain", members));
        }
        return result;
    }
    internal JObject Capture(IReadOnlyList<CrewTarget> targets)
    {
        var wanted = new HashSet<int>(targets.SelectMany(t => t.Members)); var units = new JArray();
        foreach (var pair in OwnUnits().Where(p => wanted.Contains(p.Id)))
        {
            var unit = pair.Unit; var parameters = Required(unit, "_obp");
            var row = new JObject { ["id"] = "unit:" + pair.Id, ["display_name"] = unit.Name.Value,
                ["kind"] = Code(Required(unit, "_type")), ["nation"] = unit.Nation.Value,
                ["position"] = Position(Required(Required(unit, "Position"), "Value")),
                ["heading_deg"] = Number(Required(Required(unit, "Heading"), "Value")), ["speed_knots"] = Number(Required(unit, "_velocityInKnots")),
                ["altitude_m"] = Number(Required(Required(unit, "Altitude"), "Value")),
                ["acting_independently"] = unit.Formation != null && !unit.IsFormationLeader && !unit.FollowingFormation,
                ["damage"] = UnitStatusFacts.Damage(unit, parameters), ["fuel"] = UnitStatusFacts.Fuel(unit, parameters),
                ["sensors"] = UnitStatusFacts.Sensors(parameters) };
            var names = Rows(Required(unit, "AmmunitionNameToAmmunitionDictionary")).ToDictionary(p => (string)Required(p, "Key"), p => Member(p, "Value"));
            var ammunition = new JArray();
            foreach (var entry in Rows(Required(unit, "AmmunitionAmountDictionary")))
            {
                var key = (string)Required(entry, "Key"); var count = Convert.ToInt32(Required(entry, "Value"), CultureInfo.InvariantCulture);
                if (count < 0) throw new InvalidOperationException("Negative displayed ammunition count.");
                var ammo = names.TryGetValue(key, out var value) ? value : null;
                ammunition.Add(new JObject { ["id"] = key, ["display_name"] = ammo == null ? null : (string)Required(Required(ammo, "_ap"), "_displayedName"), ["remaining_count"] = count });
            }
            row["weapons"] = new JObject { ["fire_control_state"] = Code(Required(Required(unit, "WeaponState"), "Value")), ["ammunition"] = ammunition };
            units.Add(row);
        }
        var contacts = new JArray();
        foreach (var mapUnit in model.Map.MapKnownUnits.AllUnits)
        {
            var posture = Code(Member(mapUnit, "VisualPosture")); if (posture is not ("enemy" or "unknown")) continue;
            var track = mapUnit.Unit; var position = Member(track, "PositionEstimate"); var identified = (bool)Required(Required(track, "Identified"), "Value");
            var observedClass = Member(track, "Class");
            var row = new JObject { ["track_number"] = track.Id, ["posture"] = posture, ["kind"] = Code(Member(mapUnit, "VisualType")),
                ["class_identified"] = identified, ["class_name"] = identified && observedClass != null ? (string)Required(observedClass, "Value") : null,
                ["position"] = position == null ? JValue.CreateNull() : Position(Required(position, "Item1")),
                ["position_source"] = position == null ? "unknown" : (bool)Required(track, "HasLiveTrack") ? "observed_estimate" : "extrapolated_estimate",
                ["dormant"] = (bool)Required(Required(track, "IsDormant"), "Value") };
            BattleContactMotionReader.CopyTo(row, track); contacts.Add(row);
        }
        return new JObject { ["captured_at_utc"] = SeaPower.Environment.Instance.DateTime.ToString("o", CultureInfo.InvariantCulture), ["units"] = units, ["contacts"] = contacts };
    }
    private static JObject Position(object position) => new() { ["latitude_deg"] = Number(Required(position, "Latitude")), ["longitude_deg"] = Number(Required(position, "Longitude")) };
}
