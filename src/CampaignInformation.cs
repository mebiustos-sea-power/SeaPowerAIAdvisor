using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Project every nested route as well as direct lookups, so alternate queries cannot bypass the selection.
internal static class CampaignInformation
{
    internal static JObject Ship(JObject ship, AdvisorInformation information)
    {
        if (!information.Allows(InformationKind.CampaignFuel)) ship.Remove("fuel");
        if (!information.Allows(InformationKind.CampaignAmmunition)) ship.Remove("ammunition");
        if (!information.Allows(InformationKind.CampaignDamage)) ship.Remove("damage_fraction");
        return ship;
    }
    internal static JObject Force(JObject force, AdvisorInformation information)
    {
        if (!information.Allows(InformationKind.CampaignShips)) { force.Remove("ships"); force.Remove("ship_count"); }
        else if (force["ships"] is JArray ships)
            foreach (var ship in ships) Ship((JObject)ship, information);
        return force;
    }
    internal static JObject PublicBase(JObject row, AdvisorInformation information)
    {
        if (!information.Allows(InformationKind.CampaignBases)) row.Remove("observation_status");
        return row;
    }
    internal static JObject Aviation(JObject row, AdvisorInformation information)
    {
        if (!information.Allows(InformationKind.CampaignForces)) row.Remove("force_id");
        if (!information.Allows(InformationKind.CampaignShips)) row.Remove("ship_id");
        return row;
    }
}
