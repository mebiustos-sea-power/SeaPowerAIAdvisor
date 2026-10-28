using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Copies the mission circles currently displayed by the game, without following their triggers or anchors.
internal static class BattleMissionAreaReader
{
    // SceneCreator converts mission AreaRadiusNM values into this internal distance unit.
    private const double UnitsPerNauticalMile = 27.5595;

    internal static JArray Capture(object areas)
    {
        var result = new JArray();
        foreach (var area in Rows(areas))
        {
            if (!(bool)Required(area, "Visible")) continue;
            var position = Required(area, "Position");
            result.Add(new JObject
            {
                ["display_text"] = (string?)Member(area, "Label"),
                ["shape"] = "circle",
                ["center"] = AdvisorInputBuilder.Position(new MapPosition(
                    Number(Required(position, "Latitude")), Number(Required(position, "Longitude")))),
                ["radius_nm"] = Nonnegative(Required(area, "Radius")) / UnitsPerNauticalMile
            });
        }
        return result;
    }
}
