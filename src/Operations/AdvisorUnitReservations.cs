using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorReservationUnit
{
    int Id { get; }
    string Name { get; }
    string? UnavailableReason { get; }
    JArray Reservations();
    void ClearReservations();
}

// Developer inspection/reset uses the native queue, independently of AI operation receipts.
internal sealed class AdvisorUnitReservations
{
    private readonly AdvisorOperations operations;
    private readonly Func<IReadOnlyList<IAdvisorReservationUnit>> units;
    internal AdvisorUnitReservations(AdvisorOperations operations, Func<IReadOnlyList<IAdvisorReservationUnit>> units)
    { this.operations = operations; this.units = units; }

    internal JObject Read(int unitId)
    {
        var unit = Resolve(unitId);
        return View(unit, unit.Reservations());
    }
    internal JObject Clear(int unitId, string scope)
    {
        CheckEnabled();
        if (scope != operations.ScopeId) throw new AdvisorOperationException(409, "operation_scope_changed");
        var unit = Resolve(unitId);
        var count = unit.Reservations().Count;
        unit.ClearReservations();
        var remaining = unit.Reservations();
        if (remaining.Count != 0) throw new InvalidOperationException("The native reservation queue was not cleared.");
        var result = View(unit, remaining);
        result["removed_count"] = count;
        return result;
    }
    private IAdvisorReservationUnit Resolve(int unitId)
    {
        CheckEnabled();
        var unit = units().SingleOrDefault(u => u.Id == unitId)
            ?? throw new AdvisorOperationException(409, "target_unavailable");
        if (unit.UnavailableReason is string reason) throw new AdvisorOperationException(409, reason);
        return unit;
    }
    private void CheckEnabled()
    {
        if (!operations.Enabled) throw new AdvisorOperationException(403, "unit_operations_disabled");
    }
    private JObject View(IAdvisorReservationUnit unit, JArray reservations) => new()
    {
        ["scope_id"] = operations.ScopeId, ["unit_id"] = unit.Id,
        ["target_ref"] = "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture), ["display_name"] = unit.Name,
        ["count"] = reservations.Count, ["reservations"] = reservations.DeepClone()
    };
}
