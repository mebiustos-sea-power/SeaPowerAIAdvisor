using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SeaPowerAIAdvisor.InformationKind;

internal static partial class InformationTests
{
    internal static void Run(Action<string, Action> check)
    {
        RunPersistence(check);
        check("Sensor performance and neutral/destroyed contacts are opt-in while other information and dependent selections persist", () =>
        {
            var defaults = AdvisorOptions.Parse("{}").Information;
            Require(!defaults.Allows(BattleNeutralContacts) && !defaults.Allows(BattleDestroyedContacts) && !defaults.Allows(BattleSensorPerformance) &&
                AdvisorInformation.Items.Where(i => i.Kind is not BattleNeutralContacts and not BattleDestroyedContacts and not BattleSensorPerformance).All(i => defaults.Allows(i.Kind)),
                "Opt-in information was enabled by default or another category was disabled");
            var initial = AdvisorInformation.All;
            var disabled = initial.With(CampaignForces, false);
            Require(!disabled.Allows(CampaignShips) && !disabled.Allows(CampaignFuel) && disabled.Allows(CampaignAviation),
                "A dependent category stayed available or independent aviation was disabled");
            Require(disabled.With(CampaignForces, true).Allows(CampaignFuel) && initial.Allows(CampaignForces),
                "Parent toggle destroyed the child selection or mutated a consultation's policy");
            var group = AdvisorInformation.Groups.First(g => g.Items.Contains(BattleUnits));
            var partial = initial.With(BattleWeapons, false);
            Require(partial.State(group) == null && partial.With(group, false).State(group) == false &&
                partial.With(group, true).State(group) == true, "Parent checkbox states or batch selection are incorrect");
            var saved = AdvisorOptions.Parse(System.Text.Encoding.UTF8.GetString(new AdvisorOptions(7, true, disabled).Data()));
            Require(saved.Information.Key == disabled.Key && saved.MaxQueryRounds == 7 && saved.CampaignEnabled,
                "Saving selection changed another preference or lost dependencies");
            foreach (var invalid in new[] { "null", "{}", "[true]", "[\"unknown\"]", "[\"battle_units\",\"battle_units\"]" })
            {
                try { _ = AdvisorOptions.Parse("{\"disabled_information\":" + invalid + "}"); }
                catch (AdvisorOptionsException e) when (e.Code == "information_invalid") { continue; }
                throw new InvalidOperationException("Invalid information selection was silently accepted");
            }
        });
        check("A selection change keeps visible history and excludes old and late answers from AI context", () =>
        {
            var chat = new AdvisorConversation();
            chat.UseInformationPolicy("all");
            chat.TryAdd("Earlier question", "Earlier ammunition details", out _);
            var pending = chat.Begin(new AdvisorQuestion("Pending question"));
            chat.UseInformationPolicy("limited");
            pending.Complete("Late old-policy answer");
            Require(chat.Turns.Count == 2 && chat.Exchanges.Count == 0, "Late completion or older history crossed the policy boundary");
            chat.TryAdd("New question", "Permitted answer", out _);
            chat.UseInformationPolicy("limited");
            Require(chat.Exchanges.Single().Answer == "Permitted answer", "An unchanged selection discarded current context");
            chat.UseInformationPolicy("all");
            Require(chat.Exchanges.Count == 0 && chat.Turns.Count == 3, "Re-enabling data resurrected older AI context or erased visible history");
            var retry = chat.Begin(new AdvisorQuestion("Retry")); retry.Cancel();
            chat.UseInformationPolicy("limited"); chat.Retry(retry); retry.Complete("Fresh answer");
            Require(chat.Exchanges.Single().Answer == "Fresh answer", "A retry used the original context generation");
        });
    }
    internal static void RunCampaign(string campaignPath, Action<string, Action> check)
    {
        RunGroundCampaign(campaignPath, check);
        var snapshot = QueryTests.Capture(new SituationTests.Fixture(Assembly.LoadFrom(campaignPath)));
        check("Campaign exclusions apply to overview, catalog, tools and direct requests while objectives remain available", () =>
        {
            var policy = AdvisorInformation.All;
            foreach (var item in AdvisorInformation.Items) policy = policy.With(item.Kind, false);
            var source = new CampaignAdvisorSource(() => snapshot);
            var session = source.CreateSession("ja", "What now?", 20, policy);
            var initial = session.InitialInput();
            Require(!initial["situation"]!["overview"]!.HasValues && initial["situation"]!["victory_conditions"]!["bases"]!.HasValues &&
                ((JArray)initial["knowledge_catalog"]!).Count == 1 && initial["aviation_semantics"] == null,
                "Disabled counts, catalogs or aviation details leaked, or mandatory victory context was removed");
            var queries = initial["query_tool"]!["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!.Values<string>().ToArray();
            Require(queries.SequenceEqual(new[] { "catalog", "list_bases" }), "Tools still advertise an excluded category");
            foreach (var query in AdvisorQueryCatalog.Queries.Where(q => q != "catalog" && q != "list_bases"))
                Require((string?)Call(session, new JObject { ["query"] = query, ["ids"] = new JArray("own_force_1") })["error"]?["code"] == "information_disabled",
                    "A direct request bypassed the selection: " + query);
            var bases = Call(session, new JObject { ["query"] = "list_bases" })["results"]![0]!["data"]!["items"]!;
            Require(bases.HasValues && bases.All(b => b["observation_status"] == null), "Mandatory bases disclosed optional observation availability");
        });
        check("Campaign aviation remains usable without fleet disclosures and cannot filter on excluded fleets", () =>
        {
            var wing = new FriendlyAirUnit("carrier_air_wing", "Known air wing", null, 0, 0, false,
                false, null, null, 4, new[] { new FriendlyAircraftType("Known aircraft", true,
                    new System.Collections.Generic.Dictionary<string, int?> { ["aboard"] = 4 }, Array.Empty<string>()) });
            var aviation = new FriendlyAviationSnapshot(new[] { wing }, Array.Empty<FriendlyAirFlight>());
            var withAir = new SituationSnapshot(snapshot.At, snapshot.Side, snapshot.Forces, snapshot.Contacts,
                snapshot.Bases, snapshot.Orders, snapshot.Reports, snapshot.Victory, aviation);
            var policy = AdvisorInformation.All.With(CampaignForces, false);
            var session = new AdvisorQuerySession(withAir, "ja", "Air status", information: policy);
            var listing = Data(Call(session, new JObject { ["query"] = "list_air_units" }))["items"]![0]!;
            var details = Data(Call(session, new JObject { ["query"] = "get_air_units", ["ids"] = new JArray(listing["id"]!.DeepClone()) }))[0]!;
            Require(details["aircraft"]!.HasValues && listing["force_id"] == null && listing["ship_id"] == null &&
                details["force_id"] == null && details["ship_id"] == null, "Independent aviation was disabled or linked excluded fleet identities");
            var filtered = Call(session, new JObject { ["query"] = "list_air_units", ["force_ids"] = new JArray("own_force_1") });
            Require((string?)filtered["error"]?["code"] == "information_disabled", "An aviation filter exposed excluded fleet membership");
        });
        check("Campaign status exclusions hold for both nested fleet details and direct ship details", () =>
        {
            var policy = AdvisorInformation.All.With(CampaignFuel, false).With(CampaignDamage, false);
            var session = new AdvisorQuerySession(snapshot, "ja", "Compare ships", information: policy);
            var fleet = Data(Call(session, new JObject { ["query"] = "get_forces", ["ids"] = new JArray("own_force_1"), ["include_ships"] = true }));
            foreach (var ship in fleet[0]!["ships"]!)
                Require(ship["fuel"] == null && ship["damage_fraction"] == null && ship["ammunition"] != null,
                    "An excluded status leaked through an embedded ship");
            var shipId = fleet[0]!["ships"]![0]!["id"]!.DeepClone();
            var direct = Data(Call(session, new JObject { ["query"] = "get_ships", ["ids"] = new JArray(shipId) }))[0]!;
            Require(direct["fuel"] == null && direct["damage_fraction"] == null && direct["ammunition"] != null, "Direct ship lookup ignored the selected sections");
            var noShips = new AdvisorQuerySession(snapshot, "ja", "Fleet only", information: policy.With(CampaignShips, false));
            Require((string?)Call(noShips, new JObject { ["query"] = "get_forces", ["ids"] = new JArray("own_force_1"), ["include_ships"] = true })["error"]?["code"] == "information_disabled",
                "include_ships bypassed disabled ship information");
            var fleetOnly = Data(Call(noShips, new JObject { ["query"] = "get_forces", ["ids"] = new JArray("own_force_1") }))[0]!;
            var list = Data(Call(noShips, new JObject { ["query"] = "list_forces" }))["items"]![0]!;
            Require(fleetOnly["ships"] == null && fleetOnly["ship_count"] == null && list["ship_count"] == null,
                "A fleet route leaked disabled ship contents or counts");
        });
    }
    private static JObject Call(IAdvisorQuerySession session, JObject query) => session.Execute(new JObject
        { ["consultation_id"] = session.Id, ["requests"] = new JArray(query) });
    private static JToken Data(JObject result) => result["results"]![0]!["data"]!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
