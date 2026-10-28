using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class CancelAttackExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.cancel_attack";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_cancel_attack");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate, "Cancel attacks, guidance and jamming for the specified unit; retain reservations.",
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}", AdvisorText.ReadResources(typeof(CancelAttackExtension).Assembly,
        "SeaPowerAIAdvisor.Commands.CancelAttack.Localization.", "en", "ja"), true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new CancelAttackOperation(context);
}

internal sealed class CancelAttackOperation : CombatOperationBase
{
    internal CancelAttackOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates() { CheckInformation(false); return new JArray(Combat.GetCombatUnits().Select(Identity)).ToString(Formatting.None); }
    private GameUnitInfo Read(OperationOrder order)
    {
        CheckInformation(false);
        if (JObject.Parse(order.ArgumentsJson).Count != 0) throw new OperationException(400, "invalid_cancel_attack_arguments");
        var unit = Combat.GetCombatUnits().SingleOrDefault(u => u.Target == order.Target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        return unit;
    }
    public override void Validate(IReadOnlyList<OperationOrder> orders) { foreach (var order in orders) Read(order); }
    public override string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language) =>
        string.Join("\n\n", orders.Select(order => Context.Text.Format(language, "proposal_summary", Read(order).Name))) + "\n\n" + Context.Text.Get(language, "execution_note");
    public override string Execute(OperationOrder order)
    {
        Read(order); Combat.CancelAttack(order.Target);
        return new JObject { ["target_ref"] = order.Target, ["cancel_attack_requested"] = true }.ToString(Formatting.None);
    }
}
