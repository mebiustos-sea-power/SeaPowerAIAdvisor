using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Player-owned units only. Selection and command-specific eligibility remain with each command.
internal sealed partial class BattleOperationSupport : IAdvisorGameSupport, IAdvisorReservationSupport
{
    private readonly Func<IReadOnlyList<ObjectBase>> units;
    private readonly Func<AdvisorInformation> information;
    internal BattleOperationSupport(Func<IReadOnlyList<ObjectBase>> units, Func<AdvisorInformation> information)
    { this.units = units; this.information = information; }
    public bool IsInformationEnabled(string category)
    {
        var item = AdvisorInformation.Items.SingleOrDefault(i => i.Id == category)
            ?? throw new ArgumentException("Unknown information category: " + category, nameof(category));
        return information().Allows(item.Kind);
    }
    private void Require(string category)
    { if (!IsInformationEnabled(category)) throw new OperationException(403, "information_disabled"); }
    public IReadOnlyList<GameUnitInfo> GetUnits()
    {
        Require("battle_units");
        return VisibleUnits().Select(unit => new GameUnitInfo(unit.Id, unit.Name.Value,
            new AdvisorMapCoordinate(unit.Position.Value.Latitude, unit.Position.Value.Longitude),
            unit is Aircraft || unit is Helicopter,
            unit is Helicopter helicopter ? helicopter._isInFlight : unit is Aircraft aircraft && aircraft._isInFlight,
            OrderUnavailableReason(unit))).ToArray();
    }
    private IEnumerable<ObjectBase> VisibleUnits() => units().Where(unit =>
    {
        var kind = BattleValue.Code(BattleValue.Required(unit, "_type"));
        if (unit.isWeapon()) return false;
        var subtype = kind == "landunit" ? BattleValue.Code(BattleValue.Required(unit._obp, "_landUnitSubType")) : null;
        return BattleInformation.Permitted(BattleInformation.UnitCategory(subtype), information());
    });
    private ObjectBase Unit(string target)
    {
        Require("battle_units");
        return VisibleUnits().FirstOrDefault(u => "unit:" + u.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == target)
            ?? throw new OperationException(409, "target_unavailable");
    }
    private static string? OrderUnavailableReason(ObjectBase unit) => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._userRoot == null ? "target_not_ready"
        : unit.FollowingFormation && !unit.IsFormationLeader ? "following_formation"
        : unit.CurrentOrder.Value.OrderType != Order.Type.None ? "current_order_conflict" : null;
    public ReservationRoute? GetReservationRoute(string target)
    {
        Require("battle_movement_plans");
        return BattleReservationReader.ReadRoute(Unit(target));
    }
    public string GetSonobuoyDepth(string target)
    {
        Require("battle_weapons");
        return Unit(target).SonoDeployState.Value switch
        {
            Bomb.SonobuoyDeployState.AboveLayer => "above_layer",
            Bomb.SonobuoyDeployState.BelowLayer => "below_layer",
            Bomb.SonobuoyDeployState.Intermittent => "alternating",
            _ => throw new InvalidOperationException("The unit sonobuoy depth setting is unsupported.")
        };
    }
    public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target)
    {
        Require("battle_weapons");
        var unit = Unit(target);
        return unit.AmmunitionNameToAmmunitionDictionary.Select(pair =>
        {
            var ammo = pair.Value._ap;
            var remaining = unit.getAmmunitionAmountByName(pair.Key);
            var reserved = unit._userRoot == null ? 0 : unit._userRoot.TaskViewModels
                .Where(v => v?.Task is AttackAtWaypoint attack && attack._ammunitionName == pair.Key)
                .Sum(v => ((AttackAtWaypoint)v.Task)._salvo);
            var active = unit._currentEngageTasks.Where(t => t._ammoId == pair.Key).Sum(t => t._shotsToFire);
            active += unit._obp._weaponSystems.Count(w => w._executingEngageTask &&
                w._ammoForEngage._ap._ammunitionFileName == pair.Key);
            var launchers = unit.GetWeaponSystemsForAmmunition(pair.Key, false);
            // Inventory can include ammunition without an associated launcher.
            var single = launchers != null && launchers.Count > 0 && launchers.All(w => w is WeaponSystemHardpoint &&
                (BattleValue.Required(w, "_salvoAmounts") is Dictionary<string, int> amounts
                    ? !amounts.TryGetValue(pair.Key, out var count) || count == 1
                    : throw new InvalidOperationException("The hardpoint salvo amounts are unavailable.")));
            return new AmmunitionInfo(pair.Key, ammo._displayedName, ammo._type.ToString(), ammo._subType.ToString(),
                remaining, Math.Max(0, remaining - reserved - active), single, Math.Max(0, remaining - active));
        }).ToArray();
    }
    public void ClearReservations(string target)
    {
        var unit = Unit(target);
        if (OrderUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        unit.RemoveWaypoints();
        if (unit._userRoot.TaskViewModels.Count != 0)
            throw new InvalidOperationException("The native reservation queue was not cleared.");
    }
    public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position)
    {
        Require("battle_weapons");
        var unit = Unit(target);
        if (OrderUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        if ((unit is Helicopter helicopter && !helicopter._isInFlight) || (unit is Aircraft aircraft && !aircraft._isInFlight))
            throw new OperationException(409, "unit_not_airborne");
        var ammo = GetAmmunition(target).SingleOrDefault(a => a.Id == ammunition && a.Type == "Bomb" && a.Subtype == "Sonobuoy")
            ?? throw new OperationException(409, "sonobuoy_not_carried");
        if (ammo.Available < 1) throw new OperationException(409, "no_available_sonobuoy");
        if (!ammo.SingleRoundSupported) throw new OperationException(409, "single_drop_unavailable");
        var point = new GeoPosition(position.Latitude, position.Longitude, 0);
        var task = unit.SetAttackAtWaypointTask(ammunition, null, point, point, 1, null,
            EngageTask.SalvoType.PerLauncher, -1, formationAttack: false, attackOnlyDetected: false);
        if (task == null) throw new OperationException(409, "reservation_rejected");
        if (!unit._userRoot.TaskViewModels.Any(v => ReferenceEquals(v?.Task, task)))
            throw new InvalidOperationException("The sonobuoy reservation was not added to the native order queue.");
    }
}
