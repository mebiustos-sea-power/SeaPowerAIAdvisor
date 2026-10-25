using System;
using System.Collections.Generic;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor.ExtensionFixture;

// This test client compiles against the API assembly alone, including its optional command contracts.
public sealed class CommandExtension : IAdvisorOperationExtension, IAdvisorExtensionVersion
{
    public int ApiVersion { get; set; } = AdvisorApi.ContractVersion;
    public string? Version { get; set; }
    public OperationDefinition Definition { get; set; } = new("fixture.command", "Fixture command.", "{\"type\":\"object\"}",
        new Dictionary<string, string> { ["en"] = "{\"name\":\"Fixture\",\"summary\":\"Summary\",\"requirements\":\"Conditions\"}" });
    public Func<IAdvisorOperationContext, IAdvisorOperationHandler> Open { get; set; } = _ => new CommandHandler();
    public OperationDefinition GetDefinition() => Definition;
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => Open(context);
}

public sealed class CommandHandler : IAdvisorOperationHandler, IAdvisorOperationActions
{
    public Func<string> Candidates { get; set; } = () => "[{\"target_ref\":\"fixture:target\",\"display_name\":\"Target\"}]";
    public Action<IReadOnlyList<OperationOrder>> Check { get; set; } = _ => { };
    public Func<IReadOnlyList<OperationOrder>, string, string> Describe { get; set; } = (_, _) => "Fixture proposal";
    public Func<OperationOrder, string> Run { get; set; } = _ => "{}";
    public Action Close { get; set; } = () => { };
    public string GetCandidates() => Candidates();
    public void Validate(IReadOnlyList<OperationOrder> orders) => Check(orders);
    public string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language) => Describe(orders, language);
    public string Execute(OperationOrder order) => Run(order);
    public void Dispose() => Close();
}
