using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class TemplateTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Text template forms remain unsaved until registered, and individual edits and deletes preserve other entries and settings", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "templates.json"); var store = new AdvisorTextTemplateStore(directory);
            Require(store.Read().Count == 0 && !File.Exists(path), "Opening an empty library wrote settings");
            var first = new AdvisorTextTemplate("one", "対潜網", "指定したユニットで対潜哨戒網を構築して。\n<>&\" 𠮷");
            Require(store.Read().Count == 0, "Creating a form registered it");
            store.SaveEntry(first, true);
            var before = File.ReadAllBytes(path); _ = store.Read();
            var cancelled = new AdvisorTextTemplate(first.Id, "Edited but cancelled", "Unsent message");
            Require(File.ReadAllBytes(path).SequenceEqual(before), "Reading or editing a form implicitly saved it");
            store.SaveEntry(new AdvisorTextTemplate("two", first.Name, "Second message"), true);
            File.WriteAllText(Path.Combine(directory, "options.json"), "unrelated settings");
            var otherStore = new AdvisorTextTemplateStore(directory);
            otherStore.SaveEntry(new AdvisorTextTemplate("two", "Changed independently", "Keep this"), false);
            store.SaveEntry(new AdvisorTextTemplate(first.Id, "Renamed", first.Text), false);
            var reopened = new AdvisorTextTemplateStore(directory).Read();
            Require(reopened.Select(e => e.Id).SequenceEqual(new[] { "one", "two" }) && reopened[0].Text == first.Text &&
                reopened[1].Text == "Keep this", "An edit lost order, Unicode text, identity or an independently saved entry");
            store.Remove(first.Id);
            Require(store.Read().Single().Id == "two" && File.ReadAllText(Path.Combine(directory, "options.json")) == "unrelated settings",
                "Removal affected another entry or another category");
            before = File.ReadAllBytes(path);
            Throws("template_selection_invalid", () => store.Remove("missing"));
            Throws("template_selection_invalid", () => store.SaveEntry(first, false));
            Require(File.ReadAllBytes(path).SequenceEqual(before), "An invalid selection changed the library");
            store.Remove("two"); Require(new AdvisorTextTemplateStore(directory).Read().Count == 0, "Removing the last template was not durable");
        }));
        check("Malformed and unreadable template libraries are reported without replacing data, and rejected forms leave saved entries intact", () => InTemp(directory =>
        {
            var store = new AdvisorTextTemplateStore(directory); var path = Path.Combine(directory, "templates.json");
            var entry = new AdvisorTextTemplate("one", "Valid", "Message");
            foreach (var invalid in new[] { "not JSON", "{}", "{\"schema_version\":2,\"templates\":[]}",
                "{\"schema_version\":1,\"schema_version\":1,\"templates\":[]}",
                "{\"schema_version\":1,\"templates\":[]} trailing",
                "{\"schema_version\":1,\"templates\":[{\"id\":\"one\",\"name\":\"x\",\"text\":\"y\",\"allow_operations\":true}]}",
                "{\"schema_version\":1,\"templates\":[{\"id\":\"one\",\"name\":\"x\",\"text\":\"y\"},{\"id\":\"one\",\"name\":\"x\",\"text\":\"z\"}]}" })
            {
                File.WriteAllText(path, invalid); var before = File.ReadAllBytes(path);
                Throws("templates_invalid", () => store.Read());
                Throws("templates_invalid", () => store.SaveEntry(entry, true));
                Require(File.ReadAllBytes(path).SequenceEqual(before), "A corrupt library was silently replaced");
            }
            File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0xff });
            Throws("templates_invalid", () => store.Read());
            File.Delete(path); store.SaveEntry(entry, true); var valid = File.ReadAllBytes(path);
            Throws("template_name_invalid", () => store.SaveEntry(new AdvisorTextTemplate("bad", "\n", "Message"), true));
            Throws("template_text_invalid", () => store.SaveEntry(new AdvisorTextTemplate("bad", "Name", new string('a', AdvisorConversation.MaxQuestionLength + 1)), true));
            Require(File.ReadAllBytes(path).SequenceEqual(valid), "A rejected form changed a saved library");
            File.Delete(path); Directory.CreateDirectory(path);
            Throws("templates_unreadable", () => store.Read());
            Throws("templates_unreadable", () => store.SaveEntry(entry, true));
            Require(Directory.Exists(path) && !Directory.GetFiles(directory, "*.tmp").Any(), "A failed operation replaced a directory or left temporary files");
        }));
        check("Template preferences preserve existing defaults, round-trip and survive saves from other categories", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "options.json"); var store = new AdvisorOptionsStore(path);
            var defaults = AdvisorOptions.Parse("{}").Templates;
            Require(defaults.ShowSystem && defaults.Insertion == AdvisorTemplateInsertion.Replace &&
                store.Current.Templates.ShowSystem && !File.Exists(path), "Opening unconfigured preferences changed their defaults or wrote a file");
            store.SaveConsultation(9, 2, "Keep my common prompt");
            store.SaveTemplates(new AdvisorTemplateOptions(false, AdvisorTemplateInsertion.Append));
            Require(store.Current.MaxQueryRounds == 9 && store.Current.HistoryExchanges == 2 && store.Current.CommonPrompt == "Keep my common prompt",
                "Saving template preferences changed consultation settings");
            foreach (var save in new Action[] { () => store.EnsureUserName("Commander"),
                () => store.SaveGeneral(AdvisorTextSize.Large, "Admiral"), () => store.SaveCampaign(true),
                () => store.SaveConsultation(8, 3, "Updated prompt"), () => store.SaveInformation(AdvisorInformation.Default),
                () => store.SaveDeveloper(false), () => store.SaveCommands(new[] { "custom.disabled" }) })
            {
                save(); var reloaded = new AdvisorOptionsStore(path).Current.Templates;
                Require(!reloaded.ShowSystem && reloaded.Insertion == AdvisorTemplateInsertion.Append,
                    "Saving another settings category reset the template preferences");
            }
            var saved = File.ReadAllBytes(path);
            foreach (var invalid in new[] { "null", "[]", "{\"show_system\":\"yes\"}", "{\"insertion\":3}",
                "{\"insertion\":\"unknown\"}", "{\"unexpected\":true}" })
            {
                try { AdvisorOptions.Parse("{\"template_options\":" + invalid + "}"); throw new InvalidOperationException("Invalid preferences accepted"); }
                catch (AdvisorOptionsException failure) { Require(failure.Code == "options_invalid", "The settings error was hidden"); }
            }
            Require(File.ReadAllBytes(path).SequenceEqual(saved), "Invalid preferences changed a saved file");
        }));
        check("System templates use localized messages and English fallback without modifying the user library", () => InTemp(directory =>
        {
            var store = new AdvisorTextTemplateStore(directory); var user = new AdvisorTextTemplate("user", "My template", "Keep this message");
            store.SaveEntry(user, true); var before = File.ReadAllBytes(Path.Combine(directory, "templates.json"));
            var english = AdvisorSystemTemplates.Read(AdvisorText.Load("en"));
            var japanese = AdvisorSystemTemplates.Read(AdvisorText.Load("ja"));
            var fallback = AdvisorSystemTemplates.Read(AdvisorText.Load("de"));
            Require(english.Count > 0 && english.Select(e => e.Id).SequenceEqual(japanese.Select(e => e.Id)) &&
                english[0].Name != japanese[0].Name && english[0].Text != japanese[0].Text &&
                fallback[0].Name == english[0].Name && fallback[0].Text == english[0].Text,
                "System messages lost their identity, localization or English fallback");
            Require(File.ReadAllBytes(Path.Combine(directory, "templates.json")).SequenceEqual(before) && store.Read().Single().Text == user.Text,
                "Reading bundled templates changed the editable user library");
        }));
        check("System and user templates replace or append editable messages while retaining badges and ordinary manual-input policies", () => InTemp(directory =>
        {
            var store = new AdvisorTextTemplateStore(directory);
            store.SaveEntry(new AdvisorTextTemplate("patrol", "Patrol", "指定したヘリで対潜哨戒網を構築して。\nまず状況を確認して。"), true);
            var saved = store.Read().Single(); var draft = new AdvisorDraft(); draft.Add(1029, "Hobby 1"); draft.Add(1040, "Hobby 2");
            draft.AddLocation(new SeaPowerAIAdvisor.Api.AdvisorMapCoordinate(26.6, 127.3), "Point {0}");
            foreach (var selected in new[] { saved }.Concat(AdvisorSystemTemplates.Read(AdvisorText.Load("ja"))))
            foreach (var mode in new[] { AdvisorTemplateInsertion.Replace, AdvisorTemplateInsertion.Append })
            {
                var preference = new AdvisorTemplateOptions(insertion: mode);
                var inserted = preference.Insert("My situation", selected.Text);
                Require(inserted == (mode == AdvisorTemplateInsertion.Replace ? selected.Text : "My situation\n" + selected.Text),
                    "Template selection replaced text in append mode or retained text in replacement mode");
                Require(preference.Insert("", selected.Text) == selected.Text &&
                    new AdvisorTemplateOptions(insertion: AdvisorTemplateInsertion.Append).Insert("Existing\n", selected.Text) == "Existing\n" + selected.Text,
                    "Insertion added an unnecessary leading or duplicate line break");
                var edited = inserted + "\n既存予約は維持して。";
                var request = draft.Prepare(edited);
                Require(!request.IsShortcut && request.AllowOperations && request.SituationDelivery == null && request.DisplayBody == edited &&
                    request.Units.Select(u => u.Id).SequenceEqual(new[] { 1029, 1040 }) && request.Locations.Count == 1 &&
                    request.Text.Contains("unit_id: 1029", StringComparison.Ordinal) && request.Text.Contains("unit_id: 1040", StringComparison.Ordinal),
                    "Template input carried shortcut policies, lost edits or omitted unit/location badges");
            }
            Require(store.Read().Single().Text == saved.Text, "Editing the message input modified the saved template");
        }));
    }
    internal static void RunInstalled(string assemblyPath, Action<string, Action> check)
    {
        check("The consultation selector replaces the history frame, keeps the input available and exposes template editing after the adviser category", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var resource = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml");
            using var stream = resource.GetResourceStream(); var view = XDocument.Load(stream).Root!;
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => view.DescendantsAndSelf().Single(e => (string?)e.Attribute(x + "Name") == name);
            var menu = Named("OtherConsultationsPane"); var transcript = Named("Transcript"); var history = Named("ConversationHistory");
            Require(history.Parent == menu.Parent && history.Name.LocalName == "Border" && transcript.Ancestors().Contains(history) &&
                !menu.Ancestors().Contains(history) && !Named("Question").Ancestors().Contains(menu) &&
                !Named("UnitBadgeArea").Ancestors().Contains(menu) && (string?)menu.Attribute("Visibility") == "Collapsed" &&
                Named("OtherConsultations").Name.LocalName == "ToggleButton" && Named("OtherConsultations").Ancestors().Contains(Named("ConversationPane")),
                "The selector shares the history frame, hides input or badges, or loses its toggle navigation");
            var category = Named("TemplatesCategory"); var tabs = category.Elements().ToArray();
            Require(category.Name.LocalName == "TabControl" && tabs.Length == 2 && tabs.All(tab => tab.Name.LocalName == "TabItem"),
                "Template preferences and library editing do not have separate tabs");
            foreach (var name in new[] { "ShowSystemTemplates", "TemplateInsertion", "SaveTemplateOptions" })
                Require(Named(name).Ancestors().Contains(tabs[0]), "A template preference control is mixed into library editing");
            foreach (var name in new[] { "TemplateSelector", "AddTemplate", "EditTemplate", "RemoveTemplate", "TemplateName", "TemplateMessage", "SaveTemplate", "CancelTemplate" })
                Require(Named(name).Ancestors().Contains(tabs[1]), "A template edit control is mixed into general template preferences");
            var buttons = Named("OpenProfileCategory").Parent!.Elements().Where(e => e.Name.LocalName == "Button" && e.Attribute("Grid.Row") != null)
                .OrderBy(e => (int)e.Attribute("Grid.Row")!).ThenBy(e => (int)e.Attribute("Grid.Column")!).Select(e => (string?)e.Attribute(x + "Name")).ToArray();
            Require(Array.IndexOf(buttons, "OpenTemplatesCategory") == Array.IndexOf(buttons, "OpenProfileCategory") + 1,
                "The text templates category does not follow the adviser category");
        });
    }
    private static void Throws(string code, Action action)
    {
        try { action(); } catch (AdvisorTemplateException error) { Require(error.Code == code, "Wrong error: " + error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-templates-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
