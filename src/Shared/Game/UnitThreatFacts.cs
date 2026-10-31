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
        if (Rows(Required(unit, "_rwrBearings")).Length > 0) alerts.Add("radar_illumination");
        if (HasJammingWarning(unit)) alerts.Add("jamming");
        return alerts;
    }

    private static bool HasJammingWarning(object unit)
    {
        // Match RefreshPlayerThreatAlerts, including its exclusion of defensive-only and inoperable systems.
        foreach (var system in Rows(Required(unit, "_ECMSystemsTryingToJamMe")))
        {
            if (system == null || Member(system, "_baseObject") == null || (bool)Required(system, "IsDestroyed") ||
                (bool)Required(Required(system, "Inoperable"), "Value")) continue;
            var parameters = Required(Required(system, "_ecm"), "_ep");
            if (Code(Required(parameters, "_type")) is "offensive" or "universal") return true;
        }
        return false;
    }
}
