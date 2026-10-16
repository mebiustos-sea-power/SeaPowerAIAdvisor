using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Reflection is confined to the campaign adapter. No source object is serialized.
internal sealed class CampaignSituationReader
{
    internal const int ReportLimit = 30;
    private readonly Dictionary<(Type, string), FieldInfo> fields = new();

    internal SituationSnapshot Capture(object sim, Func<object, string> ownShipDisplayName)
    {
        if (ownShipDisplayName == null) throw new ArgumentNullException(nameof(ownShipDisplayName));
        var state = Object(sim, "State");
        var definition = Object(state, "Definition");
        if (!Read<bool>(definition, "fogOfWar"))
            throw new SituationUnavailableException("fog_disabled");
        var now = Read<DateTime>(state, "Now");
        var side = Read<string>(state, "PlayerSideId");
        if (string.IsNullOrEmpty(side)) throw new InvalidOperationException("The active campaign has no player side.");
        var allIntel = Dictionary(state, "Intel");
        if (!allIntel.Contains(side)) throw new InvalidOperationException("The player's intelligence record is missing.");
        var intel = allIntel[side] ?? throw new InvalidOperationException("The player's intelligence record is null.");
        if (Read<string>(intel, "SideId") != side) throw new InvalidOperationException("The intelligence record belongs to another side.");
        var bases = List(definition, "bases").ToArray();
        var byId = bases.ToDictionary(b => Read<string>(b, "id"), StringComparer.Ordinal);
        var owners = Dictionary(state, "BaseOwner");
        var forces = new List<FriendlyForce>();
        var ownForces = new List<object>();
        foreach (var force in List(state, "TaskForces"))
        {
            // Affiliation is checked before reading any force detail, including Name.
            if (Read<string>(force, "SideId") != side) continue;
            ownForces.Add(force);
            var ships = new List<FriendlyShip>();
            foreach (var hull in List(force, "Ships")) ships.Add(Ship(sim, hull, ownShipDisplayName));
            var portId = Optional<string>(force, "PortId");
            string? port = null;
            if (portId != null)
                port = Read<string>(byId.TryGetValue(portId, out var b) ? b : throw new InvalidOperationException("An own force has an unknown port."), "name");
            var mission = Optional<object>(force, "Mission");
            // Mission kind is our own order. TargetId/Point/AimedAt can reference hidden enemy state and are excluded.
            forces.Add(new FriendlyForce(Read<string>(force, "Name"), Position(Object(force, "Position")),
                Finite(Read<double>(force, "SpeedKnots")), port, mission == null ? "None" : EnumValue(mission, "Kind"), Optional<object>(force, "Convoy") != null, ships));
        }
        var contacts = new List<ObservedContact>();
        foreach (DictionaryEntry entry in Dictionary(intel, "Contacts"))
        {
            var contact = entry.Value ?? throw new InvalidOperationException("A contact record is null.");
            var seen = Read<DateTime>(contact, "LastSeen");
            ObservationTime(seen, now);
            // Match the campaign's forgetting rule. Historical knowledge is not a current track.
            if ((now - seen).TotalHours > 12) continue;
            var kind = EnumValue(contact, "Kind");
            if (kind != "Surface" && kind != "Submarine" && kind != "Air") throw new InvalidOperationException("Unknown contact classification: " + kind);
            var typicalSpeed = kind == "Air" ? 450 : kind == "Submarine" ? 15 : 25;
            contacts.Add(new ObservedContact(kind, Position(Object(contact, "Position")), seen,
                Nonnegative(Read<int>(contact, "Count")), Read<bool>(contact, "Identified"), (now - seen).TotalHours * typicalSpeed));
        }
        var observedBases = new List<ObservedBase>();
        var sightings = Dictionary(intel, "Bases");
        foreach (var b in bases)
        {
            var id = Read<string>(b, "id");
            var owner = owners.Contains(id) ? owners[id] as string : null;
            // These locations and ownership are public on the campaign map; defenses and forces are not.
            if (owner == null || owner == side) continue;
            var sharedPort = Call(definition, "ColocatedPort", b);
            var key = sharedPort == null ? id : Read<string>(sharedPort, "id");
            var sighting = sightings.Contains(key) ? sightings[key] : null;
            if (sighting != null && Read<string>(sighting, "Holder") != owner) sighting = null;
            DateTime? landAt = null;
            DateTime? forcesAt = null;
            if (sighting != null)
            {
                var at = Read<DateTime>(sighting, "At");
                if (at != DateTime.MinValue) { ObservationTime(at, now); landAt = at; }
                forcesAt = Optional<DateTime?>(sighting, "ForcesAt");
                if (forcesAt.HasValue) ObservationTime(forcesAt.Value, now);
            }
            observedBases.Add(new ObservedBase(Read<string>(b, "name"), EnumValue(b, "kind"),
                new MapPosition(Finite(Read<double>(b, "lat")), Finite(Read<double>(b, "lon"))), landAt,
                landAt.HasValue ? Nonnegative(Read<int>(sighting!, "Batteries")) : null,
                landAt.HasValue ? Nonnegative(Read<int>(sighting!, "SamSites")) : null,
                landAt.HasValue ? Nonnegative(Read<int>(sighting!, "GroundUnits")) : null, forcesAt,
                forcesAt.HasValue ? Nonnegative(Read<int>(sighting!, "ShipsInPort")) : null,
                forcesAt.HasValue ? Nonnegative(Read<int>(sighting!, "Aircraft")) : null, Array.IndexOf(bases, b)));
        }
        var orders = new List<PublishedOrder>();
        var publicOperations = new HashSet<int>();
        foreach (var operation in List(state, "Operations"))
        {
            if (Read<string>(operation, "SideId") != side || !PublicOrder(operation, now)) continue;
            publicOperations.Add(Read<int>(operation, "Id"));
            orders.Add(new PublishedOrder(Read<string>(operation, "Name"), Read<string>(operation, "Summary"),
                EnumValue(operation, "Status"), Read<DateTime>(operation, "Offered"), null));
        }
        foreach (var order in List(state, "AdmiraltyOrders"))
        {
            if (Read<string>(order, "SideId") != side || !PublicOrder(order, now)) continue;
            var operationId = Read<int>(order, "OperationId");
            if (operationId != 0 && !publicOperations.Contains(operationId)) continue;
            var deadline = Read<DateTime>(order, "Deadline");
            orders.Add(new PublishedOrder(Read<string>(order, "Title"), Read<string>(order, "Briefing"),
                EnumValue(order, "Status"), Read<DateTime>(order, "Offered"), deadline == DateTime.MinValue ? null : deadline));
        }
        var reports = new List<PublishedReport>();
        foreach (var report in List(state, "Feed"))
        {
            if (Read<string>(report, "SideId") != side) continue;
            var at = Read<DateTime>(report, "At");
            if (at > now) continue;
            reports.Add(new PublishedReport(at, EnumValue(report, "Kind"), Read<string>(report, "Text")));
        }
        // Even ordering must depend only on disclosed values, never on internal mover IDs.
        var orderedContacts = contacts.OrderByDescending(x => x.SeenAt).ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Position.Latitude).ThenBy(x => x.Position.Longitude).ThenBy(x => x.Count).ThenBy(x => x.Identified);
        return new SituationSnapshot(now, side, forces, orderedContacts, observedBases, orders.OrderBy(x => x.OfferedAt),
            reports.OrderBy(x => x.At).Skip(Math.Max(0, reports.Count - ReportLimit)), CampaignVictoryReader.Capture(state),
            CampaignAirReader.Capture(sim, ownForces, forces), CampaignGroundReader.Capture(sim, ownForces.ToArray()));
    }

    private bool PublicOrder(object order, DateTime now)
    {
        var status = EnumValue(order, "Status");
        if (status == "Pending") return false;
        if (status != "Offered" && status != "Active" && status != "Completed" && status != "Failed" &&
            status != "Declined" && status != "Lapsed" && status != "Cancelled")
            throw new InvalidOperationException("Unknown order publication status: " + status);
        var offered = Read<DateTime>(order, "Offered");
        return offered != DateTime.MinValue && offered <= now;
    }

    private FriendlyShip Ship(object sim, object hull, Func<object, string> ownShipDisplayName)
    {
        var catalog = Object(sim, "Catalog");
        var spec = Call(catalog, "Ship", Read<string>(hull, "UnitIni"))
            ?? throw new InvalidOperationException("An own ship has no catalog specification.");
        var nuclear = Property<bool>(spec, "Nuclear");
        var unarmed = Property<bool>(spec, "Unarmed");
        var ammo = unarmed ? (double?)null : (double)(Call(Object(sim, "Logistics"), "AmmoLevel", hull)
            ?? throw new InvalidOperationException("The own ship ammunition level is unavailable."));
        return new FriendlyShip(ownShipDisplayName(hull), Read<string>(spec, "TypeAbbr"), Finite(Read<double>(hull, "Fuel")),
            Finite(Read<double>(hull, "Damage")), nuclear, unarmed, Property<bool>(spec, "Merchant"), ammo.HasValue ? Finite(ammo.Value) : null);
    }

    private MapPosition Position(object point) => new(Finite(Read<double>(point, "Lat")), Finite(Read<double>(point, "Lon")));
    private string EnumValue(object source, string name) => Object(source, name).ToString()!;
    private object Object(object source, string name) => Field(source, name).GetValue(source)
        ?? throw new InvalidOperationException(source.GetType().FullName + "." + name + " is null.");
    private T Read<T>(object source, string name) => (T)Object(source, name);
    private T? Optional<T>(object source, string name) => (T?)Field(source, name).GetValue(source);
    private IEnumerable<object> List(object source, string name) => (Object(source, name) as IEnumerable
        ?? throw new InvalidOperationException(name + " is not a collection.")).Cast<object>();
    private IDictionary Dictionary(object source, string name) => Object(source, name) as IDictionary
        ?? throw new InvalidOperationException(name + " is not a dictionary.");
    private FieldInfo Field(object source, string name)
    {
        var key = (source.GetType(), name);
        if (!fields.TryGetValue(key, out var field))
        {
            field = source.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingFieldException(source.GetType().FullName, name);
            fields.Add(key, field);
        }
        return field;
    }
    private static T Property<T>(object source, string name) => (T)(source.GetType().GetProperty(name)?.GetValue(source)
        ?? throw new MissingMemberException(source.GetType().FullName, name));
    private static object? Call(object source, string name, object argument)
    {
        var method = source.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == name && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(argument))
            ?? throw new MissingMethodException(source.GetType().FullName, name);
        return method.Invoke(source, new[] { argument });
    }
    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value)
        ? throw new InvalidOperationException("The campaign contains a non-finite displayed value.") : value;
    private static int Nonnegative(int value) => value < 0 ? throw new InvalidOperationException("A sighting has a negative count.") : value;
    private static void ObservationTime(DateTime at, DateTime now)
    { if (at > now) throw new InvalidOperationException("An observation is dated after the campaign time."); }
}
