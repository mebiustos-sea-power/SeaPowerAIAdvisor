using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class CancelReservationsExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.cancel_reservations";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_cancel_reservations");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate, "Cancel all waypoint reservations for the specified unit; retain active combat.",
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}", AdvisorText.ReadResources(typeof(CancelReservationsExtension).Assembly,
        "SeaPowerAIAdvisor.Commands.CancelReservations.Localization.", "en", "ja"), true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new CancelReservationsOperation(context);
}

internal sealed class CancelReservationsOperation : IAdvisorOperationHandler, IAdvisorOperationActions
{
    private readonly IAdvisorOperationContext context;
    private IAdvisorReservationSupport Reservations => context.Game as IAdvisorReservationSupport
        ?? throw new InvalidOperationException("The game adapter does not provide reservation control.");
    internal CancelReservationsOperation(IAdvisorOperationContext context) => this.context = context;
    private IReadOnlyList<GameUnitInfo> Units()
    {
        if (!context.Game.IsInformationEnabled("battle_units")) throw new OperationException(403, "information_disabled");
        return Reservations.GetReservationUnits();
    }
    public string GetCandidates() => new JArray(Units().Select(unit => new JObject
    {
        ["target_ref"] = unit.Target, ["display_name"] = unit.Name,
        ["available"] = unit.OrderUnavailableReason == null, ["unavailable_reason"] = unit.OrderUnavailableReason
    })).ToString(Formatting.None);
    private GameUnitInfo Read(OperationOrder order)
    {
        if (JObject.Parse(order.ArgumentsJson).Count != 0) throw new OperationException(400, "invalid_cancel_reservations_arguments");
        var unit = Units().SingleOrDefault(u => u.Target == order.Target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        return unit;
    }
    public void Validate(IReadOnlyList<OperationOrder> orders) { foreach (var order in orders) Read(order); }
    public OperationPresentation PreparePresentation(IReadOnlyList<OperationOrder> orders)
    {
        var units = orders.Select(Read).ToArray();
        return new OperationPresentation(language => new OperationReview(string.Join("\n\n", units.Select(unit => context.Text.Format(language, "proposal_summary", unit.Name))),
            new[] { context.Text.Format(language, "execution_note", string.Join(", ", units.Select(unit => unit.Name))) }));
    }
    public string Execute(OperationOrder order)
    {
        Read(order); Reservations.ClearReservations(order.Target);
        return new JObject { ["target_ref"] = order.Target, ["reservations_cleared"] = true }.ToString(Formatting.None);
    }
    public void Dispose() { }
}
