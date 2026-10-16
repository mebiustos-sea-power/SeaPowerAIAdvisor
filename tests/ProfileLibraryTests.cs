using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ProfileLibraryTests
{
    internal static void Run(byte[] png, Action<string, Action> check)
    {
        check("Registered advisers retain independent edits and stable identities through selection, saving and deletion", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png, "作戦アドバイザー");
            var original = AdvisorProfile.Load(directory);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            var draft = new AdvisorProfileDraft(original);
            draft.Personality = "Default style";
            var first = draft.Add("Alice", "情報将校"); draft.Personality = "First style";
            var second = draft.Add("Bob", "航空参謀"); draft.Personality = "Second style";
            draft.Select(first); draft.CustomName = "Alice renamed"; draft.CustomRole = "";
            Require(draft.Personality == "First style", "Switching reused another adviser's personality");
            draft.Select(AdvisorProfile.DefaultId);
            Require(draft.Personality == "Default style" && original.Advisers.Count == 1 &&
                File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before), "Draft changes escaped before Save");
            Require(new AdvisorProfileDraft(original).Advisers.Count == 1, "Discarding edits retained registrations");
            draft.Select(second); draft.Save(directory);
            var saved = AdvisorProfile.Load(directory);
            Require(saved.ActiveId == second && saved.DisplayName == "Bob" && saved.Personality == "Second style" &&
                saved.Advisers.Single(a => a.Id == first).Name == "Alice renamed" &&
                saved.Advisers.Single(a => a.Id == first).Role == "" && saved.DefaultPersonality == "Default style",
                "Saved registrations lost identity, name, role or personality");
            draft = new AdvisorProfileDraft(saved); draft.Remove(second);
            Require(draft.UseDefault && draft.Personality == "Default style", "Deleting the selected adviser did not return to default");
            foreach (var adviser in draft.Advisers.ToArray()) draft.Remove(adviser.Id);
            Throws("profile_invalid", () => draft.Remove(AdvisorProfile.DefaultId));
            draft.Save(directory); saved = AdvisorProfile.Load(directory);
            Require(saved.UseDefault && saved.Advisers.Count == 0 && saved.DisplayName == AdvisorProfile.DefaultName,
                "Deleting the last custom adviser damaged the default identity");
            draft = new AdvisorProfileDraft(saved); var next = draft.Add("New", "Operations adviser");
            Require(next != first && next != second && draft.Build().ActiveId == next, "An empty library cannot register a new adviser");
        }));
        check("Legacy custom profiles migrate without losing saved choices or silently repairing invalid registrations", () => InTemp(directory =>
        {
            var path = AdvisorProfile.SettingsPath(directory);
            var legacy = new JObject { ["mode"] = "custom", ["display_name"] = "Existing", ["portrait_file"] = "existing.jpg",
                ["default_personality"] = "Default", ["custom_personality"] = "Personal", ["custom_role"] = "" };
            File.WriteAllText(path, legacy.ToString()); var before = File.ReadAllBytes(path);
            var loaded = AdvisorProfile.Load(directory);
            AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Unexpected image reset"), "New language role");
            Require(File.ReadAllBytes(path).SequenceEqual(before) && loaded.Advisers.Count == 1 && loaded.DisplayName == "Existing" &&
                loaded.Personality == "Personal" && loaded.DefaultPersonality == "Default" && loaded.CustomRole == "",
                "Legacy read changed user data or failed to retain a custom profile");
            var draft = new AdvisorProfileDraft(loaded); draft.Add("Second", "Role"); draft.Save(directory);
            var upgraded = AdvisorProfile.Load(directory);
            Require(upgraded.Advisers.Single(a => a.Id == loaded.ActiveId).PortraitFile == "existing.jpg" &&
                upgraded.Advisers.Single(a => a.Id == loaded.ActiveId).Personality == "Personal", "Saving a library discarded the legacy adviser");
            var data = JObject.Parse(File.ReadAllText(path));
            var duplicate = (JObject)data.DeepClone();
            ((JArray)duplicate["custom_advisors"]!).Add(data["custom_advisors"]![0]!.DeepClone());
            Throws("profile_invalid", () => AdvisorProfile.Parse(duplicate.ToString()));
            var missing = (JObject)data.DeepClone(); missing["selected_custom_id"] = "missing";
            Throws("profile_invalid", () => AdvisorProfile.Parse(missing.ToString()));
            Require(File.ReadAllText(path) == data.ToString() + Environment.NewLine, "Rejecting invalid registrations rewrote saved data");
        }));
        check("Multiple portrait previews stay local until Save and image cleanup respects every remaining registration", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var original = AdvisorProfile.Load(directory); var draft = new AdvisorProfileDraft(original);
            var first = draft.Add("First", "Role"); draft.SelectPortrait(png);
            var second = draft.Add("Second", "Role"); draft.SelectPortrait(png);
            draft.Select(first);
            Require(draft.ReadActivePortrait(directory, () => throw new InvalidOperationException()).SequenceEqual(png) &&
                Directory.GetFiles(directory, "portrait-import-*").Length == 0, "Preview saved a file or forgot another adviser's pending image");
            var saved = draft.Save(directory);
            Require(saved.Advisers.Count == 3 && Directory.GetFiles(directory, "portrait-import-*").Length == 2,
                "Save did not store every selected portrait");
            foreach (var id in new[] { first, second })
            {
                var view = new AdvisorProfileDraft(AdvisorProfile.Load(directory)); view.Select(id);
                Require(view.ReadActivePortrait(directory, () => throw new InvalidOperationException()).SequenceEqual(png), "Saved portrait depends on another profile's selection");
            }
            // External profile edits may intentionally share one imported image.
            var sharedFile = saved.Advisers.Single(a => a.Id == first).PortraitFile;
            var shared = saved.WithPortrait(second, sharedFile);
            saved.DeleteUnusedPortraits(directory, shared, _ => throw new InvalidOperationException("Cleanup failed"));
            draft = new AdvisorProfileDraft(shared); draft.Remove(first); var oneLeft = draft.Save(directory);
            shared.DeleteUnusedPortraits(directory, oneLeft, _ => throw new InvalidOperationException("Cleanup failed"));
            Require(File.Exists(Path.Combine(directory, sharedFile)), "Deleting one registration removed another's shared portrait");
            draft.Remove(second); var noneLeft = draft.Save(directory);
            oneLeft.DeleteUnusedPortraits(directory, noneLeft, _ => throw new InvalidOperationException("Cleanup failed"));
            Require(!File.Exists(Path.Combine(directory, sharedFile)) && File.Exists(Path.Combine(directory, "portrait.png")),
                "Unused imported images remained or default/source images were removed");
        }));
        check("Failed multi-adviser saves preserve saved identities and discard newly created portrait copies", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var path = AdvisorProfile.SettingsPath(directory); var before = File.ReadAllBytes(path);
            var draft = new AdvisorProfileDraft(AdvisorProfile.Load(directory));
            draft.Add("First", ""); draft.SelectPortrait(png); draft.Add("Second", ""); draft.SelectPortrait(png);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws("profile_write_failed", () => draft.Save(directory));
            Require(File.ReadAllBytes(path).SequenceEqual(before) && Directory.GetFiles(directory, "portrait-import-*").Length == 0,
                "A failed multi-adviser save published partial settings or left new images");
        }));
    }
    private static void InTemp(Action<string> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); try { run(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Throws(string code, Action action)
    { try { action(); } catch (AdvisorProfileException error) { Require(error.Code == code, "Wrong profile error"); return; } throw new InvalidOperationException("Expected " + code); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
