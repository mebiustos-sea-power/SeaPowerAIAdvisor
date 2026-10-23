using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAIAdvisor;

// Constructed only from the current player's taskforce. All reads and writes run on the game thread.
internal sealed class BattleSonobuoyUnit : IBattleSonobuoyUnit
{
    private readonly ObjectBase unit;
    internal BattleSonobuoyUnit(ObjectBase unit) => this.unit = unit;
    internal static bool Supports(ObjectBase unit) => unit.AmmunitionNameToAmmunitionDictionary.Values.Any(IsSonobuoy);
    private static bool IsSonobuoy(SeaPower.Ammunition ammunition) =>
        ammunition._ap._type == SeaPower.Ammunition.Type.Bomb && ammunition._ap._subType == SeaPower.Ammunition.Type.Sonobuoy;
    public int Id => unit.Id;
    public string Name => unit.Name.Value;
    public JObject Position => AdvisorInputBuilder.Position(new MapPosition(unit.Position.Value.Latitude, unit.Position.Value.Longitude));
    public BattleReservationRoute? ReservationRoute => BattleReservationReader.ReadRoute(unit);
    public string Depth => unit.SonoDeployState.Value switch
    {
        Bomb.SonobuoyDeployState.AboveLayer => "above_layer",
        Bomb.SonobuoyDeployState.BelowLayer => "below_layer",
        Bomb.SonobuoyDeployState.Intermittent => "alternating",
        _ => throw new InvalidOperationException("The unit sonobuoy depth setting is unsupported.")
    };
    public string? UnavailableReason => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : (unit is Helicopter helicopter && !helicopter._isInFlight) ||
            (unit is Aircraft aircraft && !aircraft._isInFlight) ? "unit_not_airborne"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable"
        : unit._userRoot == null ? "target_not_ready"
        : unit.FollowingFormation && !unit.IsFormationLeader ? "following_formation"
        : unit.CurrentOrder.Value.OrderType != Order.Type.None ? "current_order_conflict" : null;

    public IReadOnlyList<BattleSonobuoyAmmo> Ammunition()
    {
        var result = new List<BattleSonobuoyAmmo>();
        foreach (var pair in unit.AmmunitionNameToAmmunitionDictionary)
        {
            if (!IsSonobuoy(pair.Value)) continue;
            var ammo = pair.Value._ap;
            var remaining = unit.getAmmunitionAmountByName(pair.Key);
            var reserved = unit._userRoot == null ? 0 : unit._userRoot.TaskViewModels
                .Where(v => v?.Task is AttackAtWaypoint attack && attack._ammunitionName == pair.Key)
                .Sum(v => ((AttackAtWaypoint)v.Task)._salvo);
            reserved += unit._currentEngageTasks.Where(t => t._ammoId == pair.Key).Sum(t => t._shotsToFire);
            reserved += unit._obp._weaponSystems.Count(w => w._executingEngageTask &&
                w._ammoForEngage._ap._ammunitionFileName == pair.Key);
            var launchers = unit.GetWeaponSystemsForAmmunition(pair.Key, false);
            var single = launchers.Count > 0 && launchers.All(w => w is WeaponSystemHardpoint &&
                (BattleValue.Required(w, "_salvoAmounts") is Dictionary<string, int> amounts
                    ? !amounts.TryGetValue(pair.Key, out var count) || count == 1
                    : throw new InvalidOperationException("The hardpoint salvo amounts are unavailable.")));
            result.Add(new BattleSonobuoyAmmo(pair.Key, ammo._displayedName, remaining, Math.Max(0, remaining - reserved), single));
        }
        return result;
    }
    public void Append(string ammunition, double latitude, double longitude)
    {
        var point = new GeoPosition(latitude, longitude, 0);
        var task = unit.SetAttackAtWaypointTask(ammunition, null, point, point, 1, null,
            EngageTask.SalvoType.PerLauncher, -1, formationAttack: false, attackOnlyDetected: false);
        if (task == null) throw new AdvisorOperationException(409, "reservation_rejected");
        if (!unit._userRoot.TaskViewModels.Any(v => ReferenceEquals(v?.Task, task)))
            throw new InvalidOperationException("The sonobuoy reservation was not added to the native order queue.");
    }
}
