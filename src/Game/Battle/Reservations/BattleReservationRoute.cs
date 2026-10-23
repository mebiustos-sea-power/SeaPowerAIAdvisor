namespace SeaPowerAIAdvisor;

// Detached route facts; each operation decides how to use the existing reservation endpoint.
internal sealed class BattleReservationRoute
{
    internal AdvisorMapCoordinate CurrentPosition { get; }
    internal AdvisorMapCoordinate? EndPosition { get; }
    internal BattleReservationRoute(AdvisorMapCoordinate currentPosition, AdvisorMapCoordinate? endPosition)
    { CurrentPosition = currentPosition; EndPosition = endPosition; }
}
