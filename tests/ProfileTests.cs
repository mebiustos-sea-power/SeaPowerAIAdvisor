using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Mono.Cecil;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ProfileTests
{
    internal static void Run(string advisorPath, Action<string, Action> check)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(advisorPath);
        var resource = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.DefaultPortrait.png");
        var png = resource.GetResourceData();
        ProfileLibraryTests.Run(png, check);
        var jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDxSAC4hWWYeZI3V35J/E0UUV9fgv8Adqf+FfkjGp8b9T//2Q==");
        check("First profile setup selects the packaged default identity and preserves a custom portrait slot", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var profile = AdvisorProfile.Load(directory);
            Require(profile.UseDefault && profile.DisplayName == "Michael Reed" && profile.PortraitFile == "portrait.png" &&
                profile.Personality == "Calm, professional and courteous." && profile.CustomPersonality == "",
                "Initial user profile or default adviser style is incorrect");
            Require(profile.ReadPortrait(directory).SequenceEqual(png), "Bundled image was modified or not installed");
        }));
        check("Custom role initializes once from the game language for new and older profiles", () =>
        {
            foreach (var language in new[] { "ja", "en", "fr" }) InTemp(directory =>
            {
                var role = AdvisorText.Load(language)["advisor.role"];
                AdvisorProfile.EnsureCreated(directory, () => png, role);
                var initial = AdvisorProfile.Load(directory);
                Require(initial.CustomRole == role && initial.DefaultPersonality == "Calm, professional and courteous.",
                    "The initial role was not localized or the default adviser style depended on the game language");
                var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
                var other = AdvisorText.Load(language == "ja" ? "en" : "ja")["advisor.role"];
                AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Existing portrait replaced"), other);
                Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before),
                    "Changing the game language replaced the saved role");
                var legacy = new JObject { ["display_name"] = "Existing advisor", ["portrait_file"] = "portrait.png", ["personality"] = "Existing style" };
                File.WriteAllText(AdvisorProfile.SettingsPath(directory), legacy.ToString());
                AdvisorProfile.EnsureCreated(directory, () => png, role);
                var upgraded = AdvisorProfile.Load(directory);
                Require(upgraded.CustomRole == role && upgraded.DisplayName == "Existing advisor" && !upgraded.UseDefault &&
                    upgraded.DefaultPersonality == "Existing style" && upgraded.CustomPersonality == "Existing style",
                    "Initializing an older profile changed its identity or style");
                before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
                AdvisorProfile.EnsureCreated(directory, () => png, other);
                Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before), "The old profile was initialized again");
            });
        });
        check("Custom role edits survive mode changes and portrait imports while an empty saved role stays empty", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png, AdvisorText.Load("ja")["advisor.role"]);
            var applied = AdvisorProfile.Load(directory);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            var draft = new AdvisorProfileDraft(applied) { UseDefault = false, CustomRole = "情報将校" };
            draft.UseDefault = true;
            Require(draft.CustomRole == "情報将校" && applied.CustomRole != draft.CustomRole &&
                File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before), "Unsaved role edits changed the active profile");
            Require(new AdvisorProfileDraft(applied).CustomRole == applied.CustomRole, "Discarding edits retained an unsaved role");
            draft.SelectPortrait(png);
            draft.Save(directory);
            var loaded = AdvisorProfile.Load(directory);
            Require(loaded.UseDefault && loaded.CustomRole == "情報将校" && loaded.ReadPortrait(directory).SequenceEqual(png),
                "Saving the default identity or importing a portrait lost the custom role");
            draft = new AdvisorProfileDraft(loaded) { UseDefault = false, CustomRole = "" };
            draft.Save(directory);
            AdvisorProfile.EnsureCreated(directory, () => png, AdvisorText.Load("en")["advisor.role"]);
            Require(AdvisorProfile.Load(directory).CustomRole == "", "An explicitly empty role was filled in again");
            before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            foreach (var invalid in new[] { new string('x', AdvisorProfile.MaxRoleLength + 1), "invalid\nrole" })
            {
                draft.CustomRole = invalid;
                Throws("role_invalid", () => draft.Save(directory));
            }
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before), "An invalid role changed saved settings");
        }));
        check("Default and custom identities retain independent personality edits across switches and saves", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var defaultStyle = "落ち着いて、率直に説明する。";
            var customStyle = "親しみのある口調。\n必要なら詳しく説明する。";
            var custom = new AdvisorProfile("カスタム士官", "custom.png", false, defaultStyle, customStyle);
            File.WriteAllBytes(custom.ResolvePortrait(directory), png);
            custom.Save(directory);
            var draft = new AdvisorProfileDraft(custom);
            draft.Personality = customStyle += " 選択肢も示す。";
            draft.UseDefault = true;
            Require(draft.Personality == defaultStyle, "Switching to default reused the custom personality");
            draft.Personality = defaultStyle += " 不明点は明示する。";
            draft.UseDefault = false;
            Require(draft.Personality == customStyle, "Switching back discarded unsaved custom edits");
            draft.UseDefault = true;
            draft.Save(directory);
            var defaults = AdvisorProfile.Load(directory);
            Require(defaults.UseDefault && defaults.DisplayName == AdvisorProfile.DefaultName &&
                defaults.CustomName == custom.CustomName && defaults.PortraitFile == custom.PortraitFile &&
                defaults.Personality == defaultStyle && defaults.CustomPersonality == customStyle,
                "Saving default mode lost an identity or either personality");
            File.Delete(custom.ResolvePortrait(directory));
            Require(defaults.ReadActivePortrait(directory, () => png).SequenceEqual(png),
                "The default portrait depends on a custom file");
            var restored = new AdvisorProfileDraft(defaults) { UseDefault = false };
            Throws("portrait_missing", () => restored.ReadActivePortrait(directory, () => png));
            File.WriteAllBytes(custom.ResolvePortrait(directory), png);
            restored.Personality = "";
            restored.Save(directory);
            var saved = AdvisorProfile.Load(directory);
            Require(!saved.UseDefault && saved.DisplayName == custom.CustomName && saved.Personality == "" &&
                saved.DefaultPersonality == defaultStyle, "Clearing custom style also erased default style");
        }));
        check("Legacy shared personality is preserved in both modes without overwriting explicit empty values", () => InTemp(directory =>
        {
            Directory.CreateDirectory(directory);
            foreach (var useDefault in new[] { false, true })
            {
                var legacy = new JObject
                {
                    ["mode"] = useDefault ? "default" : "custom", ["display_name"] = "既存の士官",
                    ["portrait_file"] = "portrait.png", ["personality"] = "穏やかに話す。\n詳しく説明する。"
                };
                var path = AdvisorProfile.SettingsPath(directory);
                File.WriteAllText(path, legacy.ToString());
                var before = File.ReadAllBytes(path);
                var profile = AdvisorProfile.Load(directory);
                Require(profile.DefaultPersonality == (string?)legacy["personality"] &&
                    profile.CustomPersonality == profile.DefaultPersonality &&
                    File.ReadAllBytes(path).SequenceEqual(before), "Reading legacy settings lost text or rewrote the file");
                var draft = new AdvisorProfileDraft(profile) { UseDefault = !useDefault, Personality = "" };
                draft.Save(directory);
                var saved = AdvisorProfile.Load(directory);
                var original = new AdvisorProfileDraft(saved) { UseDefault = useDefault };
                Require(saved.Personality == "" && original.Personality == profile.Personality,
                    "Legacy migration did not retain separate values after saving");
                legacy["default_personality"] = "";
                var mixed = AdvisorProfile.Parse(legacy.ToString());
                Require(mixed.DefaultPersonality == "" && mixed.CustomPersonality == profile.CustomPersonality,
                    "An explicit empty mode preference was replaced by the legacy value");
            }
        }));
        check("Portrait selection and profile previews do not save until the explicit save action", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var applied = AdvisorProfile.Load(directory);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            var count = Directory.GetFiles(directory).Length;
            var draft = new AdvisorProfileDraft(applied) { UseDefault = false, CustomName = "新しい士官", Personality = "穏やかな口調" };
            draft.SelectPortrait(png);
            Require(draft.ReadActivePortrait(directory, () => throw new InvalidOperationException("Custom preview used defaults")).SequenceEqual(png) &&
                File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before) &&
                Directory.GetFiles(directory).Length == count && applied.Personality != draft.Personality,
                "Preview selection modified active preferences or saved an image");
            draft.UseDefault = true;
            var saved = draft.Save(directory);
            var loaded = AdvisorProfile.Load(directory);
            Require(loaded.UseDefault && loaded.DisplayName == AdvisorProfile.DefaultName &&
                loaded.CustomName == "新しい士官" && loaded.Personality == applied.Personality && loaded.CustomPersonality == "穏やかな口調" &&
                loaded.ReadPortrait(directory).SequenceEqual(png) && saved.PortraitFile != applied.PortraitFile,
                "Saving defaults did not retain the pending custom image and custom style");
            var custom = new AdvisorProfileDraft(loaded) { UseDefault = false };
            Require(custom.CustomName == draft.CustomName && custom.Personality == "穏やかな口調" &&
                custom.ReadActivePortrait(directory, () => throw new InvalidOperationException("Custom image lost")).SequenceEqual(png),
                "The selected custom image or name was lost after saving default mode");
        }));
        check("Repeated setup preserves existing empty and edited styles and portraits without consulting bundled defaults", () =>
        {
            foreach (var useDefault in new[] { false, true })
            foreach (var style in new[] { "", "Existing user style" }) InTemp(directory =>
            {
                AdvisorProfile.EnsureCreated(directory, () => png);
                new AdvisorProfile("変更した名前", "custom.png", useDefault, style, "Custom style", "Saved role").Save(directory);
                File.WriteAllBytes(Path.Combine(directory, "custom.png"), png);
                var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
                AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Existing profile must not be reset"), "Different role");
                var saved = AdvisorProfile.Load(directory);
                Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before) &&
                    saved.DefaultPersonality == style && saved.CustomPersonality == "Custom style" && saved.UseDefault == useDefault &&
                    saved.ReadPortrait(directory).SequenceEqual(png), "Updating the mod reset user preferences");
                var draft = new AdvisorProfileDraft(saved) { UseDefault = true, Personality = "Edited after update" };
                draft.Save(directory);
                Require(AdvisorProfile.Load(directory).DefaultPersonality == "Edited after update", "The default adviser style could no longer be edited");
                draft.Personality = "";
                draft.Save(directory);
                AdvisorProfile.EnsureCreated(directory, () => png, "Different role");
                Require(AdvisorProfile.Load(directory).DefaultPersonality == "", "An explicitly cleared style was filled in again");
            });
        });
        check("Profile names and image paths round-trip through files without changing provider settings", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var credentials = Path.Combine(directory, "gemini.json");
            File.WriteAllText(credentials, "local provider settings");
            var absolute = Path.Combine(directory, "人物 画像.png");
            File.WriteAllBytes(absolute, png);
            var desired = new AdvisorProfile("Reed / リード <>&\"𠮷", absolute);
            desired.Save(directory);
            var actual = AdvisorProfile.Load(directory);
            Require(actual.DisplayName == desired.DisplayName && actual.ResolvePortrait(directory) == absolute &&
                actual.ReadPortrait(directory).SequenceEqual(png), "Unicode or an absolute local path was lost");
            Require(File.ReadAllText(credentials) == "local provider settings", "Profile save touched API settings");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "Profile save left a temporary file");
        }));
        check("External file replacement is read afresh and image reads leave files unlocked", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var first = AdvisorProfile.Load(directory);
            first.ReadPortrait(directory);
            var image = first.ResolvePortrait(directory);
            using (var file = new FileStream(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.WriteAllText(AdvisorProfile.SettingsPath(directory), "{\"display_name\":\"外部変更\",\"portrait_file\":\"portrait.png\"}");
            File.WriteAllBytes(image, new byte[] { 0, 1, 2 });
            Require(AdvisorProfile.Load(directory).DisplayName == "外部変更", "The loaded name was cached");
            Throws("portrait_invalid", () => AdvisorProfile.Load(directory).ReadPortrait(directory));
            File.WriteAllBytes(image, png);
            Require(AdvisorProfile.Load(directory).ReadPortrait(directory).SequenceEqual(png), "A restored image was not re-read");
        }));
        check("JPG and JPEG selections preview without saving and survive source deletion without recompression", () =>
        {
            foreach (var extension in new[] { ".jpg", ".jpeg", ".JPG", ".JPEG" }) InTemp(directory =>
            {
                AdvisorProfile.EnsureCreated(directory, () => png);
                var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
                var original = Path.Combine(directory, "人物 画像" + extension);
                File.WriteAllBytes(original, jpeg);
                var selected = new AdvisorProfile("変更した名前", original);
                var image = selected.ReadPortrait(directory);
                var draft = new AdvisorProfileDraft(AdvisorProfile.Load(directory)) { UseDefault = false, CustomName = "変更した名前" };
                draft.SelectPortrait(image);
                File.Delete(original);
                Require(draft.ReadActivePortrait(directory, () => png).SequenceEqual(jpeg) &&
                    File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before) &&
                    Directory.GetFiles(directory, "portrait-import-*").Length == 0, "JPEG preview saved or lost the selected image");
                var saved = draft.Save(directory);
                var loaded = AdvisorProfile.Load(directory);
                Require(Path.GetExtension(saved.PortraitFile) == ".jpg" && !loaded.UseDefault &&
                    loaded.ReadActivePortrait(directory, () => png).SequenceEqual(jpeg), "JPEG was recompressed, mislabeled or not reloaded");
                var replacement = saved.ImportPortrait(directory, png);
                saved.DeleteImportedPortrait(directory, _ => throw new InvalidOperationException("JPEG cleanup failed"));
                Require(!File.Exists(saved.ResolvePortrait(directory)) && replacement.ReadPortrait(directory).SequenceEqual(png),
                    "Replacing a JPEG left the old imported copy or removed the new PNG");
            });
        });
        check("JPEG metadata is skipped and malformed or oversized headers cannot replace the saved portrait", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            // Marker-like bytes inside APP1 metadata are not a frame header.
            var withMetadata = new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 10, 1, 2, 0xff, 0xc0, 0xff, 0xff, 3, 4 }
                .Concat(jpeg.Skip(2)).ToArray();
            Require(PortraitImage.Extension(withMetadata) == ".jpg", "Valid JPEG metadata was mistaken for image dimensions");
            var frame = Enumerable.Range(0, jpeg.Length - 1).First(i => jpeg[i] == 0xff && jpeg[i + 1] == 0xc0);
            var tooWide = (byte[])jpeg.Clone(); tooWide[frame + 7] = 0x10; tooWide[frame + 8] = 1;
            var zeroHeight = (byte[])jpeg.Clone(); zeroHeight[frame + 5] = 0; zeroHeight[frame + 6] = 0;
            var badLength = (byte[])jpeg.Clone(); badLength[frame + 2] = 0; badLength[frame + 3] = 1;
            var truncatedMetadata = (byte[])withMetadata.Clone(); truncatedMetadata[4] = 0xff; truncatedMetadata[5] = 0xff;
            var progressiveHeader = (byte[])jpeg.Clone(); progressiveHeader[frame + 1] = 0xc2;
            Require(PortraitImage.Extension(progressiveHeader) == ".jpg", "Progressive frame dimensions were not recognized");
            var current = AdvisorProfile.Load(directory);
            foreach (var invalid in new[] { tooWide, zeroHeight, badLength, truncatedMetadata, jpeg.Take(frame).ToArray(),
                new byte[] { 0xff, 0xd8, 0xff }, new byte[] { 0xff, 0xd8, 0xff, 0xda }, new byte[] { 0xff, 0xd8, 0xff, 0xd9 } })
                Throws("portrait_invalid", () => current.ImportPortrait(directory, invalid));
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before) &&
                Directory.GetFiles(directory, "portrait-import-*").Length == 0, "An invalid JPEG changed saved settings or left a new image");
        }));
        check("Imported portraits remain usable after deleting the source and only previous imported copies are removed", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var original = Path.Combine(directory, "選択した人物.png");
            File.WriteAllBytes(original, png);
            var selected = new AdvisorProfile("変更した名前", original);
            var imported = selected.ImportPortrait(directory, selected.ReadPortrait(directory));
            File.Delete(original);
            var loaded = AdvisorProfile.Load(directory);
            Require(loaded.DisplayName == selected.DisplayName && loaded.ReadPortrait(directory).SequenceEqual(png) &&
                loaded.ResolvePortrait(directory) != original, "The profile still depends on the original image");
            var replacement = loaded.ImportPortrait(directory, png);
            loaded.DeleteImportedPortrait(directory, _ => throw new InvalidOperationException("Cleanup unexpectedly failed"));
            var defaults = new AdvisorProfile(AdvisorProfile.DefaultName, AdvisorProfile.DefaultPortraitFile);
            defaults.DeleteImportedPortrait(directory, _ => throw new InvalidOperationException("Custom paths must not be removed"));
            Require(!File.Exists(loaded.ResolvePortrait(directory)) &&
                File.Exists(defaults.ResolvePortrait(directory)) && replacement.ReadPortrait(directory).SequenceEqual(png),
                "Replacing an imported image removed the wrong file");
        }));
        check("A failed profile save removes the new copy while preserving the previously imported image", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var current = AdvisorProfile.Load(directory).ImportPortrait(directory, png);
            var settings = AdvisorProfile.SettingsPath(directory);
            File.Move(settings, settings + ".retained");
            Directory.CreateDirectory(settings);
            Throws("profile_write_failed", () => current.ImportPortrait(directory, png));
            Require(current.ReadPortrait(directory).SequenceEqual(png) &&
                Directory.GetFiles(directory, "portrait-import-*.png").Length == 1 &&
                Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed import changed the active image or left a new copy");
        }));
        check("Missing user images and broken profiles remain explicit errors instead of being reset", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            File.Delete(Path.Combine(directory, "portrait.png"));
            AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Missing user images must not be replaced"));
            Throws("portrait_missing", () => AdvisorProfile.Load(directory).ReadPortrait(directory));
            File.WriteAllText(AdvisorProfile.SettingsPath(directory), "{broken");
            AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Broken profiles must not be reset"));
            Throws("profile_invalid", () => AdvisorProfile.Load(directory));
            File.WriteAllBytes(AdvisorProfile.SettingsPath(directory), new byte[] { 255, 255 });
            Throws("profile_invalid", () => AdvisorProfile.Load(directory));
        }));
        check("Invalid profile data is rejected without exposing its contents in errors", () =>
        {
            foreach (var json in new[] {
                "{}", "{\"display_name\":\"a\",\"display_name\":\"b\",\"portrait_file\":\"portrait.png\"}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"extra\":\"SECRET\"}",
                "{\"display_name\":\"\",\"portrait_file\":\"portrait.png\"}",
                "{\"display_name\":\"a\\nb\",\"portrait_file\":\"portrait.png\"}",
                "{\"display_name\":17,\"portrait_file\":\"portrait.png\"}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"mode\":\"unknown\"}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"personality\":17}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"default_personality\":17}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"custom_personality\":17}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"custom_role\":17}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.png\",\"custom_role\":null}",
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.gif\"}"
            }) Throws("profile_invalid", () => AdvisorProfile.Parse(json));
        });
        check("Overlong personality text is rejected before saving a changed profile", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            foreach (var useDefault in new[] { false, true })
            {
                var draft = new AdvisorProfileDraft(AdvisorProfile.Load(directory))
                    { UseDefault = useDefault, Personality = new string('x', AdvisorProfile.MaxPersonalityLength + 1) };
                draft.UseDefault = !useDefault;
                Throws("personality_invalid", () => draft.Save(directory));
            }
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before),
                "Invalid personality text changed the saved profile");
        }));
        check("Oversized and invalid PNG dimensions are rejected before texture allocation", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var profile = AdvisorProfile.Load(directory); var image = profile.ResolvePortrait(directory);
            var invalid = (byte[])png.Clone();
            invalid[16] = 127;
            File.WriteAllBytes(image, invalid);
            Throws("portrait_invalid", () => profile.ReadPortrait(directory));
            invalid = (byte[])png.Clone(); Array.Clear(invalid, 16, 4);
            File.WriteAllBytes(image, invalid);
            Throws("portrait_invalid", () => profile.ReadPortrait(directory));
            using (var file = new FileStream(image, FileMode.Create)) file.SetLength(AdvisorProfile.MaxPortraitBytes + 1L);
            Throws("portrait_invalid", () => profile.ReadPortrait(directory));
        }));
        check("Missing bundled defaults surface as deployment errors rather than profile fallback", () => InTemp(directory =>
        {
            try { AdvisorProfile.EnsureCreated(directory, () => throw new FileNotFoundException("Missing bundled portrait")); }
            catch (FileNotFoundException) { return; }
            throw new InvalidOperationException("A missing packaged dependency was hidden");
        }));
        check("Settings categories replace the conversation area, isolate form actions and keep API keys masked", () =>
        {
            var view = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml");
            using var stream = view.GetResourceStream(); var root = XDocument.Load(stream).Root!;
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            var settings = Named("SettingsPane"); var home = Named("SettingsHome"); var conversation = Named("ConversationPane");
            Require(settings.Parent == root && conversation.Parent == root && (string?)settings.Attribute("Visibility") == "Collapsed", "Settings do not replace the whole conversation");
            foreach (var button in new[] { "OpenGeneralCategory", "OpenProfileCategory", "OpenTemplatesCategory", "OpenApiCategory", "OpenConsultationCategory", "OpenExtensionsCategory", "OpenInformationCategory", "OpenDeveloperCategory", "OpenWipCategory" })
                Require(Named(button).Ancestors().Contains(home), "A category is inaccessible from the settings home");
            foreach (var control in new[] { "Portrait", "Question", "Transcript", "Send", "Status" })
                Require(Named(control).Ancestors().Contains(conversation), "Conversation controls leak into settings");
            foreach (var control in new[] { "ProfilePreview", "ChoosePortrait", "ProfileName", "ProfilePersonality", "SaveProfileEntry", "CancelProfileEntry" })
                Require(Named(control).Ancestors().Contains(Named("ProfileEditor")), "An advisor edit action is not local to its form");
            foreach (var control in new[] { "ApiProvider", "ApiProviderDescription", "ApiProviderFields", "TestConnection", "ConnectionTestStatus", "SaveApiEntry", "CancelApiEntry" })
                Require(Named(control).Ancestors().Contains(Named("ApiEditor")), "An API edit or test result is not local to its form");
            var extensionTabs = Named("ExtensionsCategory").Elements().ToArray();
            Require(extensionTabs.Length == 2 && extensionTabs.All(tab => tab.Name.LocalName == "TabItem") &&
                Named("CommandList").Ancestors().Contains(extensionTabs[0]) &&
                Named("CommandDetails").Ancestors().Contains(extensionTabs[0]) &&
                Named("AdapterEntries").Ancestors().Contains(extensionTabs[1]) &&
                !Named("EnableUnitOperations").Ancestors().Contains(extensionTabs[1]),
                "Extension tabs mix connection types with command permissions or hide a registered-capability list");
            Require(Named("CommandList").Parent == Named("CommandDetails").Parent &&
                (string?)Named("CommandDetails").Attribute("Visibility") == "Collapsed" &&
                Named("CommandEntries").Ancestors().Contains(Named("CommandList")) &&
                new[] { "CommandPrompt", "CommandDefinition" }.All(id => Named(id).Ancestors().Contains(Named("CommandDetails")) &&
                    (string?)Named(id).Attribute("IsReadOnly") == "True"), "Command browsing overlaps the list or permits editing AI definitions");
            Require(new[] { "EnableUnitOperations", "SaveCommands" }.All(id => Named(id).Ancestors().Contains(Named("CommandList"))) &&
                !Named("EnableUnitOperations").Ancestors().Contains(Named("WipCategory")),
                "Command permission is unavailable on its page or still editable under WIP");
            Require(new[] { "EnableCampaignAdvisor", "SaveWip" }.All(id => Named(id).Ancestors().Contains(Named("WipCategory"))) &&
                !Named("EnableCampaignAdvisor").Ancestors().Contains(Named("GeneralCategory")),
                "Campaign alpha settings or their save action are still placed under General");
            var apiList = Named("ApiConnectionList"); var apiEditor = Named("ApiEditor");
            Require(apiList.Parent == apiEditor.Parent && (string?)apiEditor.Attribute("Visibility") == "Collapsed" &&
                Named("ApiConnectionCards").Ancestors().Contains(apiList) && !Named("ApiConnectionCards").Ancestors().Contains(apiEditor) &&
                Named("AddApiConfiguration").Ancestors().Contains(apiList) &&
                new[] { "DuplicateApiConfiguration", "RemoveApiConfiguration" }.All(id => Named(id).Ancestors().Contains(apiEditor)) &&
                Named("ApiProvider").Name.LocalName == "ComboBox", "The saved-connection list shares editing space or changes provider selection");
            Require(Named("SettingsConfirmation").Parent == root && (string?)Named("SettingsConfirmation").Attribute("Visibility") == "Collapsed" &&
                Named("AcceptConfirmation").Ancestors().Contains(Named("SettingsConfirmation")), "Delete and discard confirmations are not available outside the disabled page");
            Require(Named("DeveloperCategory").Name.LocalName == "TabControl" &&
                Named("PromptFields").Ancestors().Contains(Named("DeveloperCategory")) &&
                Named("ApplyPrompts").Ancestors().Contains(Named("DeveloperCategory")) &&
                !Named("PromptFields").Ancestors().Contains(Named("DeveloperDiagnosticsPane")), "Prompt editing displaces the diagnostic output area");
            var output = Named("DiagnosticOutput"); var diagnostics = Named("DeveloperDiagnosticsPane");
            var rows = diagnostics.Element(diagnostics.Name.Namespace + "Grid.RowDefinitions")!.Elements().ToArray();
            Require(output.Parent == diagnostics && output.Attribute("Height") == null && (string?)rows[int.Parse((string)output.Attribute("Grid.Row")!)].Attribute("Height") == "*", "Diagnostics cannot use the remaining page height");
        });

    }

    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-profile-" + Guid.NewGuid().ToString("N"));
        try { action(directory); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(string code, Action action)
    {
        try { action(); }
        catch (AdvisorProfileException error)
        {
            Require(error.Code == code && !error.ToString().Contains("SECRET", StringComparison.Ordinal), "Wrong or unsafe profile error");
            return;
        }
        throw new InvalidOperationException("Expected profile error: " + code);
    }
}
