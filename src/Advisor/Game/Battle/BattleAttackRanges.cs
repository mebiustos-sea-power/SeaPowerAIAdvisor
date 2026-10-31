using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;
using UnityEngine;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport
{
    // Match the native GetMaxRangeForAmmo estimate. Terrain checks short-circuit only when already out of range.
    private static AttackRangeInfo ReadAttackRange(ObjectBase shooter, Ammunition ammunition, ObjectBase target, string contact)
    {
        const double nauticalMilesPerWorldUnit = 0.03628513;
        var range = shooter.GetMaxEngagementRanges(target, ammunition, false, true, false, null);
        var maximum = Math.Min(range.KinematicRange, Math.Min(range.SensorRange, range.TerrainRange));
        var factor = maximum == range.TerrainRange ? "terrain" : maximum == range.SensorRange ? "sensor" : "kinematic";
        return new AttackRangeInfo(contact, ammunition._ap._ammunitionFileName,
            Vector3.Distance(shooter.transform.position, target.transform.position) * nauticalMilesPerWorldUnit,
            ammunition._ap._launchRangesInUnity.x * nauticalMilesPerWorldUnit, maximum * nauticalMilesPerWorldUnit, factor);
    }
    private IEnumerable<AttackRangeInfo> InRangeOptions(AttackUnitInfo unit,
        IReadOnlyDictionary<string, ObjectBase> shooters, IReadOnlyDictionary<string, ObjectBase> contacts)
    {
        if (unit.Unit.OrderUnavailableReason != null) yield break;
        var shooter = shooters[unit.Unit.Target];
        foreach (var ammunition in unit.Ammunition.Where(a => Math.Max(a.Available, a.AvailableAfterSwitch) > 0))
        {
            if (!shooter.GetWeaponSystemsForAmmunition(ammunition.Id, false).Any(w => !w.IsDestroyed && !w.Inoperable.Value)) continue;
            var native = shooter.getAmmunitionByName(ammunition.Id);
            foreach (var contact in ammunition.CompatibleTargets.Where(contacts.ContainsKey))
            {
                var range = ReadAttackRange(shooter, native, contacts[contact], contact);
                if (range.InRange) yield return range;
            }
        }
    }
    private Dictionary<string, ObjectBase> AttackShooters() => VisibleUnits().ToDictionary(
        u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, bool> GetAttackRangeOverview()
    {
        var targets = AttackTargets().Where(t => !t.Info.Destroyed && t.Info.Posture is "enemy" or "unknown").ToArray();
        var wanted = new HashSet<string>(targets.Select(t => t.Info.Target), StringComparer.Ordinal);
        var units = ReadAttackUnits(targets, contacts: wanted); var shooters = AttackShooters();
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var contact = new Dictionary<string, ObjectBase>(StringComparer.Ordinal) { [target.Info.Target] = target.Object };
            result.Add(target.Info.Target, units.Any(unit => InRangeOptions(unit, shooters, contact).Any()));
        }
        return result;
    }

    public IReadOnlyList<AttackUnitInfo> GetInRangeAttackUnits(IReadOnlyList<string> contacts, string? shooter = null)
    {
        if (contacts.Count < 1 || contacts.Count > AdvisorQueryLimits.MaxIdsPerQuery || contacts.Distinct(StringComparer.Ordinal).Count() != contacts.Count)
            throw new OperationException(400, "invalid_attack_contacts");
        var wanted = new HashSet<string>(contacts, StringComparer.Ordinal);
        var all = AttackTargets();
        var targets = all.Where(t => wanted.Contains(t.Info.Target) && !t.Info.Destroyed).ToDictionary(t => t.Info.Target, t => t.Object, StringComparer.Ordinal);
        if (targets.Count != wanted.Count) throw new OperationException(409, "attack_target_unavailable");
        var shooters = AttackShooters();
        if (shooter != null && !shooters.ContainsKey(shooter)) throw new OperationException(409, "target_unavailable");
        var result = new List<AttackUnitInfo>();
        foreach (var unit in ReadAttackUnits(all, shooter, wanted))
        {
            var ranges = InRangeOptions(unit, shooters, targets).ToArray();
            if (ranges.Length == 0) continue;
            var ammunition = unit.Ammunition.Where(a => ranges.Any(r => r.Ammunition == a.Id)).Select(a =>
                new AttackAmmunitionInfo(a.Id, a.Name, a.Remaining, a.Available, a.AvailableAfterSwitch,
                    ranges.Where(r => r.Ammunition == a.Id).Select(r => r.Target))).ToArray();
            result.Add(new AttackUnitInfo(unit.Unit, ammunition, unit.PendingAttacks, ranges));
        }
        return result;
    }
}
