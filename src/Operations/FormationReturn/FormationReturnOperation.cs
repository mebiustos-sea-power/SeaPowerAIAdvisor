using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class FormationReturnExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorReservationExtension, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.return_to_formation";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public bool EndsReservationSequence => true;
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_formation_return");
    public OperationDefinition GetDefinition() => new(OperationId, "Return to formation after preceding reservations.",
        new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject() }.ToString(Formatting.None),
        AdvisorText.ReadResources(typeof(FormationReturnExtension).Assembly, "SeaPowerAIAdvisor.Commands.FormationReturn.Localization.", "en", "ja"),
        true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new FormationReturnOperation(context);
}

internal sealed class FormationReturnOperation : MovementOperationBase
{
    internal FormationReturnOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates() => new JArray(Units().Where(u => !u.IsFormationLeader && u.LeaderTarget != null).Select(unit => new JObject
    {
        ["target_ref"] = unit.Unit.Target, ["display_name"] = unit.Unit.Name, ["available"] = unit.Unit.OrderUnavailableReason == null,
        ["unavailable_reason"] = unit.Unit.OrderUnavailableReason, ["formation_leader"] = unit.LeaderTarget,
        ["formation_leader_name"] = unit.LeaderName, ["following_formation"] = unit.IsFollowingFormation,
        ["already_following"] = unit.IsFollowingFormation
    })).ToString(Formatting.None);
    private MovementUnitInfo Read(ReservationOrder order)
    {
        if (JObject.Parse(order.Order.ArgumentsJson).Count != 0) throw new OperationException(400, "invalid_formation_return_arguments");
        var unit = Unit(order.Order.Target);
        if (unit.IsFormationLeader || unit.LeaderTarget == null) throw new OperationException(409, "formation_return_unavailable");
        if (unit.IsFollowingFormation && order.Index == 0) throw new OperationException(409, "already_following_formation");
        return unit;
    }
    public override void ValidateReservations(IReadOnlyList<ReservationOrder> orders)
    { foreach (var order in orders) Read(order); }
    public override string DescribeReservation(ReservationOrder order, string language)
    {
        var unit = Read(order);
        return Context.Text.Format(language, "proposal_summary", unit.Unit.Name, unit.LeaderName ?? unit.LeaderTarget!) +
            QueueDescription(order, language) + "\n" + Context.Text.Get(language, "proposal_timing");
    }
    public override ReservationPreview PreviewReservation(ReservationOrder order, string language)
    {
        var unit = Read(order);
        var drawings = order.StartPosition is AdvisorMapCoordinate start
            ? new[] { AdvisorMapDrawing.Point(start, Context.Text.Format(language, "preview_return", unit.Unit.Name)) } : new AdvisorMapDrawing[0];
        return new ReservationPreview(new AdvisorMapPreview(drawings), null);
    }
    public override string ExecuteReservation(ReservationOrder order)
    {
        var unit = Read(order); Movement.AppendFormationReturn(order.Order.Target);
        return new JObject { ["target_ref"] = order.Order.Target, ["formation_leader"] = unit.LeaderTarget, ["queued"] = true }.ToString(Formatting.None);
    }
}
