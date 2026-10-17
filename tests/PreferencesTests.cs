using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class PreferencesTests
{
    private const string Key = "mock_preferences_key_never_sent";
    internal static void Run(Action<string, Action> check)
    {
        check("Discarded connection and log edits leave saved settings and the advisor unchanged", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json");
            File.WriteAllText(path, Original().ToString());
            var profile = new AdvisorProfile("Original", "portrait.png"); profile.Save(directory);
            var before = File.ReadAllBytes(path);
            var profileBefore = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            var draft = AdvisorSettings.Edit(path);
            draft.Main.ApiKey = "different_mock_key"; draft.Main.Model = "gemini-user-selected-model"; draft.LogAiJson = true;
            _ = draft.Data();
            var profileDraft = new AdvisorProfileDraft(profile) { CustomName = "Unsaved name", Personality = "Unsaved style" };
            Require(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(profileBefore),
                "Editing or validating saved a pending change");
            var reopened = AdvisorSettings.Edit(path);
            Require(reopened.Main.ApiKey == Key && reopened.Main.Model == "gemini-original-model" && !reopened.LogAiJson &&
                AdvisorProfile.Load(directory).DisplayName == "Original", "Discarded edits survived reopening settings");
        }));
        check("Shared save applies advisor, credentials, Gemini model and logging while preserving advanced values", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json"); File.WriteAllText(path, Original().ToString());
            var draft = AdvisorSettings.Edit(path);
            draft.Main.ApiKey = "  changed_mock_key  "; draft.Main.Model = " gemini-2.5-pro "; draft.LogAiJson = true;
            var profile = new AdvisorProfileDraft(new AdvisorProfile("Before", "portrait.png", false, "Default style", "Before style"))
                { CustomName = "After", Personality = "After style" };
            Save(profile, draft, directory, path);
            var saved = AdvisorSettings.Load(path); var advisor = AdvisorProfile.Load(directory);
            Require(saved.ApiKey == "changed_mock_key" && saved.Model == "gemini-2.5-pro" && saved.LogAiJson &&
                saved.RequestTimeoutSeconds == 45 && saved.ConsultationTimeoutSeconds == 90 && saved.MaxOutputTokens == 2048,
                "Saving changed unedited values or lost connection edits");
            Require(advisor.DisplayName == "After" && advisor.Personality == "After style" && advisor.DefaultPersonality == "Default style",
                "The profile was not committed with the connection");
        }));
        check("First setup can save an advisor without an API key but a real consultation still requires one", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json");
            var draft = AdvisorSettings.Edit(path);
            Require(draft.Main.ApiKey == "" && draft.Main.Model == AdvisorSettings.DefaultModel && !draft.LogAiJson, "Missing settings did not start unconfigured");
            Save(new AdvisorProfileDraft(new AdvisorProfile("First advisor", "portrait.png")), draft, directory, path);
            Require(AdvisorProfile.Load(directory).DisplayName == "First advisor" && AdvisorSettings.Edit(path).Main.ApiKey == "", "First profile setup required credentials");
            Throws("api_key_missing", () => AdvisorSettings.Load(path));
        }));
        check("Gemini model IDs are editable beyond Lite while other providers and URL paths are rejected before saving", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json"); File.WriteAllText(path, Original().ToString());
            foreach (var id in new[] { "gemini-pro", "gemini-2.5-flash", "gemini-flash-latest", "gemini-custom_future.v2" })
            {
                var draft = AdvisorSettings.Edit(path); draft.Main.Model = id;
                Require(AdvisorSettings.Parse(Encoding.UTF8.GetString(draft.Data())).Model == id, "A Gemini model was restricted by a fixed catalogue or Lite-only filter");
            }
            var before = File.ReadAllBytes(path);
            var profile = new AdvisorProfile("Before", "portrait.png"); profile.Save(directory);
            var profileBefore = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            foreach (var id in new[] { "other-provider", "https://example.invalid/gemini-pro", "gemini-pro/../other", "gemini-pro?key=value", "" })
            {
                var draft = AdvisorSettings.Edit(path); draft.Main.Model = id;
                Throws("model_invalid", () => Save(new AdvisorProfileDraft(profile) { CustomName = "Unsaved" }, draft, directory, path));
                Require(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(profileBefore),
                    "A rejected model saved part of the settings");
            }
        }));
        check("A failed profile save restores connection settings and a failed connection write leaves the profile intact", () => InTemp(directory =>
        {
            foreach (var existing in new[] { false, true })
            {
                var folder = Path.Combine(directory, existing ? "existing" : "new"); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "gemini.json");
                if (existing) File.WriteAllText(path, Original().ToString());
                var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
                Directory.CreateDirectory(AdvisorProfile.SettingsPath(folder));
                var draft = AdvisorSettings.Edit(path); draft.Main.ApiKey = "changed_mock_key"; draft.LogAiJson = true;
                try { Save(new AdvisorProfileDraft(new AdvisorProfile("New", "portrait.png")), draft, folder, path); }
                catch (AdvisorProfileException error) when (error.Code == "profile_write_failed")
                {
                    Require(before == null ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(before), "Failed save left new credentials or logging active");
                    continue;
                }
                throw new InvalidOperationException("Expected the profile write to fail");
            }
            var blockedPath = Path.Combine(directory, "blocked.json"); Directory.CreateDirectory(blockedPath);
            var profile = new AdvisorProfile("Original", "portrait.png"); profile.Save(directory);
            var original = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            Throws("settings_write_failed", () => Save(new AdvisorProfileDraft(profile) { CustomName = "Unsaved" },
                AdvisorSettings.Edit(blockedPath), directory, blockedPath));
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(original), "An AI write failure still saved the profile");
        }));
    }

    private static AdvisorProfile Save(AdvisorProfileDraft profile, AdvisorSettingsDraft connection, string directory, string path) =>
        AdvisorPreferences.Save(profile, connection, new AdvisorOptionsStore(Path.Combine(directory, "options.json")),
            new AdvisorOptions(), directory, path);

    private static JObject Original() => new() { ["api_key"] = Key, ["model"] = "gemini-original-model",
        ["log_ai_json"] = false, ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90, ["max_output_tokens"] = 2048 };
    private static void Throws(string code, Action action)
    {
        try { action(); }
        catch (AdvisorConnectionException error)
        { if (error.Code == code && !error.ToString().Contains(Key, StringComparison.Ordinal)) return; throw; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
