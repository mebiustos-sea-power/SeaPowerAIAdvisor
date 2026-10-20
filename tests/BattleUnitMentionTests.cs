using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunUnitMentions(Action<string, Action> check)
    {
        check("Inserted unit references bind distinct same-name units, contacts and launched weapons to the current snapshot IDs", () =>
        {
            var f = new Fixture();
            f.Track.Id = 7018;
            f.OwnWeapons.Add(new OwnWeapon("Missile"));
            var captured = f.Capture();
            var data = captured.Copy();
            var first = (JObject)data["friendly_units"]![0]!;
            var second = (JObject)first.DeepClone(); second["id"] = "own_unit_9";
            ((JArray)data["friendly_units"]!).Add(second);
            var question = "Compare " + AdvisorUnitMentions.Format(1001, (string)first["display_name"]!) +
                " and " + AdvisorUnitMentions.Format(2002, (string)second["display_name"]!) +
                ", " + AdvisorUnitMentions.Format(7018, "Contact 7018") +
                ", " + AdvisorUnitMentions.Format(1002, "Missile") + " and again (unit_id: 1001).";
            var references = new AdvisorUnitReferences(new[] { ("own_unit_9", 1001), ("own_unit_1", 2002),
                ("observed_contact_1", 7018), ("own_weapon_1", 1002) });
            var snapshot = new BattleSnapshot(data, references);
            var session = new BattleQuerySession(snapshot, "ja", question);
            var input = session.InitialInput();
            var mentions = (JArray)input["referenced_units"]!;
            Require(mentions.Select(m => (int)m["unit_id"]!).SequenceEqual(new[] { 1001, 2002, 7018, 1002 }) &&
                mentions.Select(m => (string)m["id"]!).SequenceEqual(new[] { "own_unit_9", "own_unit_1", "observed_contact_1", "own_weapon_1" }),
                "A repeated mention was duplicated, same-name units were conflated, or an old consultation ID was reused");
            Require((string?)input["question"] == question && session.UnitReferences.Resolve("observed_contact_1") == null,
                "The draft was rewritten or contact bindings changed own-unit answer links");
            Require(captured.UnitReferences.Resolve("observed_contact_1") == 7018 && captured.UnitReferences.Resolve("own_weapon_1") == 1002,
                "The reader did not retain displayed contact and weapon identities");
            input["referenced_units"]![0]!["id"] = "mutated";
            Require((string?)session.InitialInput()["referenced_units"]![0]!["id"] == "own_unit_9", "The input retained a mutable binding");
        });
        check("Inserted references cannot bypass information selection or retarget a removed or unknown unit", () =>
        {
            var f = new Fixture(); f.Track.Id = 7018;
            f.OwnWeapons.Add(new OwnWeapon("Missile"));
            var snapshot = f.Capture();
            const string question = "(unit_id: 1001) (unit_id: 7018) (unit_id: 1002) (unit_id: 9999) (unit_id: 99999999999999999999)";
            var policy = AdvisorInformation.Default.With(InformationKind.BattleUnits, false)
                .With(InformationKind.BattleContacts, false).With(InformationKind.BattleLaunchedWeapons, false);
            var hidden = new BattleQuerySession(snapshot, "en", question, information: policy).InitialInput();
            var mentions = (JArray)hidden["referenced_units"]!;
            Require(mentions.Count == 4 && mentions.All(m => (string?)m["status"] == "unavailable" && m["id"] == null && m["display_name"] == null),
                "An excluded or unknown unit exposed its current ID or name, or an overflowing ID was accepted");
            var data = snapshot.Copy(); ((JArray)data["friendly_units"]!).Clear();
            var removed = new BattleQuerySession(new BattleSnapshot(data, snapshot.UnitReferences), "en", "(unit_id: 1001)").InitialInput();
            Require((string?)removed["referenced_units"]![0]!["status"] == "unavailable", "A removed target was guessed by name or position");
            Require(new BattleQuerySession(snapshot, "en", "Ordinary question").InitialInput()["referenced_units"] == null,
                "Ordinary questions acquired unnecessary unit data");
        });
    }
}
