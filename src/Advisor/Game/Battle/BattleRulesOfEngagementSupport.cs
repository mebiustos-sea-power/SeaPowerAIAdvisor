using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport : IAdvisorRulesOfEngagementSupport
{
    private static GameUnitInfo EngagementIdentity(ObjectBase unit) => new(unit.Id, unit.Name.Value, null,
        unit.IsAirUnit, unit is Aircraft a ? a._isInFlight : unit is Helicopter h && h._isInFlight,
        unit.IsDestroyed || unit.Disabled ? "target_unavailable" : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable" : null);
    public IReadOnlyList<RulesOfEngagementUnitInfo> GetRulesOfEngagementUnits()
    {
        Require("battle_units");
        return VisibleUnits().Select(unit => new RulesOfEngagementUnitInfo(EngagementIdentity(unit), unit._weaponStatus switch
        {
            ObjectBase.WeaponStatus.Free => RulesOfEngagement.Free,
            ObjectBase.WeaponStatus.Tight => RulesOfEngagement.Tight,
            ObjectBase.WeaponStatus.Hold => RulesOfEngagement.Hold,
            _ => throw new InvalidOperationException("The unit's rules of engagement are unsupported.")
        })).ToArray();
    }
    public void SetRulesOfEngagement(string target, RulesOfEngagement mode)
    {
        var unit = Unit(target);
        if (EngagementIdentity(unit).OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        var native = mode switch
        {
            RulesOfEngagement.Free => ObjectBase.WeaponStatus.Free,
            RulesOfEngagement.Tight => ObjectBase.WeaponStatus.Tight,
            RulesOfEngagement.Hold => ObjectBase.WeaponStatus.Hold,
            _ => throw new OperationException(400, "invalid_rules_of_engagement_arguments")
        };
        // Use the policy setter directly: the UI's Hold action also ceases fire and can propagate through a formation.
        unit.SetWeaponStatus(native, formationCheck: false);
    }
}
