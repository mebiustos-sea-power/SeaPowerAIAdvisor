using System;
using SeaPower;

namespace SeaPowerAIAdvisor;

// Read current-player units on the game thread. No reservation or simulation updates are issued here.
internal static class BattleReservationReader
{
    internal static BattleReservationRoute? ReadRoute(ObjectBase unit)
    {
        var root = unit._userRoot;
        if (root == null) return null;
        var current = unit.Position.Value;
        AdvisorMapCoordinate? end = null;
        if (root.TaskViewModels.Count != 0)
        {
            // Native simulation resolves relative/course orders and stores each displayed task's endpoint.
            var route = root.SimulatedRoute;
            if (route.Count == 0) throw new InvalidOperationException("The native reservation route is unavailable.");
            var position = route[route.Count - 1];
            end = new AdvisorMapCoordinate(position.Latitude, position.Longitude);
        }
        return new BattleReservationRoute(new AdvisorMapCoordinate(current.Latitude, current.Longitude), end);
    }
}
