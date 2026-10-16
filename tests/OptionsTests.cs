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
            Require(!store.Current.CampaignEnabled && store.Current.MaxQueryRounds == 5 && !File.Exists(path),
                "A new or existing installation implicitly enabled campaign alpha support or wrote settings on read");
            Require(!AdvisorOptions.Parse("{}").CampaignEnabled && AdvisorOptions.Parse("{}").MaxQueryRounds == 5,
                "Absent fields did not use compatible defaults");
            foreach (var invalid in new[] { "{", "{\"campaign_advisor_enabled\":\"true\"}", "{\"unknown\":1}",
                "{\"max_query_rounds\":5,\"max_query_rounds\":7}", "{\"max_query_rounds\":0}", "{\"max_query_rounds\":21}", "{\"max_query_rounds\":1.5}" })
            {
                File.WriteAllText(path, invalid);
                try { _ = store.Read(); }
                catch (AdvisorOptionsException)
                {
                    Require(store.Current.MaxQueryRounds == 5 && !store.Current.CampaignEnabled && File.ReadAllText(path) == invalid,
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
        check("Shared save publishes campaign and retrieval settings together while discarded edits leave saved state intact", () => InTemp(directory =>
        {
            var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
            var pending = new AdvisorOptions(7, true);
            Require(!store.Current.CampaignEnabled && store.Read().MaxQueryRounds == 5, "Pending edits took effect before save");
            var connection = new AdvisorSettingsDraft(new JObject { ["api_key"] = "" });
            AdvisorPreferences.Save(new AdvisorProfileDraft(new AdvisorProfile("Saved advisor", "portrait.png")), connection, store, pending,
                directory, Path.Combine(directory, "gemini.json"));
            Require(store.Current.CampaignEnabled && store.Current.MaxQueryRounds == 7 &&
                new AdvisorOptionsStore(store.Path).Current.MaxQueryRounds == 7 && AdvisorProfile.Load(directory).DisplayName == "Saved advisor",
                "The common save did not publish and persist all settings");
            var discarded = new AdvisorOptions(1, false);
            _ = discarded.Data();
            Require(store.Current.CampaignEnabled && store.Read().CampaignEnabled && store.Read().MaxQueryRounds == 7,
                "Discarding an edited candidate changed runtime or saved options");
        }));
        check("A failed save restores options and credentials without publishing partial runtime changes", () => InTemp(directory =>
        {
            foreach (var existing in new[] { false, true })
            {
                var folder = Path.Combine(directory, existing ? "existing" : "new"); Directory.CreateDirectory(folder);
                var store = new AdvisorOptionsStore(Path.Combine(folder, "options.json"));
                if (existing) store.Write(new AdvisorOptions());
                var optionBytes = File.Exists(store.Path) ? File.ReadAllBytes(store.Path) : null;
                var connectionPath = Path.Combine(folder, "gemini.json");
                File.WriteAllText(connectionPath, "{\"api_key\":\"mock_original\"}");
                var connectionBytes = File.ReadAllBytes(connectionPath);
                Directory.CreateDirectory(AdvisorProfile.SettingsPath(folder));
                try
                {
                    AdvisorPreferences.Save(new AdvisorProfileDraft(new AdvisorProfile("Unsaved", "portrait.png")),
                        new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock_changed" }), store, new AdvisorOptions(9, true), folder, connectionPath);
                }
                catch (AdvisorProfileException error) when (error.Code == "profile_write_failed")
                {
                    Require(File.ReadAllBytes(connectionPath).SequenceEqual(connectionBytes) &&
                        (optionBytes == null ? !File.Exists(store.Path) : File.ReadAllBytes(store.Path).SequenceEqual(optionBytes)) &&
                        !store.Current.CampaignEnabled && store.Current.MaxQueryRounds == 5, "A profile failure left a partial save active");
                    continue;
                }
                throw new InvalidOperationException("Expected a failed profile save");
            }
            var blocked = Path.Combine(directory, "blocked.json");
            var blockedStore = new AdvisorOptionsStore(blocked);
            Directory.CreateDirectory(blocked);
            var path = Path.Combine(directory, "gemini.json"); File.WriteAllText(path, "{\"api_key\":\"mock_original\"}");
            var before = File.ReadAllBytes(path);
            try { AdvisorPreferences.Save(new AdvisorProfileDraft(new AdvisorProfile("Unsaved", "portrait.png")),
                new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock_changed" }), blockedStore, new AdvisorOptions(3, true), directory, path); }
            catch (AdvisorOptionsException error) when (error.Code == "options_write_failed")
            {
                Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(AdvisorProfile.SettingsPath(directory)) &&
                    !blockedStore.Current.CampaignEnabled, "An options write failure changed another setting");
                return;
            }
            throw new InvalidOperationException("Expected an options write failure");
        }));
        check("Both game modes advertise and enforce configured retrieval rounds and retain one final answer request", () =>
        {
            var campaign = QueryTests.Capture(new SituationTests.Fixture(Assembly.LoadFrom(campaignPath)));
            var battle = new BattleSnapshot(new JObject
            {
                ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Test mission",
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
            var initial = JObject.Parse((string)body["contents"]![0]!["parts"]![0]!["text"]!);
            Require((int?)initial["query_limits"]?["max_rounds"] == limit, "The AI was told a different limit from the adapter");
            var querying = Calls <= limit;
            Require((string?)body["toolConfig"]!["functionCallingConfig"]!["mode"] == (querying ? "AUTO" : "NONE"),
                "Tools were disabled too early or remained enabled for the final answer");
            var part = querying ? new JObject { ["functionCall"] = new JObject { ["name"] = "query_situation",
                ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) } } } : new JObject { ["text"] = "回答" };
            return Task.FromResult(new JObject { ["candidates"] = new JArray(new JObject { ["finishReason"] = "STOP",
                ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray(part) } }) });
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
