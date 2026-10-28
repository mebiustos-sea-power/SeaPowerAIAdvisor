using System;
using System.Collections.Generic;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Revokes retained SDK services on battle retirement; never exports native objects or permits worker-thread game access.
internal sealed class AdvisorOperationContext : IAdvisorOperationContext, IAdvisorGameSupport, IAdvisorReservationSupport, IAdvisorReservationSequenceSupport, IAdvisorMovementSupport, IAdvisorSensorSupport, IAdvisorCombatSupport, IAdvisorMap, IAdvisorOperationText, IDisposable
{
    private readonly IAdvisorGameSupport game;
    private readonly IAdvisorMap map;
    private readonly AdvisorOperationText text;
    private readonly List<Lease> drawings = new();
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool disposed;
    internal AdvisorOperationContext(IAdvisorGameSupport game, IAdvisorMap map, AdvisorOperationText text)
    { this.game = game; this.map = map; this.text = text; }
    public IAdvisorGameSupport Game { get { Check(); return this; } }
    public IAdvisorMap Map { get { Check(); return this; } }
    public IAdvisorOperationText Text { get { Check(); return this; } }
    internal void CheckThread()
    { if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Command support must run on its owning game thread."); }
    internal void Check()
    { CheckThread(); if (disposed) throw new ObjectDisposedException(nameof(AdvisorOperationContext)); }
    public bool IsInformationEnabled(string category) { Check(); return game.IsInformationEnabled(category); }
    public IReadOnlyList<GameUnitInfo> GetUnits() { Check(); return game.GetUnits(); }
    public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target) { Check(); return game.GetAmmunition(target); }
    public ReservationRoute? GetReservationRoute(string target) { Check(); return game.GetReservationRoute(target); }
    public string GetSonobuoyDepth(string target) { Check(); return game.GetSonobuoyDepth(target); }
    private IAdvisorReservationSequenceSupport Sequence => game as IAdvisorReservationSequenceSupport
        ?? throw new InvalidOperationException("The game adapter does not provide reservation sequences.");
    private IAdvisorMovementSupport Movement => game as IAdvisorMovementSupport
        ?? throw new InvalidOperationException("The game adapter does not provide movement support.");
    private IAdvisorSensorSupport Sensors => game as IAdvisorSensorSupport
        ?? throw new InvalidOperationException("The game adapter does not provide sensor support.");
    private IAdvisorCombatSupport Combat => game as IAdvisorCombatSupport
        ?? throw new InvalidOperationException("The game adapter does not provide combat support.");
    public IReadOnlyList<GameUnitInfo> GetCombatUnits() { Check(); return Combat.GetCombatUnits(); }
    public IReadOnlyList<CombatTargetInfo> GetAttackTargets() { Check(); return Combat.GetAttackTargets(); }
    public IReadOnlyList<AttackUnitInfo> GetAttackUnits() { Check(); return Combat.GetAttackUnits(); }
    public void IssueAttack(string target, AttackUpdateMode mode, IReadOnlyList<AttackInstruction> attacks) { Check(); Combat.IssueAttack(target, mode, attacks); }
    public void CeaseFire(string target) { Check(); Combat.CeaseFire(target); }
    public IReadOnlyList<SensorUnitInfo> GetSensorUnits() { Check(); return Sensors.GetSensorUnits(); }
    public void SetSensorControls(string target, IReadOnlyList<SensorControlChange> changes) { Check(); Sensors.SetSensorControls(target, changes); }
    public void ApplyEmcon(string target) { Check(); Sensors.ApplyEmcon(target); }
    public ReservationQueueState InspectReservations(string target, ReservationPlacement placement) { Check(); return Sequence.InspectReservations(target, placement); }
    public bool BeginReservations(string target, ReservationPlacement placement) { Check(); return Sequence.BeginReservations(target, placement); }
    public void SetReservationLoop(string target, IReadOnlyList<AdvisorMapCoordinate> points) { Check(); Sequence.SetReservationLoop(target, points); }
    public IReadOnlyList<MovementUnitInfo> GetMovementUnits() { Check(); return Movement.GetMovementUnits(); }
    public void AppendMovement(string target, MovementWaypoint waypoint) { Check(); Movement.AppendMovement(target, waypoint); }
    public void AppendFormationReturn(string target) { Check(); Movement.AppendFormationReturn(target); }
    public void ClearReservations(string target)
    {
        Check();
        var reservations = game as IAdvisorReservationSupport
            ?? throw new InvalidOperationException("The game adapter does not provide reservation control.");
        reservations.ClearReservations(target);
    }
    public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position)
    { Check(); game.AppendSonobuoyDrop(target, ammunition, position); }
    public string Get(string language, string key) { Check(); return text.For(language)[key]; }
    public string Format(string language, string key, params object[] arguments) { Check(); return text.For(language).Format(key, arguments); }
    public void Center(AdvisorMapCoordinate location) { Check(); map.Center(location); }
    public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> values)
    {
        Check(); var lease = new Lease(this, map.Draw(values)); drawings.Add(lease); return lease;
    }
    public void Dispose()
    {
        CheckThread(); if (disposed) return;
        disposed = true;
        var errors = new List<Exception>();
        foreach (var lease in drawings.ToArray())
            try { lease.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Command drawings could not be fully cleared.", errors);
    }
    private sealed class Lease : IDisposable
    {
        private readonly AdvisorOperationContext owner;
        private IDisposable? drawing;
        internal Lease(AdvisorOperationContext owner, IDisposable drawing) { this.owner = owner; this.drawing = drawing; }
        public void Dispose()
        {
            owner.CheckThread(); var previous = drawing; drawing = null;
            owner.drawings.Remove(this); previous?.Dispose();
        }
    }
}
