using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunAttackRangeOverview(Action<string, Action> check)
    {
        check("Attack range summaries survive each data delivery mode and preserve negative results without transmitting combinations", () =>
        {
            var fixture = new Fixture(); var snapshot = fixture.Capture(); var data = snapshot.Copy();
            var contacts = (JArray)data["observed_contacts"]!;
            foreach (var contact in contacts.OfType<JObject>()) contact["has_in_range_attack_option"] = false;
            foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var session = new BattleQuerySession(new BattleSnapshot(data, snapshot.UnitReferences), "en", "Choose attacks");
                var context = AdvisorContext.Create(session, Array.Empty<ChatExchange>());
                var input = context.PrepareStandardInput(mode);
                JToken supplied;
                if (mode == AdvisorSituationDelivery.OnDemand)
                {
                    var summary = input.Data["situation"]!["attack_range_overview"]!;
                    Require(summary.Count() == contacts.Count && summary.All(c => (bool?)c["has_in_range_attack_option"] == false && c["ammunition"] == null),
                        "Initial overview lost no-option contacts or sent full combinations");
                    supplied = session.Execute(Request(session, new JObject { ["query"] = "list_contacts" }))["results"]![0]!["data"]!["items"]!;
                }
                else
                {
                    var rows = mode == AdvisorSituationDelivery.Upfront ? input.Data["situation_data"]! : context.Reads.ExecuteAsync(new JObject {
                        ["requests"] = new JArray(new JObject { ["source"] = "situation", ["query"] = "get_situation" }) }, CancellationToken.None)
                        .GetAwaiter().GetResult()["results"]![0]!["data"]!["situation_data"]!;
                    supplied = new JArray(rows.Where(r => (string?)r["query"] == "list_contacts").SelectMany(r => r["data"]!["items"]!));
                }
                Require(supplied.Count() == contacts.Count && supplied.All(c => (bool?)c["has_in_range_attack_option"] == false),
                    "A delivery mode changed range summaries or removed out-of-range contacts");
            }
        });
        check("Range summaries respect disabled weapons and contacts and retain only permitted contact aliases", () =>
        {
            var snapshot = new Fixture().Capture(); var data = snapshot.Copy();
            foreach (var contact in ((JArray)data["observed_contacts"]!).OfType<JObject>()) contact["has_in_range_attack_option"] = true;
            foreach (var kind in new[] { InformationKind.BattleUnits, InformationKind.BattleWeapons, InformationKind.BattleContacts })
            {
                var session = new BattleQuerySession(new BattleSnapshot(data, snapshot.UnitReferences), "en", "Limited", information: AdvisorInformation.All.With(kind, false));
                var input = session.InitialInput();
                Require(input["situation"]!["attack_range_overview"] == null && !session.ExportSituation().ToString().Contains("has_in_range_attack_option", StringComparison.Ordinal),
                    "A derived range bit disclosed excluded unit or ammunition information");
                if (kind == InformationKind.BattleContacts)
                    Require(session.OperationReferences.Units.Resolve("observed_contact_1") == null, "Excluded contact aliases reached command queries");
            }
            var permitted = new BattleQuerySession(new BattleSnapshot(data, snapshot.UnitReferences), "en", "Allowed");
            Require(permitted.OperationReferences.Units.Resolve("observed_contact_1") == snapshot.UnitReferences.Resolve("observed_contact_1"),
                "Visible contact references could not be used for the detail query");
        });
    }
}
