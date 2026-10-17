using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OptionsTests
{
    internal static void Run(string campaignPath, Action<string, Action> check)
    {
        check("Missing behavior settings opt out of campaign AI and malformed values remain visible errors", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "options.json");
            var store = new AdvisorOptionsStore(path);
            Require(!store.Current.CampaignEnabled && store.Current.MaxQueryRounds == 5 && store.Current.TextSize == AdvisorTextSize.Small && store.Current.HistoryExchanges == 3 && !File.Exists(path),
                "A new or existing installation implicitly enabled campaign alpha support or wrote settings on read");
            Require(!AdvisorOptions.Parse("{}").CampaignEnabled && AdvisorOptions.Parse("{}").MaxQueryRounds == 5 && AdvisorOptions.Parse("{}").TextSize == AdvisorTextSize.Small && AdvisorOptions.Parse("{}").HistoryExchanges == 3,
                "Absent fields did not use compatible defaults");
            foreach (var invalid in new[] { "{", "{\"campaign_advisor_enabled\":\"true\"}", "{\"unknown\":1}",
                "{\"max_query_rounds\":5,\"max_query_rounds\":7}", "{\"max_query_rounds\":0}", "{\"max_query_rounds\":21}", "{\"max_query_rounds\":1.5}", "{\"conversation_text_size\":\"unknown\"}",
                "{\"conversation_text_size\":17}", "{\"conversation_text_size\":null}", "{\"conversation_text_size\":true}",
                "{\"history_exchanges\":-1}", "{\"history_exchanges\":21}", "{\"history_exchanges\":1.5}", "{\"history_exchanges\":\"3\"}",
                "{\"history_exchanges\":null}", "{\"history_exchanges\":true}", "{\"history_exchanges\":99999999999999999999999}",
                "{\"history_exchanges\":1,\"history_exchanges\":2}" })
            {
                File.WriteAllText(path, invalid);
                try { _ = store.Read(); }
                catch (AdvisorOptionsException)
                {
                    Require(store.Current.MaxQueryRounds == 5 && !store.Current.CampaignEnabled && store.Current.TextSize == AdvisorTextSize.Small && store.Current.HistoryExchanges == 3 && File.ReadAllText(path) == invalid,
                        "Invalid settings were silently replaced or applied");
                    continue;
                }
                throw new InvalidOperationException("Invalid options were accepted");
            }
            File.Delete(path); Directory.CreateDirectory(path);
            try { _ = store.Read(); }
            catch (AdvisorOptionsException error) when (error.Code == "options_unreadable") { return; }
            throw new InvalidOperationException("A directory at the settings path was treated as missing settings");
        }));
        check("User names initialize once in the initial UI language and retain edits and explicit blanks across categories and restarts", () => InTemp(directory =>
        {
            foreach (var language in new[] { "ja", "en" })
            foreach (var legacy in new[] { false, true })
            {
                var path = Path.Combine(directory, language + "-" + legacy + ".json");
                var store = new AdvisorOptionsStore(path);
                if (legacy) store.Write(new AdvisorOptions(7, true, commonPrompt: "Keep this", textSize: AdvisorTextSize.Large, historyExchanges: 0));
                var defaults = AdvisorText.Load(language)["general.default_user_name"];
                store.EnsureUserName(defaults);
                Require(store.Current.UserName == (language == "ja" ? "司令官" : "Commander") &&
                    (string?)JObject.Parse(File.ReadAllText(path))["user_name"] == defaults,
                    "The initial localized name was not stored as an actual setting");
                if (legacy) Require(store.Current.MaxQueryRounds == 7 && store.Current.CampaignEnabled && store.Current.CommonPrompt == "Keep this" &&
                    store.Current.TextSize == AdvisorTextSize.Large && store.Current.HistoryExchanges == 0, "Name initialization reset existing settings");
                var before = File.ReadAllBytes(path);
                var reopened = new AdvisorOptionsStore(path);
                reopened.EnsureUserName(language == "ja" ? "Commander" : "司令官");
                Require(reopened.Current.UserName == defaults && File.ReadAllBytes(path).SequenceEqual(before), "Changing language rewrote an initialized name");
                const string edited = "山田 <>& 𠮷";
                reopened.SaveGeneral(AdvisorTextSize.Standard, false, edited);
                reopened.SaveConsultation(8, 2, "Another prompt");
                reopened.SaveInformation(AdvisorInformation.Default.With(InformationKind.BattleWeapons, false));
                var saved = new AdvisorOptionsStore(path);
                saved.EnsureUserName(defaults);
                Require(saved.Current.UserName == edited && saved.Current.CommonPrompt == "Another prompt" && saved.Current.MaxQueryRounds == 8 &&
                    !saved.Current.Information.Allows(InformationKind.BattleWeapons), "Another category or restart replaced the edited user name");
                saved.SaveGeneral(saved.Current.TextSize, saved.Current.CampaignEnabled, "");
                before = File.ReadAllBytes(path);
                var blank = new AdvisorOptionsStore(path); blank.EnsureUserName(defaults);
                Require(blank.Current.UserName == "" && File.ReadAllBytes(path).SequenceEqual(before), "An explicit blank name was refilled with the default");
            }
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue(3), new JValue("bad\nname"),
                new JValue(new string('x', AdvisorOptions.MaxUserNameLength + 1)), new JValue(new string((char)0xd800, 1)) })
            {
                try { _ = AdvisorOptions.Parse(new JObject { ["user_name"] = invalid }.ToString()); }
                catch (AdvisorOptionsException error) when (error.Code == "user_name_invalid") { continue; }
                throw new InvalidOperationException("An invalid user name was silently accepted");
            }
        }));
        check("Category saves preserve current values in other categories and discarded candidates do not apply", () => InTemp(directory =>
        {
            var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
            store.SaveGeneral(AdvisorTextSize.Large, true, "Commander");
            // A second window or file edit saves consultation options after this page was opened.
            var other = new AdvisorOptionsStore(store.Path); other.SaveConsultation(7, 0, "共通の希望");
            store.SaveInformation(AdvisorInformation.All.With(InformationKind.BattleWeapons, false));
            store.SaveGeneral(AdvisorTextSize.Standard, false, "Commander");
            var saved = new AdvisorOptionsStore(store.Path).Current;
            Require(!saved.CampaignEnabled && saved.TextSize == AdvisorTextSize.Standard && saved.MaxQueryRounds == 7 && saved.HistoryExchanges == 0 &&
                saved.CommonPrompt == "共通の希望" && !saved.Information.Allows(InformationKind.BattleWeapons), "Saving stale category values lost another completed change");
            var discarded = new AdvisorOptions(1, true, historyExchanges: 20); _ = discarded.Data();
            Require(store.Read().MaxQueryRounds == 7 && store.Current.HistoryExchanges == 0, "Discarded edits became active");
            foreach (var size in Enum.GetValues<AdvisorTextSize>())
                Require(AdvisorOptions.Parse(System.Text.Encoding.UTF8.GetString(new AdvisorOptions(textSize: size).Data())).TextSize == size, "Text size did not round-trip");
        }));
        check("An options write failure leaves runtime options unchanged and does not touch other categories", () => InTemp(directory =>
        {
            var parent = Path.Combine(directory, "blocked"); Directory.CreateDirectory(parent);
            var store = new AdvisorOptionsStore(Path.Combine(parent, "options.json"));
            Directory.Delete(parent); File.WriteAllText(parent, "blocked parent");
            var apiPath = Path.Combine(directory, "gemini.json"); File.WriteAllText(apiPath, "untouched");
            try { store.SaveGeneral(AdvisorTextSize.Large, true, "Commander"); }
            catch (AdvisorOptionsException error) when (error.Code == "options_write_failed")
            {
                Require(store.Current.TextSize == AdvisorTextSize.Small && !store.Current.CampaignEnabled && File.ReadAllText(apiPath) == "untouched" &&
                    store.Current.UserName == null && !File.Exists(AdvisorProfile.SettingsPath(directory)), "A failed options write published changes or modified another category");
                return;
            }
            throw new InvalidOperationException("Expected options write failure");
        }));
        check("Both game modes advertise and enforce configured retrieval rounds and retain one final answer request", () =>
        {
            var campaign = QueryTests.Capture(new SituationTests.Fixture(Assembly.LoadFrom(campaignPath)));
            var battle = new BattleSnapshot(new JObject
            {
                ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Test mission",
                ["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
        ["public_objectives"] = new JArray(), ["friendly_units"] = new JArray(),
                ["unit_capabilities"] = new JArray(), ["contact_threat_references"] = new JArray(), ["weapon_references"] = new JArray(),
                ["observed_contacts"] = new JArray(), ["published_reports"] = new JArray(), ["mission_briefing"] = "Known briefing"
            });
            IAdvisorSource[] sources = { new CampaignAdvisorSource(() => campaign), new BattleAdvisorSource(() => battle) };
            foreach (var source in sources)
            foreach (var limit in new[] { 1, 7 })
            {
                var session = source.CreateSession("ja", "次の行動は？", limit);
                var transport = new RoundTransport(limit);
                var settings = AdvisorSettings.Parse("{\"api_key\":\"mock_round_settings\"}");
                var result = new GeminiAdvisor(transport).ConsultAsync(session, settings, Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
                Require(result.QueryRounds == limit && result.ApiRequests == limit + 1 && transport.Calls == limit + 1 && session.MaxRounds == limit,
                    "A fixed five-round or six-request limit overrode the configured consultation");
                var rejected = session.Execute(new JObject { ["consultation_id"] = session.Id,
                    ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) });
                Require((string?)rejected["error"]?["code"] == "round_limit" && session.RoundsUsed == limit,
                    "The adapter accepted more queries than it advertised");
            }
        });
    }
    private sealed class RoundTransport : IGeminiTransport
    {
        private readonly int limit;
        internal int Calls;
        internal RoundTransport(int limit) { this.limit = limit; }
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            Calls++;
            if (Calls == 1)
                Require((int?)InteractionFixture.Initial(body)["query_limits"]?["max_rounds"] == limit, "The AI was told a different limit from the adapter");
            var querying = Calls <= limit;
            Require((string?)body["generation_config"]!["tool_choice"] == (querying ? "auto" : "none"),
                "Tools were disabled too early or remained enabled for the final answer");
            var part = querying ? new JObject { ["functionCall"] = new JObject { ["name"] = "query_situation",
                ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) } } } : new JObject { ["text"] = "回答" };
            return Task.FromResult(InteractionFixture.Reply(part));
        }
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
