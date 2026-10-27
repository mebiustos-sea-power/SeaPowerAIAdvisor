using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NodeCanvas.BehaviourTrees;
using NodeCanvas.Framework;
using SeaPower;
using SeaPowerAIAdvisor.Api;
using SeapowerUI;
using UnityEngine;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport : IAdvisorCombatSupport
{
    private static string? CombatUnavailableReason(ObjectBase unit) => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._ai == null || unit._obp == null ? "target_not_ready" : null;
    private static GameUnitInfo CombatIdentity(ObjectBase unit, bool attack) => new(unit.Id, unit.Name.Value, null,
        unit.IsAirUnit, unit is Aircraft a ? a._isInFlight : unit is Helicopter h && h._isInFlight,
        CombatUnavailableReason(unit) ?? (attack && (unit is Aircraft plane && !plane._isInFlight || unit is Helicopter heli && !heli._isInFlight)
            ? "unit_not_airborne" : null));
    public IReadOnlyList<GameUnitInfo> GetCombatUnits()
    { Require("battle_units"); return VisibleUnits().Select(u => CombatIdentity(u, false)).ToArray(); }
    private ObjectBase CombatUnit(string target)
    {
        var unit = Unit(target);
        if (CombatUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        return unit;
    }
    // Both lookup and disclosure use the player's map projection, never the scenario's enemy-object list.
    private IReadOnlyList<(CombatTargetInfo Info, ObjectBase Object)> AttackTargets()
    {
        Require("battle_contacts");
        var own = units(); var result = new List<(CombatTargetInfo, ObjectBase)>();
        foreach (var map in knownUnits())
        {
            var vehicle = map.Unit;
            if (vehicle.Object == null || own.Any(u => ReferenceEquals(u, vehicle.Object))) continue;
            var kind = BattleValue.Code(map.VisualType); var posture = BattleValue.Code(map.VisualPosture);
            var destroyed = (bool)BattleValue.Required(BattleValue.Required(vehicle, "BaseObject"), "IsDestroyed");
            if (posture == "neutral" && !information().Allows(InformationKind.BattleNeutralContacts) ||
                destroyed && !information().Allows(InformationKind.BattleDestroyedContacts) ||
                BattleWeaponReader.IsKind(kind) && !information().Allows(InformationKind.BattleWeaponContacts)) continue;
            var estimate = BattleValue.Member(vehicle, "PositionEstimate");
            var position = estimate == null ? (GeoPosition?)null : (GeoPosition)BattleValue.Required(estimate, "Item1");
            result.Add((new CombatTargetInfo("unit:" + vehicle.Id.ToString(CultureInfo.InvariantCulture),
                BattleSituationReader.ContactDisplayName(map), kind, posture,
                position.HasValue ? new AdvisorMapCoordinate(position.Value.Latitude, position.Value.Longitude) : null,
                vehicle.IsDormant.Value, destroyed), vehicle.Object));
        }
        return result;
    }
    public IReadOnlyList<CombatTargetInfo> GetAttackTargets() => AttackTargets().Select(t => t.Info).ToArray();
    private static IEnumerable<ActionNode> AttackNodes(Node node)
    {
        foreach (var child in node.GetChildNodes())
        {
            if (child is ActionNode action && (action.task is AttackTask || action.task is ActionList list && list.actions.Any(a => a is AttackTask)))
                yield return action;
            foreach (var nested in AttackNodes(child)) yield return nested;
        }
    }
    private static IEnumerable<AttackTask> PendingAttackTasks(ObjectBase unit) => unit._userRoot == null ? Array.Empty<AttackTask>() :
        AttackNodes(unit._userRoot).SelectMany(n => n.task is AttackTask attack ? new[] { attack } : ((ActionList)n.task).actions.OfType<AttackTask>());
    private static void ClearPendingAttackTasks(ObjectBase unit)
    {
        if (unit._userRoot == null) return;
        var nodes = AttackNodes(unit._userRoot).ToArray();
        foreach (var node in nodes)
        {
            if (node.task is ActionList list)
            {
                list.actions.RemoveAll(a => a is AttackTask);
                if (list.actions.Count > 0) continue;
            }
            node.graph.RemoveNode(node, true, false);
        }
        // Removing immediate BT nodes shifts the running index. Reset it through the native node lifecycle.
        if (nodes.Length > 0) unit._userRoot.Reset(false);
    }
    public IReadOnlyList<AttackUnitInfo> GetAttackUnits()
    {
        Require("battle_units"); Require("battle_weapons"); var targets = AttackTargets();
        var result = new List<AttackUnitInfo>();
        foreach (var unit in VisibleUnits())
        {
            var pending = PendingAttackTasks(unit).ToArray();
            var ammo = GetAmmunition("unit:" + unit.Id.ToString(CultureInfo.InvariantCulture)).Where(a =>
                unit.GetWeaponSystemsForAmmunition(a.Id, false).Count > 0).Select(a =>
            {
                var reserved = unit._userRoot?.TaskViewModels.Where(v => v?.Task is AttackAtWaypoint task && task._ammunitionName == a.Id)
                    .Sum(v => (long)((AttackAtWaypoint)v.Task)._salvo) ?? 0;
                var notStarted = pending.Where(p => p.ammunitionForEngage.value == a.Id).Sum(p => (long)Math.Max(0, p.salvo.value));
                return new AttackAmmunitionInfo(a.Id, a.Name, a.Remaining, (int)Math.Max(0, a.Available - notStarted),
                    (int)Math.Max(0, a.Remaining - reserved), targets.Where(t => !t.Info.Destroyed &&
                        unit.DoesAmmoMatchTarget(a.Id, t.Object, true)).Select(t => t.Info.Target));
            }).ToArray();
            var attacks = new List<AttackInstruction>();
            void Add(ObjectBase target, string ammunition, int rounds)
            {
                if (target == null || rounds <= 0) return;
                var known = targets.FirstOrDefault(t => ReferenceEquals(t.Object, target));
                if (known.Object != null) attacks.Add(new AttackInstruction(known.Info.Target, ammunition, rounds));
            }
            foreach (var task in unit._currentEngageTasks) Add(task._targetObject, task._ammoId, task._shotsToFire);
            foreach (var task in pending) Add(task.targetObject.value, task.ammunitionForEngage.value, task.salvo.value);
            result.Add(new AttackUnitInfo(CombatIdentity(unit, true), ammo, attacks));
        }
        return result;
    }
    public void IssueAttack(string target, AttackUpdateMode mode, IReadOnlyList<AttackInstruction> attacks)
    {
        Require("battle_weapons"); var unit = CombatUnit(target);
        if (!Enum.IsDefined(typeof(AttackUpdateMode), mode) || attacks.Count is < 1 or > 40) throw new OperationException(400, "invalid_attack_arguments");
        var info = GetAttackUnits().Single(u => u.Unit.Target == target);
        if (info.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        var targets = AttackTargets();
        var prepared = attacks.Select(a =>
        {
            var opponent = targets.SingleOrDefault(t => t.Info.Target == a.Target && !t.Info.Destroyed);
            if (opponent.Object == null) throw new OperationException(409, "attack_target_unavailable");
            var ammo = info.Ammunition.SingleOrDefault(m => m.Id == a.Ammunition) ?? throw new OperationException(409, "ammunition_not_carried");
            if (!ammo.CompatibleTargets.Contains(a.Target)) throw new OperationException(409, "incompatible_attack_target");
            return (Instruction: a, Target: opponent.Object);
        }).ToArray();
        foreach (var group in attacks.GroupBy(a => a.Ammunition))
        {
            var ammo = info.Ammunition.Single(m => m.Id == group.Key);
            if (group.Sum(a => (long)a.Rounds) > (mode == AttackUpdateMode.Replace ? ammo.AvailableAfterSwitch : ammo.Available))
                throw new OperationException(409, "insufficient_attack_ammunition");
        }
        // After this boundary, native failures may leave changes; let the host report an unknown outcome.
        if (mode == AttackUpdateMode.Replace)
        {
            ClearPendingAttackTasks(unit);
            unit._ai._objectToDestroy = null; unit._ai._objectsToDestroyList.Clear();
            unit.ClearEngageTasksKeepGuiding();
        }
        foreach (var item in prepared)
        {
            var task = unit.InsertEngageTask(item.Instruction.Ammunition, item.Target, Vector3.zero, item.Instruction.Rounds,
                unit.GetEngagePriority(item.Target), false, false, false);
            if (task == null || !unit._currentEngageTasks.Contains(task))
                throw new InvalidOperationException("The native attack instruction was not retained.");
        }
    }
    public void CeaseFire(string target)
    {
        var unit = CombatUnit(target);
        ClearPendingAttackTasks(unit);
        unit.CeaseFire(report: true, clearOrder: true, formationCheck: false, stopJamming: true, airFormationCheck: false, clearAttackTasks: true);
    }
}
