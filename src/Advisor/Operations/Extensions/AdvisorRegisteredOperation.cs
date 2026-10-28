using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Api = SeaPowerAIAdvisor.Api;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// All built-in and external commands cross this boundary before entering the existing approval engine.
internal sealed class AdvisorRegisteredOperation : IAdvisorOperation, IAdvisorOperationPlans,
    IAdvisorOperationPreview, IAdvisorOperationArgumentNormalizer, IAdvisorReservationCommand, IAdvisorExtensionVersion, IDisposable
{
    private readonly AdvisorOperationRegistration registration;
    private readonly AdvisorOperationContext context;
    private Api.IAdvisorOperationHandler? handler;
    private bool disposed;
    internal AdvisorRegisteredOperation(AdvisorOperationRegistration registration, IAdvisorGameSupport game, IAdvisorMap map)
    { this.registration = registration; context = new AdvisorOperationContext(game, map, registration.Text); }
    public string? Version => registration.Version;
    public bool ComposesReservations => registration.Extension is IAdvisorReservationExtension;
    public bool EndsReservationSequence => (registration.Extension as IAdvisorReservationExtension)?.EndsReservationSequence ?? false;
    public bool SupportsLoop => (registration.Extension as IAdvisorReservationExtension)?.SupportsLoop ?? false;
    public IReadOnlyList<AdvisorMapCoordinate> GetLoopPoints(ReservationOrder order) => Call(h =>
        h is IAdvisorLoopableReservationOperation loop ? loop.GetLoopPoints(order)
        : throw new InvalidOperationException("A loop-capable command must provide its reservation points."));
    public void SetLoop(string target, IReadOnlyList<AdvisorMapCoordinate> points) => Translate(() =>
    { context.SetReservationLoop(target, points); return true; });
    private static IAdvisorReservationOperation Reservation(Api.IAdvisorOperationHandler handler) => handler as IAdvisorReservationOperation
        ?? throw new InvalidOperationException("Reservation extension must implement the reservation handler contract.");
    public ReservationQueueState InspectQueue(string target, ReservationPlacement placement) => Translate(() => context.InspectReservations(target, placement));
    public bool BeginQueue(string target, ReservationPlacement placement) => Translate(() => context.BeginReservations(target, placement));
    public void ValidateReservations(IReadOnlyList<ReservationOrder> orders) => Call(h => { Reservation(h).ValidateReservations(orders); return true; });
    public string DescribeReservation(ReservationOrder order, string language) => Call(h => Reservation(h).DescribeReservation(order, language));
    public ReservationPreview PreviewReservation(ReservationOrder order, string language) => Call(h => Reservation(h).PreviewReservation(order, language));
    public JObject ExecuteReservation(ReservationOrder order) => Call(h => JObject.Parse(Reservation(h).ExecuteReservation(order)));
    public string Id => registration.Definition.Id;
    public string Description => registration.Instructions;
    public AdvisorOperationText Documentation => registration.Text;
    public OperationExecutionMode ExecutionMode => registration.Definition.ExecutionMode;
    public bool RequiresApproval => registration.Definition.RequiresApproval;
    public AdvisorOperationPlanPolicy PlanPolicy => registration.Policy;
    public JObject Definition() => new() { ["id"] = Id, ["description"] = Description, ["parameters"] = registration.Parameters.DeepClone() };
    private T Translate<T>(Func<T> action)
    {
        context.Check();
        try { return action(); }
        catch (OperationException error) { throw new AdvisorOperationException(error.Status, error.Code); }
    }
    private T Call<T>(Func<Api.IAdvisorOperationHandler, T> action) => Translate(() =>
    {
        handler ??= registration.Extension.CreateSession(context) ?? throw new InvalidOperationException("The command session is missing: " + Id);
        return action(handler);
    });
    private static IAdvisorOperationActions Actions(Api.IAdvisorOperationHandler handler) => handler as IAdvisorOperationActions
        ?? throw new InvalidOperationException("A command outside reservation composition must implement its action contract.");
    private static IReadOnlyList<OperationOrder> Orders(IReadOnlyList<AdvisorOperationOrder> orders) =>
        Array.AsReadOnly(orders.Select(o => new OperationOrder(o.Target, o.Arguments.ToString(Formatting.None))).ToArray());
    public JArray Candidates() => Call(h => JArray.Parse(h.GetCandidates()));
    public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) => Call(h => { Actions(h).Validate(Orders(orders)); return true; });
    public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Call(h => Actions(h).DescribeProposal(Orders(orders), language));
    public JObject Execute(string target, JObject arguments) => Call(h => JObject.Parse(Actions(h).Execute(new OperationOrder(target, arguments.ToString(Formatting.None)))));
    public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Call(h =>
        h is Api.IAdvisorOperationPreview preview ? preview.Preview(Orders(orders), language) : AdvisorMapPreview.Empty);
    public JObject NormalizeArguments(string target, JObject arguments, AdvisorOperationReferences references, JArray resolutions) => Call(h =>
    {
        if (h is not Api.IAdvisorOperationArgumentNormalizer normalizer) return arguments;
        var normalized = normalizer.NormalizeArguments(new OperationOrder(target, arguments.ToString(Formatting.None)), new References(references));
        var result = JObject.Parse(normalized.ArgumentsJson);
        foreach (var correction in normalized.Resolutions)
            resolutions.Add(new JObject { ["argument"] = correction.Argument, ["requested"] = correction.Requested,
                ["resolved"] = correction.Resolved, ["reason"] = correction.Reason });
        return result;
    });
    public void Dispose()
    {
        context.CheckThread();
        if (disposed) return;
        disposed = true;
        var errors = new List<Exception>();
        try { handler?.Dispose(); } catch (Exception error) { errors.Add(error); }
        finally { handler = null; }
        try { context.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Command session could not be fully disposed.", errors);
    }
    private sealed class References : IAdvisorOperationReferences
    {
        private readonly AdvisorOperationReferences source;
        internal References(AdvisorOperationReferences source) => this.source = source;
        public string? ResolveUnit(string consultationId) => source.Units.Resolve(consultationId) is int id
            ? "unit:" + id.ToString(CultureInfo.InvariantCulture) : null;
        public OperationReferenceMatch MatchAmmunition(string requested, IReadOnlyList<AmmunitionIdentity> candidates)
        {
            var matches = source.Weapons.Match(requested, candidates.Select(c => (c.Id, c.Name)), out var reason);
            return new OperationReferenceMatch(matches, reason);
        }
    }
}
