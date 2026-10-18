using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Game access is restricted to own units before reading their names, aircraft, or service data.
internal static class CampaignAirReader
{
    internal static FriendlyAviationSnapshot Capture(object sim, IReadOnlyList<object> ownForces, IReadOnlyList<FriendlyForce> projectedForces)
    {
        var state = Value(sim, "State");
        var definition = Value(state, "Definition");
        var catalog = Value(sim, "Catalog");
        var side = Read<string>(state, "PlayerSideId");
        var bases = Items(definition, "bases").ToArray();
        var units = new List<FriendlyAirUnit>();
        var squadrons = new Dictionary<int, int>();
        var carriers = new Dictionary<int, int>();
        foreach (var squadron in Items(state, "Squadrons"))
        {
            if (Read<string>(squadron, "SideId") != side) continue;
            var frames = Items(squadron, "Airframes").ToArray();
            if (frames.Length == 0) continue;
            var baseId = Optional<string>(squadron, "BaseId");
            int? baseIndex = baseId == null ? null : Array.FindIndex(bases, b => Read<string>(b, "id") == baseId);
            if (baseIndex < 0) throw new InvalidOperationException("An own squadron has an unknown base.");
            DateTime? runway = baseIndex.HasValue ? (DateTime?)Static(state, "BaseStrikes", "RunwayOutUntil", state, bases[baseIndex.Value]) : null;
            var aircraft = new List<FriendlyAircraftType>();
            foreach (var group in frames.GroupBy(f => Read<string>(f, "UnitIni"), StringComparer.Ordinal))
            {
                var counts = new Dictionary<string, int?> { ["total"] = group.Count(), ["ready_unarmed"] = 0,
                    ["arming"] = 0, ["armed"] = 0, ["airborne"] = 0, ["maintenance"] = 0 };
                foreach (var frame in group)
                {
                    var status = Value(frame, "Status").ToString();
                    var key = status switch { "Ready" => "ready_unarmed", "Arming" => "arming", "Armed" => "armed",
                        "Airborne" => "airborne", "Maintenance" => "maintenance",
                        _ => throw new InvalidOperationException("Unknown own aircraft status: " + status) };
                    counts[key]++;
                }
                var spec = Call(catalog, "Aircraft", group.Key) ?? throw new InvalidOperationException("An own aircraft has no specification.");
                aircraft.Add(new FriendlyAircraftType(DisplayName(catalog, group.Key), true, counts,
                    Items(spec, "Loadouts").Select(l => Read<string>(l, "Name"))));
            }
            squadrons.Add(Read<int>(squadron, "Id"), units.Count);
            units.Add(new FriendlyAirUnit("land_squadron", Read<string>(squadron, "Name"), baseIndex, null, null,
                Optional<string>(squadron, "AirliftOf") != null, baseIndex.HasValue ? runway.HasValue : (bool?)null,
                null, runway, null, aircraft));
        }
        for (var fi = 0; fi < ownForces.Count; fi++)
        {
            var force = ownForces[fi];
            if (Read<string>(force, "SideId") != side) throw new InvalidOperationException("An enemy force reached own aviation capture.");
            var hulls = Items(force, "Ships").ToArray();
            for (var si = 0; si < hulls.Length; si++)
            {
                var hull = hulls[si];
                var spec = Call(catalog, "Ship", Read<string>(hull, "UnitIni")) ?? throw new InvalidOperationException("An own ship has no specification.");
                if (!(bool)Call(spec, "HasRole", "Carrier")! && !(bool)Call(spec, "HasRole", "CVS")!) continue;
                var deck = Value(sim, "Deck");
                // These are the same read-only calculations used by CarrierAirViewModel.Refresh.
                var wing = ((IEnumerable)(Call(deck, "Wing", hull) ?? throw new InvalidOperationException("Own carrier wing is unavailable."))).Cast<object>().ToArray();
                if (wing.Length == 0) continue;
                var reason = (string?)Call(deck, "LaunchRefused", hull);
                var aircraft = wing.Select(type =>
                {
                    var key = Read<string>(type, "Type");
                    var supported = Read<bool>(type, "Flies");
                    var counts = new Dictionary<string, int?> { ["nominal_complement"] = Count(type, "Full"), ["aboard"] = Count(type, "Aboard"),
                        ["available_for_preparation"] = supported ? Count(type, "Launchable") : null,
                        ["cooling"] = supported ? Count(type, "Cooling") : null, ["arming"] = supported ? Count(type, "Arming") : null,
                        ["airborne"] = supported ? Count(type, "Flying") : null, ["lost"] = Count(type, "Lost") };
                    var loadouts = supported
                        ? ((IEnumerable)(StaticCall(deck.GetType(), "Loadouts", catalog, key)
                            ?? throw new InvalidOperationException("Carrier loadouts are unavailable."))).Cast<object>()
                            .Select(l => Read<string>(l, "Name")).ToArray()
                        : Array.Empty<string>();
                    return new FriendlyAircraftType(DisplayName(catalog, key), supported, counts, loadouts);
                }).ToArray();
                carriers.Add(Read<int>(hull, "Id"), units.Count);
                units.Add(new FriendlyAirUnit("carrier_air_wing", projectedForces[fi].Ships[si].Name, null, fi, si, false,
                    reason != null, reason, null, (int)(Call(deck, "CycleLeft", hull) ?? throw new InvalidOperationException("The deck cycle is unavailable.")), aircraft));
            }
        }
        var flights = new List<FriendlyAirFlight>();
        foreach (var flight in Items(state, "Flights"))
        {
            if (Read<string>(flight, "SideId") != side) continue;
            int? source = null;
            var carrier = Read<int>(flight, "CarrierHullId");
            if (carrier != 0 && carriers.TryGetValue(carrier, out var ci)) source = ci;
            else if (squadrons.TryGetValue(Read<int>(flight, "SquadronId"), out var qi)) source = qi;
            var position = Value(flight, "Position");
            var lat = Read<double>(position, "Lat"); var lon = Read<double>(position, "Lon");
            if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon))
                throw new InvalidOperationException("An own flight position is not finite.");
            var aircraft = Call(catalog, "Aircraft", Read<string>(flight, "UnitIni")) ?? throw new InvalidOperationException("An own flight has no aircraft specification.");
            flights.Add(new FriendlyAirFlight(Read<string>(flight, "Name"), Read<string>(aircraft, "DisplayName"), new MapPosition(lat, lon),
                Items(flight, "Airframes").Count(), Read<string>(flight, "LoadoutName"), Read<bool>(flight, "Returning"), source));
        }
        return new FriendlyAviationSnapshot(units, flights);
    }

    private static int Count(object source, string name)
    {
        var count = Read<int>(source, name);
        return count >= 0 ? count : throw new InvalidOperationException("An own aircraft count is negative.");
    }
    private static string DisplayName(object catalog, string key) => Call(catalog, "DisplayName", key) as string
        ?? throw new InvalidOperationException("An own aircraft has no display name.");
    private static object? Static(object state, string type, string method, params object[] args) =>
        StaticCall(state.GetType().Assembly.GetType("DynamicCampaignMod.Core.Sim." + type, true)!, method, args);
    private static object? StaticCall(Type type, string method, params object[] args) =>
        (type.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m => m.Name == method &&
            m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(matches => matches))
            ?? throw new MissingMethodException(type.FullName, method)).Invoke(null, args);
    private static object? Call(object source, string name, params object[] args) =>
        (source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(source.GetType().FullName, name)).Invoke(source, args);
    private static FieldInfo Field(object source, string name) => source.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingFieldException(source.GetType().FullName, name);
    private static object Value(object source, string name) => Field(source, name).GetValue(source)
        ?? throw new InvalidOperationException(source.GetType().FullName + "." + name + " is null.");
    private static T Read<T>(object source, string name) => (T)Value(source, name);
    private static T? Optional<T>(object source, string name) => (T?)Field(source, name).GetValue(source);
    private static IEnumerable<object> Items(object source, string name) => ((IEnumerable)Value(source, name)).Cast<object>();
}
