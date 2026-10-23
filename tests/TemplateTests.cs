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
        check("A saved template can be edited and sent with existing badges using ordinary manual-input policies", () => InTemp(directory =>
        {
            var store = new AdvisorTextTemplateStore(directory);
            store.SaveEntry(new AdvisorTextTemplate("patrol", "Patrol", "指定したヘリで対潜哨戒網を構築して。\nまず状況を確認して。"), true);
            var selected = store.Read().Single(); var units = new AdvisorUnitDraft(); units.Add(1029, "Hobby 1"); units.Add(1040, "Hobby 2");
            var edited = selected.Text + "\n既存予約は維持して。";
            var request = units.Prepare(edited);
            Require(!request.IsShortcut && request.AllowOperations && request.SituationDelivery == null && request.DisplayBody == edited &&
                request.Units.Select(u => u.Id).SequenceEqual(new[] { 1029, 1040 }) &&
                request.Text.Contains("unit_id: 1029", StringComparison.Ordinal) && request.Text.Contains("unit_id: 1040", StringComparison.Ordinal),
                "Template input carried shortcut policies, lost edits or omitted the target identities");
            Require(store.Read().Single().Text == selected.Text, "Editing the message input modified the saved template");
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
            var category = Named("TemplatesCategory");
            foreach (var name in new[] { "TemplateSelector", "AddTemplate", "EditTemplate", "RemoveTemplate", "TemplateName", "TemplateMessage", "SaveTemplate", "CancelTemplate" })
                Require(Named(name).Ancestors().Contains(category), "A template edit control is outside its settings category");
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
