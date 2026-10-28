using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport
{
    public void SetReservationLoop(string target, IReadOnlyList<AdvisorMapCoordinate> points)
    {
        Require("battle_movement_plans");
        var unit = Unit(target);
        if (MovementUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        if (points.Count < 2 || points.Select(p => (p.Latitude, p.Longitude)).Distinct().Count() < 2)
            throw new ArgumentException("A loop needs at least two distinct reservation points.", nameof(points));
        var tasks = unit._userRoot.TaskViewModels.Select(v => v.Task).ToArray();
        if (tasks.Length < points.Count) throw new InvalidOperationException("The loop reservation suffix is missing.");
        var suffix = tasks.Skip(tasks.Length - points.Count).ToArray();
        var template = new List<WaypointData>();
        foreach (var pair in suffix.Select((task, index) => (task, index)))
        {
            if (pair.task is not GoToWaypointTask task || task.GetType() != typeof(GoToWaypointTask) || task._returnToFormation ||
                task._waypointsData.Count != 0 || task._setCourse.value || task._setTurn.value || task._toCurrentPosition.value)
                throw new InvalidOperationException("The loop suffix is not an absolute movement route.");
            var position = task._waypointGeoPos.value;
            if (position.Latitude != points[pair.index].Latitude || position.Longitude != points[pair.index].Longitude)
                throw new InvalidOperationException("The registered loop suffix differs from the approved route.");
            template.Add(unit.CreateWaypointData(task));
        }
        // Capture the suffix before assigning it: the template must not contain a nested copy of itself.
        suffix.Last()._waypointsData = template;
        unit._userRoot.UpdateSimulation();
    }
}
