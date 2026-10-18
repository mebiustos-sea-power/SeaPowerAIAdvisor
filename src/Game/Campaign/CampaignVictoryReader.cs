using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Use the same read-only objective/progress APIs as VictoryPanelViewModel.Card. Never invoke Victory.Check.
internal static class CampaignVictoryReader
{
    internal static VictorySnapshot Capture(object state)
    {
        var definition = Read<object>(state, "Definition");
        var sourceBases = Sequence(definition, "bases");
        var sides = Sequence(definition, "sides");
        var sideIds = sides.Select(s => Read<string>(s, "id")).ToArray();
        var player = Read<string>(state, "PlayerSideId");
        if (sideIds.Length == 0 || sideIds.Any(string.IsNullOrEmpty) ||
            sideIds.Distinct(StringComparer.Ordinal).Count() != sideIds.Length || !sideIds.Contains(player))
            throw new InvalidOperationException("The campaign's public sides are inconsistent.");
        var owners = Read<IDictionary>(state, "BaseOwner");
        var bases = sourceBases.Select(b =>
        {
            var id = Read<string>(b, "id");
            var owner = owners.Contains(id) ? owners[id] : null;
            if (owner != null && !(owner is string))
                throw new InvalidOperationException("A public base owner is not a side identifier.");
            var lat = Read<double>(b, "lat"); var lon = Read<double>(b, "lon");
            if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon))
                throw new InvalidOperationException("An objective base has a non-finite position.");
            return new VictoryBase(Read<string>(b, "name"), Read<object>(b, "kind").ToString()!,
                new MapPosition(lat, lon), (string?)owner);
        }).ToArray();
        var victory = state.GetType().Assembly.GetType("DynamicCampaignMod.Core.Sim.Victory", true)!;
        var objectivesMethod = Required(victory, "Objectives", BindingFlags.Public | BindingFlags.Static, state.GetType(), typeof(string));
        var progressMethod = Required(victory, "Progress", BindingFlags.Public | BindingFlags.Static, state.GetType(), typeof(string));
        var result = new List<VictorySide>();
        foreach (var side in sides.OrderBy(s => Read<string>(s, "id") == player ? 0 : 1))
        {
            var id = Read<string>(side, "id");
            var objectives = ((IEnumerable)(objectivesMethod.Invoke(null, new[] { state, id })
                ?? throw new InvalidOperationException("The campaign returned no objective list."))).Cast<object>().ToArray();
            var indices = objectives.Select(b =>
            {
                var index = Array.IndexOf(sourceBases, b);
                return index >= 0 ? index : throw new InvalidOperationException("An objective is outside the campaign base list.");
            }).ToArray();
            if (indices.Distinct().Count() != indices.Length)
                throw new InvalidOperationException("The campaign returned duplicate objectives.");
            var progress = progressMethod.Invoke(null, new[] { state, id })
                ?? throw new InvalidOperationException("The campaign returned no victory progress.");
            var held = Read<int>(progress, "Item1");
            if (Read<int>(progress, "Item2") != indices.Length || held != indices.Count(i => bases[i].OwnerSide == id))
                throw new InvalidOperationException("Victory progress disagrees with the public objectives and ownership.");
            var included = new HashSet<int>(indices);
            var excluded = new List<ExcludedVictoryBase>();
            for (var i = 0; i < sourceBases.Length; i++)
            {
                if (included.Contains(i)) continue;
                var b = sourceBases[i];
                string reason;
                if (Read<bool>(b, "homeland") && Read<string>(b, "side") != id) reason = "opposing_homeland";
                else if ((bool)(Required(b.GetType(), "IsObjectiveExemptFor", BindingFlags.Public | BindingFlags.Instance, typeof(string))
                    .Invoke(b, new object[] { id }) ?? throw new InvalidOperationException("An objective exemption is unavailable.")))
                    reason = "side_specific_exemption";
                else throw new InvalidOperationException("The campaign excluded a base for an unsupported victory rule.");
                excluded.Add(new ExcludedVictoryBase(i, reason));
            }
            result.Add(new VictorySide(id, Read<string>(side, "name"), held, indices, excluded));
        }
        var recordedWinner = Value(state, "Victor");
        if (recordedWinner != null && !(recordedWinner is string))
            throw new InvalidOperationException("The recorded winner is not a side identifier.");
        var winner = (string?)recordedWinner;
        DateTime? decided = null;
        if (winner != null)
        {
            if (!sideIds.Contains(winner)) throw new InvalidOperationException("The recorded winner is not a campaign side.");
            decided = Read<DateTime>(state, "VictoryAt");
            if (decided == DateTime.MinValue || decided > Read<DateTime>(state, "Now"))
                throw new InvalidOperationException("The recorded victory time is invalid.");
        }
        return new VictorySnapshot(bases, result, winner, decided);
    }

    private static object? Value(object source, string name) =>
        (source.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException(source.GetType().FullName, name)).GetValue(source);
    private static T Read<T>(object source, string name) => (T)(Value(source, name)
        ?? throw new InvalidOperationException(source.GetType().FullName + "." + name + " is null."));
    private static object[] Sequence(object source, string name) => Read<IEnumerable>(source, name).Cast<object>().ToArray();
    private static MethodInfo Required(Type type, string name, BindingFlags flags, params Type[] parameters) =>
        type.GetMethod(name, flags, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
}
