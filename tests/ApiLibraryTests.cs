using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ApiLibraryTests
{
    private const string MainKey = "mock_library_main_no_network";
    private const string SubKey = "mock_library_sub_no_network";
    internal static void Run(Action<string, Action> check)
    {
        check("Startup renames old settings byte-for-byte, preserves formats and leaves new installations unwritten", () => InTemp(directory =>
        {
            var current = AdvisorSettings.MigrateFileName(directory);
            Require(!Directory.Exists(Path.GetDirectoryName(current)!) && !File.Exists(current), "Startup created empty settings for a new installation");
            var registered = new JObject { ["schema_version"] = 2,
                ["api_configurations"] = new JArray(new JObject { ["id"] = "kept", ["name"] = "Registered 日本語", ["api_key"] = MainKey, ["model"] = "gemini-main" }),
                ["main_api_id"] = "kept", ["sub_api_id"] = JValue.CreateNull(), ["max_output_tokens"] = 2048, ["log_ai_json"] = true };
            var modern = JObject.Parse(Encoding.UTF8.GetString(new AdvisorSettingsDraft(Legacy()).Data()));
            foreach (var pair in new[] { Legacy(), registered, modern })
            {
                var dataPath = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                var destination = AdvisorSettings.PathIn(dataPath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var legacy = Path.Combine(Path.GetDirectoryName(destination)!, "gemini.json");
                var before = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(pair.ToString())).ToArray();
                File.WriteAllBytes(legacy, before);
                var path = AdvisorSettings.MigrateFileName(dataPath);
                Require(path == destination && !File.Exists(legacy) && File.ReadAllBytes(path).SequenceEqual(before),
                    "Filename migration transformed, duplicated or lost the existing settings");
                var saved = AdvisorSettings.Edit(path);
                Require(saved.Main.ApiKey == MainKey && saved.Options.MaxOutputTokens == 2048 && saved.LogAiJson,
                    "Renaming lost the registered connection, output budget or logging preference");
                Require(AdvisorSettings.MigrateFileName(dataPath) == path && File.ReadAllBytes(path).SequenceEqual(before), "Repeated startup rewrote the migrated settings");
                new AdvisorApiSettingsStore(path, () => "API {0}").SaveLogging(false);
                Require(!File.Exists(legacy) && !AdvisorSettings.Edit(path).LogAiJson, "Saving after migration wrote back to the retired filename");
            }
        }));
        check("Existing new settings win and migration failures preserve the old file without silently resetting it", () => InTemp(directory =>
        {
            var current = AdvisorSettings.PathIn(directory); Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            var legacy = Path.Combine(Path.GetDirectoryName(current)!, "gemini.json");
            var original = Encoding.UTF8.GetBytes(Legacy().ToString()); File.WriteAllBytes(legacy, original);
            var newer = Legacy(); newer["api_key"] = "mock_preferred_new_settings"; File.WriteAllText(current, newer.ToString());
            Require(AdvisorSettings.MigrateFileName(directory) == current && AdvisorSettings.Load(current).ApiKey == "mock_preferred_new_settings" &&
                File.ReadAllBytes(legacy).SequenceEqual(original), "Migration overwrote the new file or removed the old file when both existed");
            File.WriteAllText(current, "broken new settings");
            Require(AdvisorSettings.MigrateFileName(directory) == current, "A broken new file selected the old filename");
            Throws("settings_invalid", () => AdvisorSettings.Edit(current));
            File.Delete(current); Directory.CreateDirectory(current);
            Throws("settings_migration_failed", () => AdvisorSettings.MigrateFileName(directory));
            Require(File.ReadAllBytes(legacy).SequenceEqual(original), "A blocked migration deleted or rewrote the old file");
            Directory.Delete(current); File.Delete(legacy); Directory.CreateDirectory(legacy);
            Throws("settings_migration_failed", () => AdvisorSettings.MigrateFileName(directory));
            Require(!File.Exists(current), "An invalid old settings path was silently replaced with empty new settings");
        }));
        check("Legacy API migration preserves both connections and advanced settings without writing until accepted", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json");
            var old = Legacy(); File.WriteAllText(path, old.ToString()); var before = File.ReadAllBytes(path);
            var draft = AdvisorSettings.Edit(path, "API設定{0}");
            Require(draft.Configurations.Count == 2 && draft.Main.ApiKey == MainKey && draft.Sub!.ApiKey == SubKey &&
                draft.Main.Model == "gemini-main" && draft.Sub.Model == "gemini-sub", "Migration lost a key/model pair or changed roles");
            var pending = draft.Data();
            Require(File.ReadAllBytes(path).SequenceEqual(before) && AdvisorSettings.Load(path).ApiKey == MainKey,
                "Opening or validating settings rewrote the legacy file");
            AdvisorSettings.WriteBytes(path, pending);
            var saved = AdvisorSettings.Load(path); var loaded = AdvisorSettings.Edit(path, "Different language {0}");
            Require(saved.ApiKey == MainKey && saved.ForFallback().RequestApiKey == SubKey && saved.Model == "gemini-main" &&
                saved.ForFallback().RequestModel == "gemini-sub" && saved.RequestTimeoutSeconds == 45 &&
                saved.MaxOutputTokens == 2048 && saved.LogAiJson &&
                loaded.Main.Name == "API設定1" && loaded.Sub!.Name == "API設定2", "Saving migration reset credentials, labels or advanced settings");
            var json = JObject.Parse(File.ReadAllText(path));
            Require(json["api_key"] == null && json["fallback_api_key"] == null && json["model"] == null && json["fallback_model"] == null,
                "Saving retained a second, conflicting set of positional credentials");
            var accepted = File.ReadAllBytes(path);
            loaded.Main.Name = "Discarded rename";
            var originalMainId = loaded.MainId; loaded.MainId = loaded.SubId; loaded.SubId = originalMainId;
            var discarded = loaded.Add(); discarded.ApiKey = "mock_discarded"; _ = loaded.Data();
            Require(File.ReadAllBytes(path).SequenceEqual(accepted) && AdvisorSettings.Edit(path).Main.Name == "API設定1",
                "Discarding registration or role edits changed the saved settings");
        }));
        check("Named assignments select complete key/model pairs and existing fallback uses the newly selected sub", () =>
        {
            var draft = new AdvisorSettingsDraft(Legacy());
            var first = draft.Main; var second = draft.Sub!;
            first.Name = "Free"; second.Name = "Paid";
            first.Model = second.Model = "gemini-same-model";
            draft.MainId = second.Id; draft.SubId = first.Id;
            Require(draft.Main.Id == second.Id && draft.Main.Name == "Paid" && draft.Sub!.Id == first.Id &&
                first.ApiKey == MainKey && second.ApiKey == SubKey, "Reassigning roles changed the registered credentials");
            var settings = AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data()));
            var sent = new List<string>();
            var transport = new Fake((s, _, _) =>
            {
                sent.Add(s.RequestApiKey);
                Require(s.RequestModel == "gemini-same-model", "An assignment used the wrong model");
                if (sent.Count == 1) throw new AdvisorConnectionException("rate_limited", 429);
                return Task.FromResult(InteractionFixture.Reply(new JObject { ["text"] = "Selected" }));
            });
            var input = new AdvisorProviderInput(new JObject(), "Test", new JObject
            { ["name"] = "query_situation", ["description"] = "Test", ["parameters"] = new JObject() });
            using var fallback = new AdvisorConnection(new GeminiProvider(transport, settings), new GeminiProvider(transport, settings.ForFallback()), input, _ => { });
            _ = fallback.GenerateAsync(false, new AdvisorContinuation(new JArray(), 0, 1), CancellationToken.None, (_, _, _, _) => { }).GetAwaiter().GetResult();
            Require(sent.SequenceEqual(new[] { SubKey, MainKey }), "Fallback used the old positional credentials");
        });
        check("Unassigned API entries can be tested independently while edits do not enable unsaved logging", () =>
        {
            var source = Legacy(); source["log_ai_json"] = false;
            var draft = new AdvisorSettingsDraft(source);
            var extra = draft.Add(); extra.Name = "Independent"; extra.ApiKey = "mock_library_extra"; extra.Model = "gemini-extra";
            draft.Main.ApiKey = ""; draft.Main.Model = ""; draft.LogAiJson = true;
            var test = draft.TestConfiguration(extra.Id);
            Require(test.RequestApiKey == extra.ApiKey && test.RequestModel == extra.Model && !test.LogAiJson &&
                test.FallbackApiKey == "" && test.RequestTimeoutSeconds == 45,
                "Testing depended on main settings, used another key or applied unsaved logging");
            Throws("api_key_missing", () => draft.TestSettings());
        });
        check("Deleting registered API settings clears their assignments without silently choosing another credential", () =>
        {
            var draft = new AdvisorSettingsDraft(Legacy());
            var mainId = draft.Main.Id; var subId = draft.Sub!.Id;
            draft.Remove(subId);
            Require(draft.SubId == null && draft.Main.Id == mainId && AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data())).FallbackApiKey == "",
                "Deleting the sub selected an unintended fallback or changed main");
            var extra = draft.Add(); extra.ApiKey = SubKey; extra.Model = "gemini-extra";
            draft.Remove(mainId);
            Require(draft.MainId == null && draft.Configurations.Single().Id == extra.Id, "Deleting main silently selected a different credential");
            Throws("api_main_missing", () => AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data())));
            draft.MainId = extra.Id;
            Require(AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data())).ApiKey == SubKey, "A replacement could not be selected explicitly");
            draft.Remove(extra.Id);
            Throws("api_main_missing", () => AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data())));
            var last = draft.Add(); draft.MainId = last.Id;
            Throws("api_key_missing", () => draft.Data());
        });
        check("Invalid or ambiguous API libraries fail visibly instead of reverting to legacy or another registered credential", () =>
        {
            var draft = new AdvisorSettingsDraft(Legacy());
            var valid = JObject.Parse(Encoding.UTF8.GetString(draft.Data()));
            void Bad(string code, Action<JObject> edit)
            { var data = (JObject)valid.DeepClone(); edit(data); Throws(code, () => AdvisorSettings.Parse(data.ToString())); }
            Bad("settings_invalid", data => data["schema_version"] = 99);
            Bad("settings_invalid", data => data.Remove("schema_version"));
            Bad("settings_invalid", data => data["api_key"] = "conflicting_legacy_key");
            Bad("settings_invalid", data => data["api_configurations"]![1]!["id"] = data["api_configurations"]![0]!["id"]!.DeepClone());
            Bad("api_configuration_name_invalid", data => data["api_configurations"]![1]!["name"] = "");
            Bad("api_selection_invalid", data => data["main_api_id"] = "not-registered");
            Bad("api_selection_same", data => data["sub_api_id"] = data["main_api_id"]!.DeepClone());
            var legacy = Legacy(); legacy.Remove("fallback_api_key"); legacy["fallback_model"] = "gemini-model-only";
            var modelOnly = new AdvisorSettingsDraft(legacy);
            Require(modelOnly.SubId == null && modelOnly.Configurations.Count == 1 &&
                AdvisorSettings.Parse(Encoding.UTF8.GetString(modelOnly.Data())).FallbackApiKey == "",
                "Incomplete legacy sub was registered or unexpectedly enabled fallback");
        });
    }
    private static JObject Legacy() => new() { ["api_key"] = MainKey, ["fallback_api_key"] = SubKey,
        ["model"] = "gemini-main", ["fallback_model"] = "gemini-sub", ["request_timeout_seconds"] = 45,
        ["consultation_timeout_seconds"] = 90, ["max_output_tokens"] = 2048, ["log_ai_json"] = true };
    private static void Throws(string code, Action action)
    {
        try { action(); }
        catch (AdvisorConnectionException error)
        {
            Require(error.Code == code && !error.ToString().Contains(MainKey, StringComparison.Ordinal) &&
                !error.ToString().Contains(SubKey, StringComparison.Ordinal), "Wrong or unsafe API library error: " + error.Code);
            return;
        }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-api-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fake : IGeminiTransport
    {
        private readonly Func<AdvisorSettings, JObject, CancellationToken, Task<JObject>> send;
        internal Fake(Func<AdvisorSettings, JObject, CancellationToken, Task<JObject>> send) { this.send = send; }
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation) => send(settings, body, cancellation);
    }
}
