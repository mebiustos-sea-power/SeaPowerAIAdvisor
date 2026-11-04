using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class ConfigMigrationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Standard BOM detection preserves current profiles until a normal UTF-8 save", () => InTemp(directory =>
        {
            var index = 0;
            foreach (var encoding in BomEncodings())
            {
                var profileDirectory = Path.Combine(directory, (index++).ToString());
                var original = new AdvisorProfile("田中・James 😀", "portrait.png", customRole: "艦長");
                original.Save(profileDirectory);
                var path = AdvisorProfile.SettingsPath(profileDirectory);
                var json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                var before = Encode(encoding, json); File.WriteAllBytes(path, before);
                var loaded = AdvisorProfile.Load(profileDirectory);
                Require(loaded.DisplayName == original.DisplayName && loaded.CustomRole == original.CustomRole &&
                    File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
                    "Reading a BOM profile lost Unicode text or rewrote current settings");
                loaded.Save(profileDirectory);
                var saved = JObject.Parse(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
                Require((string?)saved["custom_advisors"]![0]!["display_name"] == original.DisplayName &&
                    AdvisorProfile.Load(profileDirectory).CustomRole == original.CustomRole,
                    "Saving a BOM profile failed to produce UTF-8 or changed its contents");
            }
        }));
        check("BOM API files migrate or save as UTF-8 while current schemas remain untouched on read", () => InTemp(directory =>
        {
            var index = 0;
            foreach (var encoding in BomEncodings())
            foreach (var version in new[] { 3, AdvisorApiLibrary.SchemaVersion })
            {
                var path = Path.Combine(directory, (index++).ToString() + ".json");
                var entry = new AdvisorApiConfiguration("bom", "接続 😀", "mock-bom-key", "gemini-bom", 45, 2048);
                var source = new AdvisorApiLibrary(new[] { entry }, entry.Id, null).Document(new JObject(), false);
                source["schema_version"] = version;
                var before = Encode(encoding, source.ToString()); File.WriteAllBytes(path, before);
                var loaded = AdvisorSettings.Edit(path);
                Require(loaded.Main.Name == entry.Name && loaded.Main.ApiKey == entry.ApiKey && loaded.Main.MaxOutputTokens == 2048,
                    "BOM decoding changed a connection or its output budget");
                if (version == AdvisorApiLibrary.SchemaVersion)
                    Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
                        "Encoding alone caused a migration or backup");
                else
                {
                    var migrated = JObject.Parse(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
                    Require((int?)migrated["schema_version"] == AdvisorApiLibrary.SchemaVersion &&
                        File.ReadAllBytes(path + ".bak").SequenceEqual(before),
                        "Schema migration failed to save UTF-8 with an exact original backup");
                }
                new AdvisorApiSettingsStore(path, () => "API {0}").SaveLogging(true);
                var saved = JObject.Parse(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
                Require((bool?)saved["log_ai_json"] == true && (string?)saved["api_configurations"]![0]!["name"] == entry.Name &&
                    AdvisorSettings.Load(path).ApiKey == entry.ApiKey,
                    "Saving BOM API settings failed to produce UTF-8 or changed credentials");
            }
        }));
        check("Configuration migrations apply in order without mutating input and stop at the current schema", () =>
        {
            var source = new JObject { ["amount"] = 7 };
            var calls = 0;
            void Advance(JObject value, int version)
            {
                calls++;
                if (version == 0) { value["quantity"] = value["amount"]!.DeepClone(); value.Remove("amount"); }
                else value["quantity"] = (int)value["quantity"]! * 2;
                value["schema_version"] = version + 1;
            }
            var result = ConfigMigration.Upgrade(source, 2, Advance);
            Require(calls == 2 && (int)result["quantity"]! == 14 && source["quantity"] == null && source["schema_version"] == null,
                "Migration order or original data was lost");
            Require(ReferenceEquals(result, ConfigMigration.Upgrade(result, 2, Advance)) && calls == 2, "An up-to-date migration ran again");
            var before = source.ToString();
            Expect<JsonException>(() => ConfigMigration.Upgrade(source, 2, (value, version) =>
            { if (version == 1) throw new JsonSerializationException("Unsupported input"); value["schema_version"] = 1; value.Remove("amount"); }));
            Require(source.ToString() == before, "A failed later step changed its input");
            Expect<InvalidOperationException>(() => ConfigMigration.Upgrade(source, 1, (_, _) => { }));
            foreach (var version in new JToken[] { new JValue(3), new JValue(0), new JValue(-1), new JValue("1"), JValue.CreateNull() })
                Expect<JsonException>(() => ConfigMigration.Upgrade(new JObject { ["schema_version"] = version }, 2, Advance));
        });
        check("Options migrate explicit and legacy choices once, preserve inactive children and retain an exact backup", () => InTemp(directory =>
        {
            foreach (var legacy in new[] { true, false })
            {
                var path = Path.Combine(directory, legacy + ".json");
                var value = new JObject { ["common_prompt"] = "Keep my instructions", ["max_query_rounds"] = 9,
                    ["user_name"] = "User", ["unit_mention_completion"] = true };
                if (legacy) value["disabled_information"] = new JArray("battle_weapons", "campaign_forces");
                else value["information_selection"] = new JObject { ["battle_units"] = false, ["battle_weapons"] = true, ["battle_bases"] = false, ["battle_mission_messages"] = true };
                var before = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(value.ToString())).ToArray(); File.WriteAllBytes(path, before);
                var loaded = new AdvisorOptionsStore(path).Current;
                Require(loaded.CommonPrompt == "Keep my instructions" && loaded.MaxQueryRounds == 9 && loaded.UnitMentionCompletion &&
                    File.ReadAllBytes(path + ".bak").SequenceEqual(before), "Options migration lost values or the backup");
                Require(legacy ? !loaded.Information.Allows(InformationKind.BattleWeapons) && loaded.Information.Allows(InformationKind.BattleBases) :
                    loaded.Information.With(InformationKind.BattleUnits, true).Allows(InformationKind.BattleWeapons) &&
                    !loaded.Information.With(InformationKind.BattleUnits, true).Allows(InformationKind.BattleBases) && loaded.Information.Allows(InformationKind.BattleMissionMessages),
                    "Migration changed an effective or inactive user selection");
                VerifyNoRewrite(path, () => _ = new AdvisorOptionsStore(path));
            }
        }));
        check("Crew preferences receive a schema without resetting connection, language-independent choices or data selection", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "crew.json");
            var source = new CrewSettings("selected-api", 12, 22, CrewNotificationPosition.BottomLeft, false, 71, showRanks: false).Json();
            source.Remove("schema_version"); File.WriteAllText(path, source.ToString()); var before = File.ReadAllBytes(path);
            var loaded = new CrewSettingsStore(path).Current;
            Require(loaded.ConnectionId == "selected-api" && loaded.IntervalMinutes == 12 && loaded.DisplaySeconds == 22 &&
                loaded.NotificationPosition == CrewNotificationPosition.BottomLeft && !loaded.ShowHelpTab && !loaded.ShowRanks &&
                loaded.ExchangeChancePercent == 71 && JToken.DeepEquals(loaded.Information.Json(), source["information_selection"]) &&
                File.ReadAllBytes(path + ".bak").SequenceEqual(before), "Crew migration reset a saved preference");
            VerifyNoRewrite(path, () => _ = new CrewSettingsStore(path));
        }));
        check("API metadata migration preserves assignments, connection-specific budgets and opaque extension settings", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json");
            var source = new JObject { ["schema_version"] = 3, ["main_api_id"] = "nano", ["sub_api_id"] = JValue.CreateNull(),
                ["translation_api_id"] = "", ["log_ai_json"] = true, ["request_timeout_seconds"] = 123,
                ["api_configurations"] = new JArray(
                    new JObject { ["id"] = "nano", ["name"] = "My Nano", ["provider"] = "nanogpt", ["prompt"] = "Preserve prompt", ["situation_delivery"] = "upfront",
                        ["settings"] = new JObject { ["api_key"] = "mock-nano-key", ["model"] = "deepseek/test", ["max_output_tokens"] = "4321" } },
                    new JObject { ["id"] = "custom", ["name"] = "External", ["provider"] = "custom-provider", ["request_timeout_seconds"] = 77,
                        ["max_output_tokens"] = 5000, ["settings"] = new JObject { ["extension_option"] = new JArray(1, 2) } }) };
            File.WriteAllText(path, source.ToString()); var before = File.ReadAllBytes(path);
            var loaded = AdvisorSettings.Edit(path);
            Require(loaded.MainId == "nano" && loaded.TranslationId == "" && loaded.LogAiJson && loaded.Main.Prompt == "Preserve prompt" &&
                loaded.Main.SituationDelivery == AdvisorSituationDelivery.Upfront && loaded.Main.MaxOutputTokens == 4321 && loaded.Main.RequestTimeoutSeconds == 123 &&
                loaded.Find("custom")!.RequestTimeoutSeconds == 77 && loaded.Find("custom")!.MaxOutputTokens == 5000 &&
                JToken.DeepEquals(loaded.Find("custom")!.Settings, source["api_configurations"]![1]!["settings"]) &&
                File.ReadAllBytes(path + ".bak").SequenceEqual(before), "API migration changed a saved connection or extension settings");
            VerifyNoRewrite(path, () => _ = AdvisorSettings.Edit(path));
        }));
        check("Invalid or future settings fail before migration writes or creates a backup", () => InTemp(directory =>
        {
            foreach (var json in new[] { "{\"schema_version\":999}", "{\"schema_version\":null}", "{\"common_prompt\":123}",
                "{\"disabled_information\":[],\"information_selection\":{}}", "{\"unknown_setting\":true}" })
            {
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, json);
                var before = File.ReadAllBytes(path);
                Expect<AdvisorOptionsException>(() => _ = new AdvisorOptionsStore(path));
                Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"), "Rejected options were overwritten");
            }
            var api = Path.Combine(directory, "api.json");
            foreach (var model in new[] { "", "invalid-model" })
            {
                File.WriteAllText(api, new JObject { ["api_key"] = "mock-key", ["model"] = model }.ToString());
                var apiBefore = File.ReadAllBytes(api);
                Expect<AiConnectionException>(() => _ = AdvisorSettings.Edit(api));
                Require(File.ReadAllBytes(api).SequenceEqual(apiBefore) && !File.Exists(api + ".bak"), "An invalid API model discarded the saved credential");
            }
            byte[] original;
            var crew = Path.Combine(directory, "crew.json"); File.WriteAllText(crew, "{\"interval_minutes\":0}"); original = File.ReadAllBytes(crew);
            Expect<CrewException>(() => _ = new CrewSettingsStore(crew));
            Require(File.ReadAllBytes(crew).SequenceEqual(original) && !File.Exists(crew + ".bak"), "Invalid Crew data was persisted");
            var corrupt = Path.Combine(directory, "corrupt.json"); var bytes = new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 }; File.WriteAllBytes(corrupt, bytes);
            Expect<AdvisorOptionsException>(() => _ = new AdvisorOptionsStore(corrupt));
            Require(File.ReadAllBytes(corrupt).SequenceEqual(bytes) && !File.Exists(corrupt + ".bak"), "Invalid UTF-8 was replaced and saved");
        }));
        check("Backup and replacement failures preserve the original settings and never publish migrated preferences", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "options.json"); var before = Encoding.UTF8.GetBytes("{\"common_prompt\":\"Keep\"}"); File.WriteAllBytes(path, before);
            Directory.CreateDirectory(path + ".bak");
            Expect<AdvisorOptionsException>(() => _ = new AdvisorOptionsStore(path));
            Require(File.ReadAllBytes(path).SequenceEqual(before), "A backup failure replaced the original");
            Directory.Delete(path + ".bak");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Expect<AdvisorOptionsException>(() => _ = new AdvisorOptionsStore(path));
                Require(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(path + ".bak").SequenceEqual(before), "A replacement failure lost original data");
            }
            Require(new AdvisorOptionsStore(path).Current.CommonPrompt == "Keep", "Migration could not recover after the write failure");
        }));
    }
    private static Encoding[] BomEncodings() => new Encoding[]
    {
        new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)
    };
    private static byte[] Encode(Encoding encoding, string text) => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    private static void VerifyNoRewrite(string path, Action read)
    {
        var before = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak");
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)); var stamp = File.GetLastWriteTimeUtc(path);
        read();
        Require(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup) && File.GetLastWriteTimeUtc(path) == stamp,
            "Reading current settings rewrote the document or backup");
    }
    private static void InTemp(Action<string> action)
    { var path = Path.Combine(Path.GetTempPath(), "aiadvisor-migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); try { action(path); } finally { Directory.Delete(path, true); } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
