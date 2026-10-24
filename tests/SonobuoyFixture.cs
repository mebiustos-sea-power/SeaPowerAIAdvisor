using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal interface IBattleSonobuoyUnit
{
    int Id { get; }
    string Name { get; }
    string? UnavailableReason { get; }
    string Depth { get; }
    JObject Position { get; }
    ReservationRoute? ReservationRoute { get; }
    IReadOnlyList<BattleSonobuoyAmmo> Ammunition();
    void Append(string ammunition, double latitude, double longitude);
}

internal sealed class BattleSonobuoyAmmo
{
    internal string Id { get; }
    internal string Name { get; }
    internal int Remaining { get; }
    internal int Available { get; }
    internal bool SingleDrop { get; }
    internal BattleSonobuoyAmmo(string id, string name, int remaining, int available, bool singleDrop)
    { Id = id; Name = name; Remaining = remaining; Available = available; SingleDrop = singleDrop; }
}


// Adapts the existing queue/state fixtures into the same public support used by production commands.
internal static class SonobuoyFixture
{
    internal static AdvisorRegisteredOperation Create(Func<IReadOnlyList<IBattleSonobuoyUnit>> units, Func<AdvisorInformation>? information = null)
    {
        var catalog = new AdvisorOperationCatalog(); catalog.Register(new SonobuoyDropExtension());
        return catalog.CreateOperations(new Game(units, information ?? (() => AdvisorInformation.Default)), new Map()).Single();
    }
    private sealed class Game : IAdvisorGameSupport
    {
        private readonly Func<IReadOnlyList<IBattleSonobuoyUnit>> units;
        private readonly Func<AdvisorInformation> information;
        internal Game(Func<IReadOnlyList<IBattleSonobuoyUnit>> units, Func<AdvisorInformation> information) { this.units = units; this.information = information; }
        public bool IsInformationEnabled(string category) => information().Allows(AdvisorInformation.Items.Single(i => i.Id == category).Kind);
        public IReadOnlyList<GameUnitInfo> GetUnits() => units().Select(u => new GameUnitInfo(u.Id, u.Name,
            u.Position["latitude_deg"] is JValue lat && u.Position["longitude_deg"] is JValue lon
                ? new AdvisorMapCoordinate((double)lat, (double)lon) : null,
            true, u.UnavailableReason != "unit_not_airborne", u.UnavailableReason == "unit_not_airborne" ? null : u.UnavailableReason)).ToArray();
        private IBattleSonobuoyUnit Unit(string target) => units().Single(u => "unit:" + u.Id == target);
        public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target) => Unit(target).Ammunition()
            .Select(a => new AmmunitionInfo(a.Id, a.Name, "Bomb", "Sonobuoy", a.Remaining, a.Available, a.SingleDrop)).ToArray();
        public ReservationRoute? GetReservationRoute(string target)
            => Unit(target).ReservationRoute;
        public string GetSonobuoyDepth(string target) => Unit(target).Depth;
        public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position) => Unit(target).Append(ammunition, position.Latitude, position.Longitude);
    }
    private sealed class Map : IAdvisorMap
    {
        public void Center(AdvisorMapCoordinate position) => throw new InvalidOperationException("Command must not move the map while describing a plan.");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings) => throw new InvalidOperationException("The approval view owns drawing.");
    }
}
