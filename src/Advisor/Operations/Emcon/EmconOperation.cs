using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class EmconExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.apply_emcon";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_emcon");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate, "Apply the game's EMCON shutdown.",
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}",
        AdvisorText.ReadResources(typeof(EmconExtension).Assembly, "SeaPowerAIAdvisor.Commands.Emcon.Localization.", "en", "ja"), true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new EmconOperation(context);
}

internal sealed class EmconOperation : SensorOperationBase
{
    internal EmconOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates() => new JArray(Units().Select(Identity)).ToString(Formatting.None);
    private SensorUnitInfo Read(OperationOrder order)
    {
        if (JObject.Parse(order.ArgumentsJson).Count != 0) throw new OperationException(400, "invalid_emcon_arguments");
        return Unit(order.Target);
    }
    public override void Validate(IReadOnlyList<OperationOrder> orders)
    { foreach (var order in orders) Read(order); }
    public override string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language) =>
        string.Join("\n", orders.Select(order => Context.Text.Format(language, "proposal_summary", Read(order).Unit.Name)));
    public override string Execute(OperationOrder order)
    {
        Read(order); Sensors.ApplyEmcon(order.Target);
        return new JObject { ["target_ref"] = order.Target, ["shutdown_requested"] = true }.ToString(Formatting.None);
    }
}
