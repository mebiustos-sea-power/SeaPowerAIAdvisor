using System;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Project through the same UI method used by the campaign, including active display localization patches.
internal sealed class CampaignDisplayNames
{
    private readonly object fleet;
    private readonly MethodInfo shipRow;
    private readonly PropertyInfo rowName;

    internal CampaignDisplayNames(object campaignUi, object simulation)
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var strategy = campaignUi.GetType().GetField("_strategy", instance)?.GetValue(campaignUi)
            ?? throw new MissingMemberException(campaignUi.GetType().FullName, "_strategy");
        fleet = strategy.GetType().GetProperty("Fleet", instance)?.GetValue(strategy)
            ?? throw new MissingMemberException(strategy.GetType().FullName, "Fleet");
        var fleetSim = fleet.GetType().GetField("_sim", instance)?.GetValue(fleet)
            ?? throw new MissingMemberException(fleet.GetType().FullName, "_sim");
        if (!ReferenceEquals(fleetSim, simulation))
            throw new InvalidOperationException("The display names belong to another campaign session.");
        shipRow = fleet.GetType().GetMethod("ShipRow", instance)
            ?? throw new MissingMethodException(fleet.GetType().FullName, "ShipRow");
        rowName = shipRow.ReturnType.GetProperty("Name", instance)
            ?? throw new MissingMemberException(shipRow.ReturnType.FullName, "Name");
    }

    internal string ShipName(object ownHull)
    {
        // ShipRow creates a detached UI row; it does not add it to the campaign's displayed collections.
        var row = shipRow.Invoke(fleet, new[] { ownHull })
            ?? throw new InvalidOperationException("The campaign did not create an own-ship display row.");
        return rowName.GetValue(row) as string
            ?? throw new InvalidOperationException("An own ship has no campaign display name.");
    }
}
