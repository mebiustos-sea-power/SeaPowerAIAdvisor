using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport : IAdvisorReservationSupport
{
    // Cancellation does not require a unit to be free for new movement orders.
    public IReadOnlyList<GameUnitInfo> GetReservationUnits()
    {
        Require("battle_units");
        return VisibleUnits().Select(unit => new GameUnitInfo(unit.Id, unit.Name.Value, null, unit.IsAirUnit,
            unit is Aircraft a ? a._isInFlight : unit is Helicopter h && h._isInFlight,
            new BattleReservationUnit(unit).UnavailableReason)).ToArray();
    }
    public void ClearReservations(string target)
    {
        var unit = Unit(target); var reservations = new BattleReservationUnit(unit);
        if (reservations.UnavailableReason is string reason) throw new OperationException(409, reason);
        reservations.ClearReservations();
        if (unit._userRoot.TaskViewModels.Count != 0)
            throw new System.InvalidOperationException("The native reservation queue was not cleared.");
    }
}
