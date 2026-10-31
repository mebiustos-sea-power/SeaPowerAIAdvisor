using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor.Shared;

// Read current warnings only after the caller establishes player ownership.
internal static class UnitThreatFacts
{
    internal static JArray ActiveAlerts(object unit)
    {
        var alerts = new JArray();
        var missile = Member(unit, "ThreateningMissile");
        // The game's missile warning requires this unit to be the missile's current target.
        if (missile != null && !(bool)Required(missile, "IsDestroyed") && ReferenceEquals(Member(missile, "CurrentTarget"), unit))
            alerts.Add("missile");
        var torpedo = Member(unit, "ThreatingTorpedo");
        if (torpedo != null && !(bool)Required(torpedo, "IsDestroyed")) alerts.Add("torpedo");
        return alerts;
    }
}
