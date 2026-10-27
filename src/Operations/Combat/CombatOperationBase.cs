using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Shared only by attack and cease-fire: the host still owns approval, phases, lifetime and execution receipts.
internal abstract class CombatOperationBase : IAdvisorOperationHandler, IAdvisorOperationActions
{
    protected readonly IAdvisorOperationContext Context;
    protected IAdvisorCombatSupport Combat => Context.Game as IAdvisorCombatSupport
        ?? throw new InvalidOperationException("The game adapter does not provide combat support.");
    protected CombatOperationBase(IAdvisorOperationContext context) => Context = context;
    protected void CheckInformation(bool attack)
    {
        if (!Context.Game.IsInformationEnabled("battle_units") || attack &&
            (!Context.Game.IsInformationEnabled("battle_weapons") || !Context.Game.IsInformationEnabled("battle_contacts")))
            throw new OperationException(403, "information_disabled");
    }
    protected static JObject Identity(GameUnitInfo unit) => new()
    { ["target_ref"] = unit.Target, ["display_name"] = unit.Name, ["available"] = unit.OrderUnavailableReason == null,
        ["unavailable_reason"] = unit.OrderUnavailableReason };
    public void Dispose() { }
    public abstract string GetCandidates();
    public abstract void Validate(IReadOnlyList<OperationOrder> orders);
    public abstract string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language);
    public abstract string Execute(OperationOrder order);
}
