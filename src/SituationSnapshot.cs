using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Only these detached value objects can reach the preview or a future AI request.
internal sealed class SituationSnapshot
{
    internal DateTime At { get; }
    internal string Side { get; }
    internal IReadOnlyList<FriendlyForce> Forces { get; }
    internal IReadOnlyList<ObservedContact> Contacts { get; }
    internal IReadOnlyList<ObservedBase> Bases { get; }
    internal IReadOnlyList<PublishedOrder> Orders { get; }
    internal IReadOnlyList<PublishedReport> Reports { get; }
    internal SituationSnapshot(DateTime at, string side, IEnumerable<FriendlyForce> forces, IEnumerable<ObservedContact> contacts,
        IEnumerable<ObservedBase> bases, IEnumerable<PublishedOrder> orders, IEnumerable<PublishedReport> reports)
    {
        At = at; Side = side; Forces = Array.AsReadOnly(forces.ToArray()); Contacts = Array.AsReadOnly(contacts.ToArray());
        Bases = Array.AsReadOnly(bases.ToArray()); Orders = Array.AsReadOnly(orders.ToArray()); Reports = Array.AsReadOnly(reports.ToArray());
    }
}

internal sealed class MapPosition
{
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal MapPosition(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; }
}

internal sealed class FriendlyForce
{
    internal string Name { get; }
    internal MapPosition Position { get; }
    internal double SpeedKnots { get; }
    internal string? Port { get; }
    internal string Mission { get; }
    internal IReadOnlyList<FriendlyShip> Ships { get; }
    internal FriendlyForce(string name, MapPosition position, double speed, string? port, string mission, IEnumerable<FriendlyShip> ships)
    { Name = name; Position = position; SpeedKnots = speed; Port = port; Mission = mission; Ships = Array.AsReadOnly(ships.ToArray()); }
}

internal sealed class FriendlyShip
{
    internal string Name { get; }
    internal string Type { get; }
    internal double FuelShare { get; }
    internal double DamageShare { get; }
    internal bool Nuclear { get; }
    internal bool Unarmed { get; }
    internal double? AmmoShare { get; }
    internal FriendlyShip(string name, string type, double fuel, double damage, bool nuclear, bool unarmed, double? ammo)
    { Name = name; Type = type; FuelShare = fuel; DamageShare = damage; Nuclear = nuclear; Unarmed = unarmed; AmmoShare = ammo; }
}

internal sealed class ObservedContact
{
    internal string Kind { get; }
    internal MapPosition Position { get; }
    internal DateTime SeenAt { get; }
    internal int Count { get; }
    internal bool Identified { get; }
    internal double UncertaintyNm { get; }
    internal ObservedContact(string kind, MapPosition position, DateTime seenAt, int count, bool identified, double uncertainty)
    { Kind = kind; Position = position; SeenAt = seenAt; Count = count; Identified = identified; UncertaintyNm = uncertainty; }
}

internal sealed class ObservedBase
{
    internal string Name { get; }
    internal string Kind { get; }
    internal MapPosition Position { get; }
    internal DateTime? LandSeenAt { get; }
    internal int? Batteries { get; }
    internal int? SamSites { get; }
    internal int? GroundUnits { get; }
    internal DateTime? ForcesSeenAt { get; }
    internal int? Ships { get; }
    internal int? Aircraft { get; }
    internal ObservedBase(string name, string kind, MapPosition position, DateTime? landAt, int? batteries, int? sam, int? ground,
        DateTime? forcesAt, int? ships, int? aircraft)
    { Name = name; Kind = kind; Position = position; LandSeenAt = landAt; Batteries = batteries; SamSites = sam; GroundUnits = ground;
      ForcesSeenAt = forcesAt; Ships = ships; Aircraft = aircraft; }
}

internal sealed class PublishedOrder
{
    internal string Title { get; }
    internal string Briefing { get; }
    internal string Status { get; }
    internal DateTime OfferedAt { get; }
    internal DateTime? Deadline { get; }
    internal PublishedOrder(string title, string briefing, string status, DateTime offered, DateTime? deadline)
    { Title = title; Briefing = briefing; Status = status; OfferedAt = offered; Deadline = deadline; }
}

internal sealed class PublishedReport
{
    internal DateTime At { get; }
    internal string Kind { get; }
    internal string Text { get; }
    internal PublishedReport(DateTime at, string kind, string text) { At = at; Kind = kind; Text = text; }
}

internal sealed class SituationUnavailableException : InvalidOperationException
{
    internal SituationUnavailableException(string reason) : base(reason) { }
}
