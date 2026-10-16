using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SituationTests;

internal static class VictoryTests
{
    internal static void Run(Assembly assembly, Action<string, Action> check)
    {
        check("Victory objectives include own bases and islands, with side-specific homeland exemptions", () =>
        {
            var f = new Fixture(assembly);
            AddBase(f, "blue-home", "味方本土", "BLUE", "BLUE", true);
            AddBase(f, "red-home", "相手本土", "RED", "RED", true);
            AddBase(f, "island", "相手の島", "RED", "RED");
            var exempt = AddBase(f, "exempt", "名称変更済みの例外基地", "BLUE", "BLUE");
            Sequence(exempt, "notObjectiveFor").Add("RED");
            AddBase(f, "retake", "奪還すべき味方基地", "BLUE", "RED");
            var snapshot = new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName);
            var victory = Input(snapshot);
            var blue = Side(victory, "BLUE"); var red = Side(victory, "RED");
            Require((int)blue["progress"]!["total_count"]! == 6 && (int)blue["progress"]!["held_count"]! == 3 &&
                (int)blue["progress"]!["remaining_count"]! == 3, "Player objective progress is incorrect");
            Require((int)red["progress"]!["total_count"]! == 5 && (int)red["progress"]!["held_count"]! == 4 &&
                (int)red["progress"]!["remaining_count"]! == 1, "Opponent objective progress is incorrect");
            var bases = victory["bases"]!.ToDictionary(b => (string)b["display_name"]!, b => (string)b["id"]!);
            Require(Status(blue, bases["味方港"]) == "held" && Status(blue, bases["奪還すべき味方基地"]) == "not_held",
                "Own objectives lost defense or recapture requirements");
            Require(Status(blue, bases["相手の島"]) == "not_held", "An island was mistaken for opposing homeland");
            Require(Exemption(blue, bases["相手本土"]) == "opposing_homeland" &&
                Exemption(red, bases["名称変更済みの例外基地"]) == "side_specific_exemption", "Exemption reasons changed");
            Require((string?)blue["display_name"] == "味方陣営" && (string?)red["display_name"] == "相手陣営",
                "Displayed side names were replaced");
            Require(victory["sides"]!.SelectMany(s => s["objective_bases"]!).All(o =>
                bases.Values.Contains((string)o["base_id"]!)), "An objective reference has no public base");
            Require(victory["bases"]!.All(b => b["ground_defenses"] == null && b["ships"] == null && b["stores"] == null),
                "Objectives exposed undisclosed strength");
        });
        check("Public ownership changes update both sides without deciding a winner or mutating prior snapshots", () =>
        {
            var f = new Fixture(assembly);
            var reader = new CampaignSituationReader();
            var before = reader.Capture(f.Sim, f.DisplayShipName);
            var original = AdvisorInputBuilder.Build(before, "ja", "");
            var feedCount = Sequence(f.State, "Feed").Count;
            Dictionary(f.State, "BaseOwner")["enemy-port"] = "BLUE";
            var next = Input(reader.Capture(f.Sim, f.DisplayShipName));
            Require((bool)Side(next, "BLUE")["progress"]!["all_objectives_held"]! &&
                (int)Side(next, "RED")["progress"]!["held_count"]! == 0, "Public capture did not update both sides");
            Require((string?)next["outcome"]!["status"] == "undecided" && Get(f.State, "Victor") == null &&
                (DateTime)Get(f.State, "VictoryAt") == DateTime.MinValue && Sequence(f.State, "Feed").Count == feedCount,
                "The advisor ran a victory check or published a victory");
            Set(Sequence(f.Definition, "bases")[0]!, "name", "変更後の基地名");
            Set(Sequence(f.Definition, "sides")[0]!, "name", "変更後の陣営名");
            Require(AdvisorInputBuilder.Build(before, "ja", "") == original, "A captured victory retained mutable source objects");
        });
        check("Recorded victory remains final after territorial losses, with perspective and UTC time preserved", () =>
        {
            var f = new Fixture(assembly);
            Set(f.State, "Victor", "BLUE"); Set(f.State, "VictoryAt", f.Now.AddHours(-1));
            Dictionary(f.State, "BaseOwner")["own-port"] = "RED";
            var victory = Input(new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName));
            Require((string?)victory["outcome"]!["status"] == "player_victory" &&
                (string?)victory["outcome"]!["winner_side_id"] == "BLUE" &&
                (string?)victory["outcome"]!["decided_at_utc"] == "1985-06-01T11:00:00.0000000Z" &&
                !(bool)Side(victory, "BLUE")["progress"]!["all_objectives_held"]!, "The recorded winner was recalculated from current ownership");
            Set(f.State, "PlayerSideId", "RED");
            victory = Input(new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName));
            Require((string?)victory["outcome"]!["status"] == "player_defeat" &&
                (string?)victory["sides"]![0]!["side_id"] == "RED", "Winner perspective did not follow the player");
        });
        check("Zero objectives cannot imply victory and an unknown owner is not invented", () =>
        {
            var f = new Fixture(assembly);
            foreach (var b in Sequence(f.Definition, "bases").Cast<object>()) Sequence(b, "notObjectiveFor").Add("BLUE");
            Dictionary(f.State, "BaseOwner").Remove("own-port");
            var victory = Input(new CampaignSituationReader().Capture(f.Sim, f.DisplayShipName));
            var blue = Side(victory, "BLUE");
            Require((int)blue["progress"]!["total_count"]! == 0 && !(bool)blue["progress"]!["all_objectives_held"]!,
                "An empty objective list became a victory");
            var ownPort = victory["bases"]!.Single(b => (string?)b["display_name"] == "味方港");
            Require((string?)ownPort["ownership_status"] == "unknown" && ownPort["current_owner_side_id"]!.Type == JTokenType.Null,
                "Missing ownership became an invented side");
        });
        check("Victory base references remain local when internal campaign IDs change", () =>
        {
            var f = new Fixture(assembly);
            var original = Input(EmptySituation(f, CampaignVictoryReader.Capture(f.State))).ToString();
            foreach (var b in Sequence(f.Definition, "bases").Cast<object>())
            {
                var old = (string)Get(b, "id");
                var owners = Dictionary(f.State, "BaseOwner");
                var owner = owners[old]; owners.Remove(old);
                Set(b, "id", "REPLACED-" + old); owners.Add("REPLACED-" + old, owner);
            }
            Require(Input(EmptySituation(f, CampaignVictoryReader.Capture(f.State))).ToString() == original,
                "Internal base identifiers leaked into victory input or references");
        });
        check("Invalid recorded winners and victory dates surface as campaign errors", () =>
        {
            var f = new Fixture(assembly);
            Set(f.State, "Victor", "MISSING_SIDE");
            Throws(() => CampaignVictoryReader.Capture(f.State));
            Set(f.State, "Victor", "BLUE");
            Throws(() => CampaignVictoryReader.Capture(f.State));
            Set(f.State, "VictoryAt", f.Now.AddSeconds(1));
            Throws(() => CampaignVictoryReader.Capture(f.State));
        });
    }

    private static SituationSnapshot EmptySituation(Fixture f, VictorySnapshot victory) => new(f.Now, "BLUE",
        Array.Empty<FriendlyForce>(), Array.Empty<ObservedContact>(), Array.Empty<ObservedBase>(),
        Array.Empty<PublishedOrder>(), Array.Empty<PublishedReport>(), victory,
        new FriendlyAviationSnapshot(Array.Empty<FriendlyAirUnit>(), Array.Empty<FriendlyAirFlight>()));
    private static object AddBase(Fixture f, string id, string name, string side, string owner, bool homeland = false)
    {
        var b = f.New("Core.Data.BaseDefinition");
        Set(b, "id", id); Set(b, "name", name); Set(b, "side", side); Set(b, "homeland", homeland);
        SetEnum(b, "kind", "Port"); Set(b, "lat", 34d); Set(b, "lon", 135d);
        Sequence(f.Definition, "bases").Add(b); Dictionary(f.State, "BaseOwner").Add(id, owner);
        return b;
    }
    private static JToken Input(SituationSnapshot snapshot)
    {
        using var text = new StringReader(AdvisorInputBuilder.Build(snapshot, "ja", ""));
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        var input = JObject.Load(reader);
        Require(input["situation"]!["coverage"]!["included"]!.Values<string>().Contains("victory_conditions"),
            "Input coverage omits its public victory data");
        return input["situation"]!["victory_conditions"]!;
    }
    private static JToken Side(JToken victory, string id) => victory["sides"]!.Single(s => (string?)s["side_id"] == id);
    private static string? Status(JToken side, string id) =>
        (string?)side["objective_bases"]!.Single(b => (string?)b["base_id"] == id)["status"];
    private static string? Exemption(JToken side, string id) =>
        (string?)side["excluded_bases"]!.Single(b => (string?)b["base_id"] == id)["reason"];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid victory state was accepted");
    }
}
