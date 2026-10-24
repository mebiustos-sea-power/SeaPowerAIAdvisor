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
    IAdvisorOperationPreview, IAdvisorOperationArgumentNormalizer, IDisposable
{
    private readonly AdvisorOperationRegistration registration;
    private readonly AdvisorOperationContext context;
    private Api.IAdvisorOperationHandler? handler;
    private bool disposed;
    internal AdvisorRegisteredOperation(AdvisorOperationRegistration registration, IAdvisorGameSupport game, IAdvisorMap map)
    { this.registration = registration; context = new AdvisorOperationContext(game, map, registration.Text); }
    public string Id => registration.Definition.Id;
    public string Description => registration.Instructions;
    public AdvisorOperationText Documentation => registration.Text;
    public bool RequiresApproval => registration.Definition.RequiresApproval;
    public AdvisorOperationPlanPolicy PlanPolicy => registration.Policy;
    public JObject Definition() => new() { ["id"] = Id, ["description"] = Description, ["parameters"] = registration.Parameters.DeepClone() };
    private T Call<T>(Func<Api.IAdvisorOperationHandler, T> action)
    {
        context.Check();
        try
        {
            handler ??= registration.Extension.CreateSession(context) ?? throw new InvalidOperationException("The command session is missing: " + Id);
            return action(handler);
        }
        catch (OperationException error) { throw new AdvisorOperationException(error.Status, error.Code); }
    }
    private static IReadOnlyList<OperationOrder> Orders(IReadOnlyList<AdvisorOperationOrder> orders) =>
        Array.AsReadOnly(orders.Select(o => new OperationOrder(o.Target, o.Arguments.ToString(Formatting.None))).ToArray());
    public JArray Candidates() => Call(h => JArray.Parse(h.GetCandidates()));
    public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) => Call(h => { h.Validate(Orders(orders)); return true; });
    public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Call(h => h.DescribeProposal(Orders(orders), language));
    public JObject Execute(string target, JObject arguments) => Call(h => JObject.Parse(h.Execute(new OperationOrder(target, arguments.ToString(Formatting.None)))));
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
        try { handler?.Dispose(); }
        finally { handler = null; context.Dispose(); }
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
