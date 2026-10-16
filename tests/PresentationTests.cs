using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class PresentationTests
{
    internal static void Run(string advisorPath, string campaignPath, Action<string, Action> check)
    {
        check("UI locale selects Japanese regions and falls back to English for other game languages", () =>
        {
            var en = AdvisorText.Load("en");
            var ja = AdvisorText.Load("ja");
            Require(en.Language == "en" && ja.Language == "ja" && en["action.send"] != ja["action.send"], "UI languages are not distinct");
            foreach (var code in new[] { "ja-JP", "JA_jp" })
                Require(AdvisorText.Load(code)["action.send"] == ja["action.send"], "Japanese region was not selected");
            foreach (var code in new[] { "en-US", "fr", "ru", "de-DE", "zh-Hant" })
            {
                var fallback = AdvisorText.Load(code);
                Require(fallback.Language == "en" && en.Entries.All(e => fallback[e.Key] == e.Value), "An unsupported locale did not use English");
            }
        });
        check("A missing translated entry uses English but invalid catalogues and unknown keys fail visibly", () =>
        {
            var files = new Dictionary<string, string> { ["en"] = "{\"send\":\"Send\",\"cancel\":\"Cancel\"}", ["ja"] = "{\"send\":\"送信\"}" };
            var text = AdvisorText.FromJson("ja", files);
            Require(text["send"] == "送信" && text["cancel"] == "Cancel", "Per-entry English fallback failed");
            Throws<KeyNotFoundException>(() => { _ = text["typo"]; });
            Throws<FileNotFoundException>(() => AdvisorText.FromJson("fr", new Dictionary<string, string>()));
            foreach (var broken in new[] { "{broken", "[]", "{}", "{\"send\":17}", "{\"send\":\"a\",\"send\":\"b\"}", "{\"typo\":\"a\"}" })
            {
                files["ja"] = broken;
                Throws<InvalidDataException>(() => AdvisorText.FromJson("ja", files));
            }
            Throws<InvalidOperationException>(() => AdvisorText.Load("not a language"));
        });
        check("Packaged English and Japanese UI resources resolve the actual XAML and all formatted messages", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(advisorPath);
            var resources = assembly.MainModule.Resources.OfType<EmbeddedResource>().ToArray();
            var files = resources.Where(r => r.Name.StartsWith("SeaPowerAIAdvisor.Localization.", StringComparison.Ordinal))
                .ToDictionary(r => r.Name.Substring("SeaPowerAIAdvisor.Localization.".Length).Replace(".json", ""), r =>
                { using var reader = new StreamReader(r.GetResourceStream()); return reader.ReadToEnd(); });
            using var view = resources.Single(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml").GetResourceStream();
            var document = XDocument.Load(view);
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                foreach (var code in new[] { "en", "ja", "fr" })
                {
                    var text = AdvisorText.FromJson(code, files);
                    foreach (var attribute in document.Descendants().Attributes())
                    {
                        var match = Regex.Match(attribute.Value, @"^\{DynamicResource Advisor\.(.+)\}$");
                        if (match.Success) Require(!string.IsNullOrWhiteSpace(text[match.Groups[1].Value]), "An on-screen text resource cannot resolve");
                    }
                    foreach (var entry in text.Entries)
                        _ = text.Format(entry.Key, Enumerable.Range(0, 10).Cast<object>().ToArray());
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        check("Localized diagnostics preserve game names, source reports and unknown observations", () =>
        {
            var fixture = new SituationTests.Fixture(Assembly.LoadFrom(campaignPath));
            SituationTests.Set(fixture.Sighting, "At", DateTime.MinValue);
            var snapshot = QueryTests.Capture(fixture);
            var before = AdvisorInputBuilder.Build(snapshot, "fr", "Actual question");
            foreach (var code in new[] { "en", "ja", "fr" })
            {
                var text = AdvisorText.Load(code);
                var display = SituationFormatter.Format(snapshot, text);
                Require(display.Contains(text["diagnostic.header"], StringComparison.Ordinal), "Diagnostic language was ignored");
                Require(snapshot.Forces.All(f => display.Contains(f.Name, StringComparison.Ordinal)) &&
                    snapshot.Reports.All(r => display.Contains(r.Text, StringComparison.Ordinal)), "Game-provided text was translated or dropped");
                Require(display.Contains(text["diagnostic.ground_unknown"], StringComparison.Ordinal), "Unobserved base defenses became a number");
            }
            Require(AdvisorInputBuilder.Build(snapshot, "fr", "Actual question") == before, "Presentation changed the snapshot or AI input");
        });
        check("Shortcut captions replace internal prompts only in the visible conversation and preserve drafts", () =>
        {
            foreach (var code in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(code);
                foreach (var shortcut in new[] { AdvisorShortcut.NextSteps, AdvisorShortcut.MoreDetail, AdvisorShortcut.KeyRisks })
                {
                    var request = shortcut.Prepare(text[shortcut.LabelKey]);
                    var conversation = new AdvisorConversation();
                    Require(request.Text.All(c => c < 128) && request.Text != request.DisplayText, "Shortcut question is not separate English text");
                    Require(conversation.TryAdd(request, "Answer / 回答", out _), "Shortcut was rejected");
                    var exchange = conversation.Exchanges.Single();
                    var visible = AdvisorTranscript.Render(conversation, text, "Michael Reed", "Empty");
                    var captions = XDocument.Parse(visible).Descendants().Where(e => e.Name.LocalName == "TextBlock")
                        .Select(e => (string?)e.Attribute("Text")).ToArray();
                    Require(exchange.Question == request.Text && captions.Contains(text["speaker.you"]) && captions.Contains(request.DisplayText) &&
                        !visible.Contains(request.Text, StringComparison.Ordinal), "The internal prompt leaked into visible chat or the caption replaced AI history");
                    foreach (var draft in new[] { "", "日本語の下書き\n追加の条件", request.Text })
                        Require(request.DraftAfterSend(draft) == draft, "A shortcut cleared or overwrote the typed draft");
                }
            }
            var manual = new AdvisorQuestion("入力した質問\n<>&");
            var chat = new AdvisorConversation();
            Require(chat.TryAdd(manual, "回答", out _) && chat.Exchanges.Single().DisplayQuestion == manual.Text, "Manual input was replaced by a caption");
            Require(manual.DraftAfterSend(manual.Text) == "" && manual.DraftAfterSend("New draft") == "New draft", "Manual draft completion changed");
        });
        check("Shortcut availability prevents concurrent requests and More detail requires a real answer", () =>
        {
            foreach (var shortcut in new[] { AdvisorShortcut.NextSteps, AdvisorShortcut.MoreDetail, AdvisorShortcut.KeyRisks })
                Require(!shortcut.IsAvailable(true, 0) && !shortcut.IsAvailable(true, 1), "A busy shortcut could start or cancel a consultation");
            Require(AdvisorShortcut.NextSteps.IsAvailable(false, 0) && AdvisorShortcut.KeyRisks.IsAvailable(false, 0), "Initial consultations are unavailable");
            var chat = new AdvisorConversation();
            Require(!AdvisorShortcut.MoreDetail.IsAvailable(false, chat.Exchanges.Count), "More detail has no answer to refer to");
            chat.TryAdd("Question", "Answer", out _);
            Require(AdvisorShortcut.MoreDetail.IsAvailable(false, chat.Exchanges.Count), "More detail did not enable after an answer");
            chat.Clear();
            Require(!AdvisorShortcut.MoreDetail.IsAvailable(false, chat.Exchanges.Count), "Clear left More detail enabled");
        });
    }

    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
