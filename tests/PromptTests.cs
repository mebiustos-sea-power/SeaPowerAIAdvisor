using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class PromptTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Loaded prompt files remain fixed and reach the actual battle request without further file access", () => InTemp(directory =>
        {
            Prepare(directory);
            Rewrite(directory, "common", value => { value["system"] = "EXTERNAL_COMMON_V1"; value["instructions"]!["task"] = "EXTERNAL_TASK_V1"; });
            Rewrite(directory, "battle", value => value["evidence_policy"] = "EXTERNAL_BATTLE_V1");
            var prompts = AdvisorPrompts.Load(directory);
            Rewrite(directory, "common", value => value["system"] = "EXTERNAL_COMMON_V2");
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            var session = new BattleQuerySession(Battle(), "ja", "Advice", prompts: prompts);
            var input = session.InitialInput();
            input["instructions"]!["task"] = "CONSUMER_MUTATION";
            var fake = new CaptureTransport(body =>
            {
                var system = (string)body["system_instruction"]!;
                Require(system.Contains("EXTERNAL_COMMON_V1", StringComparison.Ordinal) && system.Contains("EXTERNAL_TASK_V1", StringComparison.Ordinal) &&
                    system.Contains("EXTERNAL_BATTLE_V1", StringComparison.Ordinal) && !system.Contains("CONSUMER_MUTATION", StringComparison.Ordinal) &&
                    !system.Contains("EXTERNAL_COMMON_V2", StringComparison.Ordinal), "A reread, hardcoded policy or mutable consumer copy replaced the loaded instructions");
                var sent = InteractionFixture.Initial(body);
                Require((string?)sent["common_prompt"] == "Shared preference" && (string?)sent["reply_language"] == "ja",
                    "The common preference or game reply language did not reach the request");
            });
            var result = new GeminiAdvisor(fake).ConsultAsync(session, AdvisorSettings.Parse("{\"api_key\":\"mock-prompts-key\"}"),
                Array.Empty<ChatExchange>(), CancellationToken.None, commonPrompt: "Shared preference").GetAwaiter().GetResult();
            Require(result.ApiRequests == 1 && fake.Calls == 1, "Loading external instructions caused extra requests");
            Require((string?)new BattleQuerySession(Battle(), "en", "Again", prompts: prompts).InitialInput()["instructions"]!["task"] == "EXTERNAL_TASK_V1",
                "A later consultation required the files again");
        }));
        check("Missing, malformed and incomplete prompt files surface errors instead of using a hidden default", () => InTemp(directory =>
        {
            Prepare(directory); File.Delete(Path.Combine(directory, "battle.json"));
            Fails(directory, "prompts_missing", "battle.json");
            Prepare(directory); File.WriteAllText(Path.Combine(directory, "common.json"), "{");
            Fails(directory, "prompts_invalid", "common.json");
            Prepare(directory); Rewrite(directory, "common", value => ((JObject)value["instructions"]!).Remove("task"));
            Fails(directory, "prompts_invalid", "common.json");
            Prepare(directory); Rewrite(directory, "campaign", value => value["evidence_policy"] = " ");
            Fails(directory, "prompts_invalid", "campaign.json");
        }));
        check("Common preferences default to blank, round-trip multiline text and reject invalid persisted values", () =>
        {
            Require(AdvisorOptions.Parse("{}").CommonPrompt == "", "An existing installation received an unsolicited common preference");
            const string text = "距離は海里で説明する。\nGive reasons. <>&\" 𠮷";
            var saved = AdvisorOptions.Parse(System.Text.Encoding.UTF8.GetString(new AdvisorOptions(commonPrompt: text).Data()));
            Require(saved.CommonPrompt == text && saved.Information.Key == AdvisorInformation.Default.Key && saved.MaxQueryRounds == 5,
                "Shared preferences lost text or changed other settings");
            foreach (var token in new JToken[] { JValue.CreateNull(), new JValue(2), new JValue(new string('x', AdvisorOptions.MaxCommonPromptLength + 1)), new JValue("bad\0text") })
            {
                try { _ = AdvisorOptions.Parse(new JObject { ["common_prompt"] = token }.ToString(Formatting.None)); }
                catch (AdvisorOptionsException error) when (error.Code == "common_prompt_invalid") { continue; }
                throw new InvalidOperationException("Invalid common preferences were silently accepted");
            }
            var boundary = new AdvisorOptions(commonPrompt: new string('a', AdvisorOptions.MaxCommonPromptLength));
            Require(boundary.CommonPrompt.Length == AdvisorOptions.MaxCommonPromptLength && new AdvisorOptions(commonPrompt: "  \n ").CommonPrompt == "",
                "The valid length boundary or explicit blank preference was rejected");
        });
    }
    internal static void RunCampaign(string campaignPath, Action<string, Action> check)
    {
        check("Campaign sessions use their own external policy and aviation semantics", () => InTemp(directory =>
        {
            Prepare(directory);
            Rewrite(directory, "campaign", value => { value["evidence_policy"] = "EXTERNAL_CAMPAIGN"; value["aviation_semantics"] = "EXTERNAL_AVIATION"; });
            Rewrite(directory, "battle", value => value["evidence_policy"] = "OTHER_MODE_ONLY");
            var prompts = AdvisorPrompts.Load(directory);
            var snapshot = QueryTests.Capture(new SituationTests.Fixture(Assembly.LoadFrom(campaignPath)));
            var session = new AdvisorQuerySession(snapshot, "fr", "Question", prompts: prompts);
            var input = session.InitialInput();
            Require((string?)input["instructions"]!["evidence_policy"] == "EXTERNAL_CAMPAIGN" && (string?)input["aviation_semantics"] == "EXTERNAL_AVIATION" &&
                !input.ToString().Contains("OTHER_MODE_ONLY", StringComparison.Ordinal), "Campaign policy was hardcoded or mixed with battle instructions");
        }));
    }
    private static void Fails(string directory, string code, string file)
    {
        try { _ = AdvisorPrompts.Load(directory); }
        catch (AdvisorPromptException error) when (error.Code == code && error.FileName == file)
        {
            foreach (var language in new[] { "en", "ja" })
                Require(AdvisorNotice.Prompts(error).Message(AdvisorText.Load(language)).Contains(code, StringComparison.Ordinal), "A prompt failure could not be displayed");
            return;
        }
        throw new InvalidOperationException("Expected an explicit prompt-file error: " + file);
    }
    private static BattleSnapshot Battle() => new(new JObject
    {
        ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission",
        ["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
        ["public_objectives"] = new JArray(), ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(),
        ["unit_capabilities"] = new JArray(), ["contact_threat_references"] = new JArray(), ["weapon_references"] = new JArray(),
        ["published_reports"] = new JArray(), ["mission_briefing"] = "Briefing"
    });
    private static void Prepare(string directory)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Prompts"), "*.json"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
    }
    private static void Rewrite(string directory, string name, Action<JObject> change)
    {
        var path = Path.Combine(directory, name + ".json"); var data = JObject.Parse(File.ReadAllText(path)); change(data);
        File.WriteAllText(path, data.ToString(Formatting.Indented));
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-prompts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private sealed class CaptureTransport : IGeminiTransport
    {
        private readonly Action<JObject> inspect;
        internal int Calls;
        internal CaptureTransport(Action<JObject> inspect) { this.inspect = inspect; }
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            Calls++; inspect(body);
            return Task.FromResult(InteractionFixture.Reply(new JObject { ["text"] = "Answer" }));
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
