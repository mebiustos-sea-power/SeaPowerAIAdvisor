using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class InputTests
{
    internal static void Run(string campaignPath, string gamePath, Action<string, Action> check)
    {
        check("Structured input preserves displayed names, source text and the draft question", () =>
        {
            var question = "ミッドウェイ空母戦闘群は？\n<>&\" {\"reply_language\":\"evil\"} 日本語𠮷";
            var input = Parse(AdvisorInputBuilder.Build(Snapshot(), "ja", question));
            Require((string?)input["question"] == question, "The question changed or became JSON properties");
            Require((string?)input["reply_language"] == "ja", "Question text overrode request metadata");
            var situation = input["situation"]!;
            Require((string?)situation["friendly_task_forces"]![0]!["display_name"] == "ミッドウェイ空母戦闘群", "The displayed fleet name was translated or replaced");
            Require((string?)situation["friendly_task_forces"]![0]!["ships"]![0]!["display_name"] == "護衛艦「しらね」", "The displayed ship name was lost");
            Require((string?)situation["recent_player_reports"]![0]!["text"] == "司令部：接触未確認。\n原文を保持 <>&\"", "Report source text changed");
            Require(input.Descendants().OfType<JProperty>().All(p => Regex.IsMatch(p.Name, @"\A[a-z][a-z0-9_]*\z")), "A JSON field name depends on a display language");
            Require((string?)situation["observed_contacts"]![0]!["kind"] == "surface" &&
                (string?)situation["published_orders"]![0]!["status"] == "active", "A machine status depends on display text");
        });
        check("Changing game-language codes affects only reply metadata", () =>
        {
            var reference = Parse(AdvisorInputBuilder.Build(Snapshot(), "ja", "質問"));
            reference.Remove("reply_language");
            foreach (var code in new[] { "en", "de", "fr", "ja", "zh-CN", "zh-TW", "pt_BR" })
            {
                var input = Parse(AdvisorInputBuilder.Build(Snapshot(), code, "質問"));
                Require((string?)input["reply_language"] == code.Replace('_', '-'), "Requested game language was lost");
                input.Remove("reply_language");
                Require(JToken.DeepEquals(reference, input), "A language change altered situation facts or names");
            }
            foreach (var bad in new string?[] { null, "", " ", "Japanese", "ja\n", "ja;en", "language_ja" })
                Throws<InvalidOperationException>(() => LanguageCode.Validate(bad));
        });
        check("JSON numbers and campaign UTC times ignore host culture and wrap longitude", () =>
        {
            var old = CultureInfo.CurrentCulture;
            var oldUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
                var input = Parse(AdvisorInputBuilder.Build(Snapshot(), "ja", null));
                var force = input["situation"]!["friendly_task_forces"]![0]!;
                Require(Math.Abs((double)force["position"]!["longitude_deg"]! + 157.95) < .00000001, "Longitude no longer denotes the same place");
                Require((double)force["ships"]![0]!["fuel"]!["remaining_fraction"]! == .85, "A localized number changed its value");
                Require((string?)input["situation"]!["captured_at_utc"] == "1985-06-01T12:00:00.0000000Z", "UTC campaign time changed");
                Require((string?)input["reply_language"] == "ja", "The host culture replaced the game language");
            }
            finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
        });
        check("Unobserved base numbers are null while observed zero is explicit", () =>
        {
            var input = Parse(AdvisorInputBuilder.Build(Snapshot(), "ja", ""));
            var bases = input["situation"]!["observed_non_player_bases"]!;
            Require((string?)bases[0]!["ground_defenses"]!["status"] == "unknown" &&
                bases[0]!["ground_defenses"]!["batteries"]!.Type == JTokenType.Null, "An unknown defense became a number");
            Require(bases[0]!["ships_and_aircraft"]!["ships_in_port"]!.Type == JTokenType.Null, "Unobserved forces leaked a retained count");
            Require((string?)bases[1]!["ships_and_aircraft"]!["status"] == "observed" &&
                (int)bases[1]!["ships_and_aircraft"]!["ships_in_port"]! == 0, "An observed zero became unknown");
            Require((string?)bases[1]!["ships_and_aircraft"]!["last_observed_at_utc"] == "1985-06-01T11:00:00.0000000Z", "Observation age was lost");
        });
        check("Assigned mission, convoy role, unarmed and nuclear states remain distinct", () =>
        {
            var input = Parse(AdvisorInputBuilder.Build(Snapshot(), "en", ""));
            var force = input["situation"]!["friendly_task_forces"]![0]!;
            Require((bool)force["is_convoy"]! && (string?)force["assigned_group_mission"]!["status"] == "not_assigned" &&
                force["assigned_group_mission"]!["kind"]!.Type == JTokenType.Null, "No assigned group mission erased convoy activity");
            var ships = force["ships"]!;
            Require((string?)ships[0]!["ammunition"]!["status"] == "known" &&
                (double)ships[0]!["ammunition"]!["remaining_fraction"]! == .75, "Armed readiness is incorrect");
            Require((bool)ships[1]!["is_merchant"]! && (string?)ships[1]!["type_code"] == "SS" &&
                (string?)ships[1]!["ammunition"]!["status"] == "unarmed" &&
                ships[1]!["ammunition"]!["remaining_fraction"]!.Type == JTokenType.Null, "Merchant SS was reduced to a submarine guess or zero ammunition");
            Require((string?)ships[2]!["fuel"]!["propulsion"] == "nuclear" &&
                ships[2]!["fuel"]!["remaining_fraction"]!.Type == JTokenType.Null, "Nuclear propulsion became an empty fuel tank");
        });
        check("Observed contacts carry age and position uncertainty without a source mover identity", () =>
        {
            var input = Parse(AdvisorInputBuilder.Build(Snapshot(), "en", ""));
            var contact = (JObject)input["situation"]!["observed_contacts"]![0]!;
            Require((string?)contact["id"] == "contact_1" && (int)contact["observed_count"]! == 6 &&
                (double)contact["observation_age_hours"]! == 1 && (double)contact["estimated_position_uncertainty_radius_nm"]! == 25, "Contact observation semantics changed");
            Require(contact["mover_id"] == null && contact["current_position"] == null && contact["target_id"] == null, "Hidden track identity or current location was introduced");
            Require((string?)input["situation"]!["coverage"]!["reference_scope"] == "current_snapshot", "Anonymous contact references lost their scope");
        });
        check("Input preview accepts an empty question and refuses oversized drafts without truncation", () =>
        {
            Require((string?)Parse(AdvisorInputBuilder.Build(Snapshot(), "en", null))["question"] == "", "A context-only preview failed");
            var question = new string('あ', AdvisorConversation.MaxQuestionLength);
            Require((string?)Parse(AdvisorInputBuilder.Build(Snapshot(), "en", question))["question"] == question, "A valid boundary question was truncated");
            Throws<ArgumentException>(() => AdvisorInputBuilder.Build(Snapshot(), "en", question + "あ"));
        });
        check("Unsupported snapshot classifications fail explicitly instead of changing meaning", () =>
        {
            var baseline = Snapshot();
            var bad = new SituationSnapshot(baseline.At, baseline.Side, baseline.Forces, new[] {
                new ObservedContact("new_unverified_kind", new MapPosition(1, 2), baseline.At, 1, false, 0)
            }, baseline.Bases, baseline.Orders, baseline.Reports, baseline.Victory, baseline.Aviation);
            Throws<InvalidOperationException>(() => AdvisorInputBuilder.Build(bad, "en", ""));
        });
        check("Display names come from the campaign UI projection including renames", () =>
        {
            var simulation = new object();
            var hull = new object();
            var fleet = new FakeFleet(simulation, hull);
            var names = new CampaignDisplayNames(new FakeUi(fleet), simulation);
            Require(names.ShipName(hull) == "表示名：しらね", "The UI's display name was replaced by raw state");
            fleet.DisplayName = "利用者の改名";
            Require(names.ShipName(hull) == "利用者の改名", "A changed display name was cached incorrectly");
            Throws<InvalidOperationException>(() => new CampaignDisplayNames(new FakeUi(fleet), new object()));
            Throws<MissingMemberException>(() => new CampaignDisplayNames(new object(), simulation));
            fleet.DisplayName = null;
            Throws<InvalidOperationException>(() => names.ShipName(hull));
        });
        check("Installed campaign and game expose the display and selected-language APIs used by capture", () =>
        {
            using var campaign = AssemblyDefinition.ReadAssembly(campaignPath);
            var ui = campaign.MainModule.GetType("DynamicCampaignMod.UI.CampaignUI");
            Require(ui.Fields.Any(f => f.Name == "_strategy" && f.FieldType.FullName == "DynamicCampaignMod.UI.Strategy.StrategyViewModel"), "Campaign strategy VM changed");
            var strategy = campaign.MainModule.GetType("DynamicCampaignMod.UI.Strategy.StrategyViewModel");
            Require(strategy.Properties.Any(p => p.Name == "Fleet" && p.GetMethod != null), "Campaign fleet VM access changed");
            var fleet = campaign.MainModule.GetType("DynamicCampaignMod.UI.Strategy.FleetViewModel");
            Require(fleet.Fields.Any(f => f.Name == "_sim") && fleet.Methods.Any(m => m.Name == "ShipRow" &&
                !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "DynamicCampaignMod.Core.State.Hull" &&
                m.ReturnType.FullName == "DynamicCampaignMod.UI.Strategy.FleetShipRow"), "Ship display projection changed");
            using var game = AssemblyDefinition.ReadAssembly(gamePath);
            var language = game.MainModule.GetType("SeaPower.LanguageResourceHandler");
            Require(language.Properties.Any(p => p.Name == "Language" && p.PropertyType.FullName == "System.String" && p.GetMethod != null), "Game language access changed");
            var singleton = game.MainModule.GetType("SeaPower.Singleton`1");
            Require(singleton.Methods.Any(m => m.Name == "InstanceExists" && m.IsStatic && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.Boolean") && singleton.Properties.Any(p => p.Name == "Instance" && p.GetMethod.IsStatic),
                "Read-only language singleton guard changed");
        });
    }

    private static JObject Parse(string json)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static readonly DateTime Now = new(1985, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static SituationSnapshot Snapshot() => new(Now, "blue", new[]
    {
        new FriendlyForce("ミッドウェイ空母戦闘群", new MapPosition(21.35, 202.05), 16.2, null, "None", true, new[]
        {
            new FriendlyShip("護衛艦「しらね」", "DDH", .85, .1, false, false, false, .75),
            new FriendlyShip("ランサー級", "SS", 1, 0, false, true, true, null),
            new FriendlyShip("原潜", "SSN", 0, 0, true, false, false, .5)
        })
    }, new[] { new ObservedContact("Surface", new MapPosition(31.5, 127.5), Now.AddHours(-1), 6, true, 25) },
    new[]
    {
        // Retained counts without an observation time must never acquire authority.
        new ObservedBase("敵港", "NavalBase", new MapPosition(35, 140), null, 999, 998, 997, null, 996, 995, 0),
        new ObservedBase("偵察済み港", "Port", new MapPosition(36, 141), Now.AddHours(-1), 0, 0, 0, Now.AddHours(-1), 0, 0, 1)
    }, new[] { new PublishedOrder("護衛命令", "船団を守れ", "Active", Now.AddHours(-2), Now.AddHours(10)) },
    new[] { new PublishedReport(Now.AddHours(-1), "Admiralty", "司令部：接触未確認。\n原文を保持 <>&\"") },
    new VictorySnapshot(new[] { new VictoryBase("味方基地", "NavalBase", new MapPosition(30, 130), "blue") },
        new[] { new VictorySide("blue", "味方", 1, new[] { 0 }, Array.Empty<ExcludedVictoryBase>()),
            new VictorySide("red", "相手", 0, new[] { 0 }, Array.Empty<ExcludedVictoryBase>()) }, null, null),
    new FriendlyAviationSnapshot(Array.Empty<FriendlyAirUnit>(), Array.Empty<FriendlyAirFlight>()));

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class FakeUi
    {
        public object _strategy;
        internal FakeUi(FakeFleet fleet) { _strategy = new FakeStrategy(fleet); }
    }
    private sealed class FakeStrategy
    {
        public FakeFleet Fleet { get; }
        internal FakeStrategy(FakeFleet fleet) { Fleet = fleet; }
    }
    private sealed class FakeFleet
    {
        public object _sim;
        private readonly object hull;
        public string? DisplayName = "表示名：しらね";
        internal FakeFleet(object simulation, object hull) { _sim = simulation; this.hull = hull; }
        private FakeRow ShipRow(object ownHull)
        {
            Require(ReferenceEquals(ownHull, hull), "Unapproved hull was passed to display projection");
            return new FakeRow { Name = DisplayName };
        }
    }
    private sealed class FakeRow { public string? Name { get; set; } }
}
