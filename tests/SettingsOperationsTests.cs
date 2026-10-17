using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class SettingsOperationsTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("API registration is local until saved and each operation merges only its entry with current storage", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json"); var store = new AdvisorApiSettingsStore(path, () => "Connection {0}");
            var pending = store.Read().Add(); pending.ApiKey = "mock_main"; pending.Model = "gemini-main";
            Require(!File.Exists(path), "Opening a new form saved an incomplete registration");
            store.SaveEntry(pending, true);
            Require(store.Read().Configurations.Count == 1 && store.Read().MainId == null, "Registration silently activated a credential");
            var stale = store.Read().Find(pending.Id)!; stale.Name = "Renamed";
            var second = store.Read().Add(); second.ApiKey = "mock_sub"; second.Model = "gemini-sub"; store.SaveEntry(second, true);
            store.Assign(pending.Id, second.Id); store.SaveLogging(true); store.SaveEntry(stale, false);
            var saved = store.Read();
            Require(saved.Configurations.Count == 2 && saved.Main.Name == "Renamed" && saved.Sub!.Id == second.Id && saved.LogAiJson,
                "Saving one form overwrote another completed operation");
            var before = File.ReadAllBytes(path); var invalid = store.Read().Add(); invalid.ApiKey = "";
            ThrowsApi("api_key_missing", () => store.SaveEntry(invalid, true));
            invalid.ApiKey = "mock_unused"; invalid.Model = "https://example.invalid/model";
            ThrowsApi("model_invalid", () => store.SaveEntry(invalid, true));
            Require(File.ReadAllBytes(path).SequenceEqual(before), "Rejected registration changed storage");
            var cancelled = store.Read().Find(pending.Id)!; cancelled.ApiKey = "unsaved";
            Require(store.Read().Main.ApiKey == "mock_main", "Canceling an editor altered saved credentials");
        }));
        check("Deleting an assigned API is durable, clears the role and never selects another credential", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "gemini.json"); var store = new AdvisorApiSettingsStore(path, () => "API {0}");
            var one = store.Read().Add(); one.ApiKey = "mock_one"; store.SaveEntry(one, true);
            var two = store.Read().Add(); two.ApiKey = "mock_two"; store.SaveEntry(two, true); store.Assign(one.Id, two.Id);
            var before = File.ReadAllBytes(path);
            ThrowsApi("api_selection_same", () => store.Assign(one.Id, one.Id));
            Require(File.ReadAllBytes(path).SequenceEqual(before), "An invalid role pair partially saved");
            store.Remove(one.Id);
            Require(store.Read().MainId == null && store.Read().SubId == two.Id && store.Read().Configurations.Count == 1,
                "Deleting main did not persist the removal or selected another credential");
            ThrowsApi("api_main_missing", () => AdvisorSettings.Load(path));
            store.Remove(two.Id); store.SaveLogging(true);
            Require(store.Read().Configurations.Count == 0 && store.Read().LogAiJson, "An empty API library prevented developer settings from saving");
        }));
        check("Profile registration, editing, activation and removal are independent durable operations", () => InTemp(directory =>
        {
            var original = new AdvisorProfile("Existing", "portrait.png", true, "Original default"); original.Save(directory);
            var store = new AdvisorProfileSettingsStore(directory, _ => { });
            var form = new AdvisorProfileDraft(store.Read()); var id = form.Add("New adviser", "Title"); form.Personality = "New style";
            Require(store.Read().Advisers.Count == 1, "Opening a profile form saved it");
            store.SaveEntry(form, id, true);
            Require(store.Read().Advisers.Count == 2 && store.Read().UseDefault, "Registration did not persist or unexpectedly changed active advisor");
            var oldForm = new AdvisorProfileDraft(store.Read()); oldForm.Select(id); oldForm.CustomName = "Edited";
            store.Select(id);
            var defaults = new AdvisorProfileDraft(store.Read()); defaults.Select(AdvisorProfile.DefaultId); defaults.Personality = "Edited default";
            store.SaveEntry(defaults, AdvisorProfile.DefaultId, false); store.SaveEntry(oldForm, id, false);
            Require(store.Read().ActiveId == id && store.Read().DisplayName == "Edited" && store.Read().DefaultPersonality == "Edited default",
                "Editing one profile replaced another form or changed the active selection");
            store.Remove(id);
            Require(store.Read().UseDefault && store.Read().Advisers.Count == 1, "Deleting active profile was not durable or did not return to default");
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            try { store.Remove(AdvisorProfile.DefaultId); throw new InvalidOperationException("Default was deleted"); }
            catch (AdvisorProfileException) { }
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before), "Default deletion changed saved profiles");
        }));
        check("Unconfigured or broken unrelated files do not block another category's save", () => InTemp(directory =>
        {
            var apiPath = Path.Combine(directory, "gemini.json"); File.WriteAllText(apiPath, "broken unrelated API JSON");
            var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
            var profilePath = AdvisorProfile.SettingsPath(directory); Directory.CreateDirectory(profilePath);
            store.SaveGeneral(AdvisorTextSize.Large, true); store.SaveConsultation(7, 0, "Common preference");
            Require(store.Current.CampaignEnabled && store.Current.HistoryExchanges == 0 && File.ReadAllText(apiPath) == "broken unrelated API JSON" && Directory.Exists(profilePath),
                "A category save required or rewrote unrelated settings");
            File.Delete(apiPath); Directory.Delete(profilePath);
            var profiles = new AdvisorProfileSettingsStore(directory, _ => { }); new AdvisorProfile("No credentials", "portrait.png").Save(directory);
            var form = new AdvisorProfileDraft(profiles.Read()) { CustomName = "Saved independently" };
            profiles.SaveEntry(form, form.SelectedId, false);
            Require(profiles.Read().DisplayName == "Saved independently" && !File.Exists(apiPath), "Profile saving created or required an API connection");
        }));
        check("Failed API and profile writes leave their saved data and other settings unchanged", () => InTemp(directory =>
        {
            var profile = new AdvisorProfile("Before", "portrait.png"); profile.Save(directory);
            var original = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            var blocked = Path.Combine(directory, "blocked.json"); Directory.CreateDirectory(blocked);
            var store = new AdvisorApiSettingsStore(blocked, () => "API {0}"); var entry = store.Read().Add(); entry.ApiKey = "mock_never_sent";
            ThrowsApi("settings_write_failed", () => store.SaveEntry(entry, true));
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(original), "A failed API write changed a profile");
            File.Delete(AdvisorProfile.SettingsPath(directory)); Directory.CreateDirectory(AdvisorProfile.SettingsPath(directory));
            var validApi = Path.Combine(directory, "gemini.json"); new AdvisorApiSettingsStore(validApi, () => "API {0}").SaveLogging(true);
            var beforeApi = File.ReadAllBytes(validApi);
            var form = new AdvisorProfileDraft(profile) { CustomName = "After" };
            // An unreadable profile is a visible error, not a fresh library that overwrites it.
            try { new AdvisorProfileSettingsStore(directory, _ => { }).SaveEntry(form, form.SelectedId, false); throw new InvalidOperationException("Unreadable profile was overwritten"); }
            catch (AdvisorProfileException) { }
            Require(Directory.Exists(AdvisorProfile.SettingsPath(directory)) && File.ReadAllBytes(validApi).SequenceEqual(beforeApi), "A failed profile operation modified another category");
        }));
    }
    private static void ThrowsApi(string code, Action action)
    {
        try { action(); } catch (AdvisorConnectionException failure) { Require(failure.Code == code, "Wrong error: " + failure.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
