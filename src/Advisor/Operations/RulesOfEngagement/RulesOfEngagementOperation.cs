using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class RulesOfEngagementExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.set_rules_of_engagement";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_rules_of_engagement");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate,
        "Set Weapons Free, Tight or Hold for the named unit without cancelling attacks or reservations.",
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"free\",\"tight\",\"hold\"]}},\"required\":[\"mode\"]}",
        AdvisorText.ReadResources(typeof(RulesOfEngagementExtension).Assembly,
            "SeaPowerAIAdvisor.Commands.RulesOfEngagement.Localization.", "en", "ja"), true,
        new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new RulesOfEngagementOperation(context);
}

internal sealed class RulesOfEngagementOperation : IAdvisorOperationHandler, IAdvisorOperationActions
{
    private readonly IAdvisorOperationContext context;
    private IAdvisorRulesOfEngagementSupport Support => context.Game as IAdvisorRulesOfEngagementSupport
        ?? throw new InvalidOperationException("The game adapter does not provide rules-of-engagement support.");
    internal RulesOfEngagementOperation(IAdvisorOperationContext context) => this.context = context;
    private IReadOnlyList<RulesOfEngagementUnitInfo> Units()
    {
        if (!context.Game.IsInformationEnabled("battle_units")) throw new OperationException(403, "information_disabled");
        return Support.GetRulesOfEngagementUnits();
    }
    private static string Code(RulesOfEngagement mode) => mode switch
    {
        RulesOfEngagement.Free => "free", RulesOfEngagement.Tight => "tight", RulesOfEngagement.Hold => "hold",
        _ => throw new InvalidOperationException("The game adapter returned unsupported rules of engagement.")
    };
    public string GetCandidates() => new JArray(Units().Select(row => new JObject
    {
        ["target_ref"] = row.Unit.Target, ["display_name"] = row.Unit.Name, ["mode"] = Code(row.Mode),
        ["available"] = row.Unit.OrderUnavailableReason == null, ["unavailable_reason"] = row.Unit.OrderUnavailableReason
    })).ToString(Formatting.None);
    private (RulesOfEngagementUnitInfo Unit, RulesOfEngagement Mode) Read(OperationOrder order)
    {
        var args = JObject.Parse(order.ArgumentsJson);
        if (args.Count != 1 || args["mode"]?.Type != JTokenType.String) throw new OperationException(400, "invalid_rules_of_engagement_arguments");
        var mode = (string?)args["mode"] switch
        {
            "free" => RulesOfEngagement.Free, "tight" => RulesOfEngagement.Tight, "hold" => RulesOfEngagement.Hold,
            _ => throw new OperationException(400, "invalid_rules_of_engagement_arguments")
        };
        var unit = Units().SingleOrDefault(u => u.Unit.Target == order.Target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        return (unit, mode);
    }
    public void Validate(IReadOnlyList<OperationOrder> orders) { foreach (var order in orders) Read(order); }
    public OperationReview DescribeProposal(IReadOnlyList<OperationOrder> orders, string language)
    {
        var items = orders.Select(Read).ToArray();
        var description = string.Join("\n", items.Select(item => context.Text.Format(language, "proposal_summary", item.Unit.Unit.Name,
            context.Text.Get(language, "mode_" + Code(item.Unit.Mode)), context.Text.Get(language, "mode_" + Code(item.Mode)))));
        var holdUnits = items.Where(item => item.Mode == RulesOfEngagement.Hold).Select(item => item.Unit.Unit.Name).ToArray();
        return new OperationReview(description, holdUnits.Length == 0 ? Array.Empty<string>() :
            new[] { context.Text.Format(language, "execution_note", string.Join(", ", holdUnits)) });
    }
    public string Execute(OperationOrder order)
    {
        var item = Read(order);
        Support.SetRulesOfEngagement(order.Target, item.Mode);
        return new JObject { ["target_ref"] = order.Target, ["rules_of_engagement_requested"] = Code(item.Mode) }.ToString(Formatting.None);
    }
    public void Dispose() { }
}
