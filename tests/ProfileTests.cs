using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class ProfileTests
{
    internal static void Run(string advisorPath, Action<string, Action> check)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(advisorPath);
        var resource = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.DefaultPortrait.png");
        var png = resource.GetResourceData();
        check("First profile setup creates an editable Michael Reed profile and the bundled portrait", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            var profile = AdvisorProfile.Load(directory);
            Require(profile.DisplayName == "Michael Reed" && profile.PortraitFile == "portrait.png", "Initial user profile is incorrect");
            Require(profile.ReadPortrait(directory).SequenceEqual(png), "Bundled image was modified or not installed");
        }));
        check("Repeated setup preserves user profile and portrait without consulting bundled defaults", () => InTemp(directory =>
        {
            AdvisorProfile.EnsureCreated(directory, () => png);
            new AdvisorProfile("変更した名前", "custom.png").Save(directory);
            File.WriteAllBytes(Path.Combine(directory, "custom.png"), png);
            var before = File.ReadAllBytes(AdvisorProfile.SettingsPath(directory));
            AdvisorProfile.EnsureCreated(directory, () => throw new InvalidOperationException("Existing profile must not be reset"));
            Require(File.ReadAllBytes(AdvisorProfile.SettingsPath(directory)).SequenceEqual(before) &&
                AdvisorProfile.Load(directory).ReadPortrait(directory).SequenceEqual(png), "Updating the mod reset user preferences");
        }));
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
                "{\"display_name\":\"a\",\"portrait_file\":\"portrait.jpg\"}"
            }) Throws("profile_invalid", () => AdvisorProfile.Parse(json));
        });
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
        check("The initial advisor layout keeps diagnostics inside settings and the name below its portrait", () =>
        {
            var view = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml");
            using var stream = view.GetResourceStream(); var root = XDocument.Load(stream).Root!;
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            var settings = Named("SettingsPane");
            Require((string?)settings.Attribute("Visibility") == "Collapsed" &&
                Named("Capture").Ancestors().Contains(settings) && Named("Input").Ancestors().Contains(settings) &&
                !Named("Transcript").Ancestors().Contains(settings), "Diagnostics or conversation start in the wrong pane");
            var name = Named("AdvisorName"); var portrait = Named("Portrait");
            Require(name.Parent == portrait.Ancestors().First(e => e.Name.LocalName == "StackPanel") &&
                name.ElementsBeforeSelf().Any(e => e.DescendantsAndSelf().Contains(portrait)), "The name is not below the portrait");
            var left = name.Parent!;
            foreach (var shortcut in new[] { "NextSteps", "MoreDetail", "KeyRisks" })
                Require(Named(shortcut).Ancestors().Contains(left) &&
                    Named(shortcut).AncestorsAndSelf().Any(e => name.ElementsAfterSelf().Contains(e)),
                    "A quick action is not below the portrait and name");
            Require(left.Parent?.Name.LocalName == "ScrollViewer", "Quick actions can be clipped in a short window");
            Require(Named("Question").Parent == root, "The question is confined to the narrower conversation column");
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
