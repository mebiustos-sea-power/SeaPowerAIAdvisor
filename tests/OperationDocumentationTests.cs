using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OperationDocumentationTests
{
    internal static readonly AdvisorOperationText Example = new(new Dictionary<string, string>
    { ["en"] = "{\"name\":\"Example\",\"summary\":\"Example command\",\"requirements\":\"Example conditions\"}" });

    internal static void Run(Action<string, Action> check)
    {
        check("Command languages resolve regions and individual missing entries independently of the host UI", () =>
        {
            var files = new Dictionary<string, string>
            {
                ["en"] = "{\"name\":\"English name\",\"summary\":\"English summary\",\"requirements\":\"English conditions\"}",
                ["ja"] = "{\"name\":\"日本語名\"}",
                ["fr"] = "{\"name\":\"Nom français\",\"summary\":\"Résumé\"}",
                ["pt-BR"] = "{\"name\":\"Nome\"}"
            };
            var command = new AdvisorOperationText(files);
            Require(command.For("JA_jp")["name"] == "日本語名" && command.For("ja")["summary"] == "English summary",
                "A region or missing translated field did not resolve correctly");
            var ui = AdvisorText.Load("fr-FR");
            Require(ui.Language == "en" && command.For(ui.RequestedLanguage)["name"] == "Nom français" &&
                command.For("PT_br")["name"] == "Nome" && command.For("de-DE")["name"] == "English name",
                "The host's English fallback hid a command's supported language or English fallback failed");
            files["en"] = "broken after registration";
            Require(command.For("en")["name"] == "English name", "A caller mutated registered command text");
        });
        check("Command registration data requires English and rejects missing fields and malformed optional translations", () =>
        {
            Throws<FileNotFoundException>(() => new AdvisorOperationText(new Dictionary<string, string> { ["ja"] = "{\"name\":\"名前\"}" }));
            Throws<InvalidDataException>(() => new AdvisorOperationText(new Dictionary<string, string> { ["en"] = "{\"name\":\"Name\"}" }));
            var english = "{\"name\":\"Name\",\"summary\":\"Summary\",\"requirements\":\"Conditions\"}";
            foreach (var invalid in new[] { "{broken", "{\"name\":17}", "{\"name\":\"a\",\"name\":\"b\"}", "{\"typo\":\"Unrecognized\"}" })
                Throws<InvalidDataException>(() => new AdvisorOperationText(new Dictionary<string, string> { ["en"] = english, ["fr"] = invalid }));
            var operations = new AdvisorOperations(() => false);
            Throws<InvalidOperationException>(() => operations.Register(new MissingTextOperation()));
            Require(operations.Inspect("en").Count == 0, "A failed registration left an incomplete command");
        });
        check("Standard command translations are embedded per command and preserve the English AI contract", () =>
        {
            var operation = Command(); var en = operation.Documentation.For("en"); var ja = operation.Documentation.For("ja-JP");
            Require(en.Language == "en" && ja.Language == "ja" && en["name"] != ja["name"] &&
                operation.Documentation.For("de")["name"] == en["name"], "Standard command resources or unsupported-language fallback are missing");
            foreach (var text in new[] { en, ja })
                Require(new[] { "name", "summary", "requirements", "proposal_append" }.All(key => !string.IsNullOrWhiteSpace(text[key])) &&
                    text.Format("proposal_summary", "Hobby 1", 2).Contains("Hobby 1", StringComparison.Ordinal), "A standard language cannot render its explanation or approval summary");
            Require((string?)operation.Definition()["description"] == operation.Description &&
                (string?)operation.Definition()["id"] == SonobuoyDropExtension.OperationId &&
                ((JArray)operation.Definition()["parameters"]!["required"]!).Values<string>().SequenceEqual(new[] { "latitude", "longitude", "ammunition" }),
                "UI localization translated identifiers, changed arguments or replaced the actual AI instruction");
        });
        check("Documentation inspection works with commands disabled and never reads candidates, creates proposals or enables execution", () =>
        {
            var operation = Command(); var operations = new AdvisorOperations(() => false); operations.Register(operation);
            var english = operations.Inspect("en").Single(); var japanese = operations.Inspect("ja").Single();
            Require(english.Id == japanese.Id && english.Text["name"] != japanese.Text["name"] &&
                JToken.DeepEquals(english.Definition, japanese.Definition) && (bool?)english.Definition["requires_approval"] == true &&
                (string?)english.Definition["plan_policy"]?["selection"] == "single" && operations.Proposal("docs") == null &&
                ((JArray)operations.Catalog()["operations"]!).Count == 0, "Viewing descriptions changed execution permissions, proposal state or definitions");
            english.Definition["description"] = "mutated view";
            Require((string?)operations.Inspect("en").Single().Definition["description"] == operation.Description, "An inspection mutated later definitions");
            try { operations.Describe(operation.Id); throw new InvalidOperationException("Read-only documentation enabled AI operation access"); }
            catch (AdvisorOperationException error) { Require(error.Code == "unit_operations_disabled", "Wrong disabled-operation error"); }
            operations.Retire();
            try { operations.Inspect("en"); throw new InvalidOperationException("A retired scope remained accessible"); }
            catch (AdvisorOperationException error) { Require(error.Code == "operation_scope_changed", "Wrong retired-scope error"); }
        });
        check("Shared tool documentation is identical to actual tool definitions and does not mutate a consultation", () =>
        {
            var prompts = AdvisorPrompts.Current;
            var shown = AdvisorOperationTools.Definitions(prompts);
            var operations = new AdvisorOperations(() => true); operations.Register(Command());
            using var gateway = new AdvisorOperationGateway(operations);
            using var session = gateway.CreateSession("docs", prompts)!;
            var before = (JObject)session.Tool.DeepClone();
            var actual = new JArray(AdvisorOperationTools.Create(session, prompts).Select(binding => binding.Definition));
            Require(JToken.DeepEquals(shown, actual) && JToken.DeepEquals(before, session.Tool) && session.History.Count == 0 &&
                operations.Proposal("docs")!.Orders.Count == 0, "The displayed tool schemas differ from sent definitions or viewing changed a consultation");
        });
    }
    private static AdvisorRegisteredOperation Command() => SonobuoyFixture.Create(() => throw new InvalidOperationException("Documentation must not inspect live units"));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class MissingTextOperation : IAdvisorOperation
    {
        public string Id => "example.missing";
        public string Description => "Invalid example";
        public AdvisorOperationText Documentation => null!;
        public bool RequiresApproval => true;
        public JObject Definition() => new();
        public JArray Candidates() => throw new InvalidOperationException();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) => throw new InvalidOperationException();
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => throw new InvalidOperationException();
        public JObject Execute(string target, JObject arguments) => throw new InvalidOperationException();
    }
}
