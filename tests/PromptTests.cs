using System;
using System.Collections.Generic;
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
            Rewrite(directory, "common", value =>
            {
                value["situation_tool_description"] = "EXTERNAL_READ_TOOL";
                value["system"] = "EXTERNAL_COMMON_V1"; value["instructions"]!["task"] = "EXTERNAL_TASK_V1";
                value["instructions"]!["advice_policy"] = "EXTERNAL_ADVICE_POLICY";
                value["instructions"]!["format"] = "EXTERNAL_REPLY_FORMAT";
            });
            Rewrite(directory, "battle", value =>
            {
                value["evidence_policy"] = "EXTERNAL_BATTLE_V1";
                value["coordinate_links"] = "EXTERNAL_COORDINATE_LINKS";
                value["tool_description"] = "EXTERNAL_BATTLE_TOOL";
                value["catalog_observed_contacts"] = "EXTERNAL_CONTACT_DESCRIPTION";
                value["error_wrong_consultation"] = "EXTERNAL_SESSION_ERROR";
            });
            var prompts = AdvisorPrompts.Load(directory);
            Rewrite(directory, "common", value => value["system"] = "EXTERNAL_COMMON_V2");
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            var session = new BattleQuerySession(Battle(), "ja", "Advice", prompts: prompts);
            var input = session.InitialInput();
            input["instructions"]!["task"] = "CONSUMER_MUTATION";
            var fake = new CaptureTransport(body =>
            {
                var system = (string)body["system_instruction"]!;
                Require(system.Contains("EXTERNAL_COMMON_V1", StringComparison.Ordinal) && system.Contains("EXTERNAL_TASK_V1", StringComparison.Ordinal) && system.Contains("EXTERNAL_REPLY_FORMAT", StringComparison.Ordinal) &&
                    system.Contains("EXTERNAL_ADVICE_POLICY", StringComparison.Ordinal) && system.Contains("EXTERNAL_BATTLE_V1", StringComparison.Ordinal) &&
                    system.Contains("EXTERNAL_COORDINATE_LINKS", StringComparison.Ordinal) &&
                    !system.Contains("CONSUMER_MUTATION", StringComparison.Ordinal) &&
                    !system.Contains("EXTERNAL_COMMON_V2", StringComparison.Ordinal), "A reread, hardcoded policy or mutable consumer copy replaced the loaded instructions");
                Require((string?)body["tools"]![0]!["description"] == "EXTERNAL_READ_TOOL", "The tool description ignored its external resource");
                var sent = InteractionFixture.Initial(body);
                Require((string?)sent["knowledge_catalog"]!["observed_contacts"]!["description"] == "EXTERNAL_CONTACT_DESCRIPTION",
                    "The knowledge catalogue ignored its external resource");
                Require((string?)sent["common_prompt"] == "Shared preference" && (string?)sent["reply_language"] == "ja",
                    "The common preference or game reply language did not reach the request");
            });
            var result = new GeminiFixture(fake).ConsultAsync(session, AdvisorSettings.Parse("{\"api_key\":\"mock-prompts-key\"}"),
                Array.Empty<ChatExchange>(), CancellationToken.None, commonPrompt: "Shared preference").GetAwaiter().GetResult();
            Require(result.ApiRequests == 1 && fake.Calls == 1, "Loading external instructions caused extra requests");
            var rejected = session.Execute(new JObject { ["consultation_id"] = "another-session", ["requests"] = new JArray() });
            Require((string?)rejected["error"]!["code"] == "wrong_consultation" &&
                (string?)rejected["error"]!["message"] == "EXTERNAL_SESSION_ERROR", "Validation bypassed the external error text or changed the rejection");
            Require((string?)new BattleQuerySession(Battle(), "en", "Again", prompts: prompts).InitialInput()["instructions"]!["task"] == "EXTERNAL_TASK_V1",
                "A later consultation required the files again");
        }));
        check("Plain consultations suppress saved style and shared preferences while retaining temporary system instructions", () => InTemp(directory =>
        {
            Prepare(directory);
            var original = AdvisorPrompts.Load(directory); var runtime = new AdvisorPromptOverrides(original);
            var edits = original.Fields.ToDictionary(f => f.Id, f => f.Value);
            edits["common.json:system"] = "TEMPORARY_SYSTEM_FOR_COMPARISON";
            runtime.Apply(edits);
            foreach (var useDefault in new[] { false, true })
            foreach (var plain in new[] { false, true })
            {
                var saved = new AdvisorProfile("Selected custom", "portrait.png", useDefault,
                    "Saved default personality", "Saved custom personality");
                var run = new AdvisorLocalRun(plainAdvisor: plain);
                var effective = run.ProfileFor(saved)!;
                var history = new[] { new ChatExchange("Earlier question", "Earlier answer") };
                const string configuredCommon = "Saved shared instruction";
                var session = new BattleQuerySession(Battle(), "ja", "Test question", prompts: runtime.Current);
                var expectedSystem = runtime.Current.SystemInstruction(session.InitialInput()["instructions"]!);
                var transport = new CaptureTransport(body =>
                {
                    var input = InteractionFixture.Initial(body);
                    Require((string?)input["advisor_preferences"]!["name"] == (plain ? AdvisorProfile.DefaultName : saved.DisplayName) &&
                        (string?)input["advisor_preferences"]!["personality_and_tone"] == (plain ? "" : saved.Personality) &&
                        (string?)input["common_prompt"] == (plain ? "" : configuredCommon),
                        "Saved style or shared instructions leaked into a plain request, or normal preferences were erased");
                    Require((string?)body["system_instruction"] == expectedSystem &&
                        (string?)input["reply_language"] == "ja" && (string?)input["user_name"] == "Commander" &&
                        input["prior_dialogue"]!.Count() == 1 && (string?)input["question"] == "Test question",
                        "Plain mode reset temporary system instructions, history, language or other consultation inputs");
                });
                _ = new GeminiFixture(transport).ConsultAsync(session, AdvisorSettings.Parse("{\"api_key\":\"mock-plain-key\"}"),
                    history, CancellationToken.None, profile: effective, commonPrompt: run.CommonPromptFor(configuredCommon), userName: "Commander").GetAwaiter().GetResult();
                Require(transport.Calls == 1 && saved.DefaultPersonality == "Saved default personality" &&
                    saved.CustomPersonality == "Saved custom personality" && saved.UseDefault == useDefault &&
                    new AdvisorLocalRun().ProfileFor(saved) == saved && new AdvisorLocalRun().CommonPromptFor(configuredCommon) == configuredCommon,
                    "Plain mode changed saved preferences or contaminated the next normal consultation");
            }
        }));
        check("Missing, malformed and incomplete prompt files surface errors instead of using a hidden default", () => InTemp(directory =>
        {
            Prepare(directory); File.Delete(Path.Combine(directory, "battle.json"));
            Fails(directory, "prompts_missing", "battle.json");
            Prepare(directory); File.WriteAllText(Path.Combine(directory, "common.json"), "{");
            Fails(directory, "prompts_invalid", "common.json");
            foreach (var field in new[] { "task", "advice_policy" })
            {
                Prepare(directory); Rewrite(directory, "common", value => ((JObject)value["instructions"]!).Remove(field));
                Fails(directory, "prompts_invalid", "common.json");
            }
            Prepare(directory); Rewrite(directory, "campaign", value => value["evidence_policy"] = " ");
            Fails(directory, "prompts_invalid", "campaign.json");
            Prepare(directory); File.Delete(Path.Combine(directory, "requests.json"));
            Fails(directory, "prompts_missing", "requests.json");
            Prepare(directory); Rewrite(directory, "requests", value => value.Remove("key_risks"));
            Fails(directory, "prompts_invalid", "requests.json");
        }));
        check("Temporary edits affect new consultations while an active multi-request consultation retains its startup snapshot", () => InTemp(directory =>
        {
            Prepare(directory);
            var before = Directory.GetFiles(directory).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
            var original = AdvisorPrompts.Load(directory); var runtime = new AdvisorPromptOverrides(original);
            var edits = original.Fields.ToDictionary(f => f.Id, f => "TEMPORARY_" + f.Id);
            var active = new BattleQuerySession(Battle(), "ja", "Advice", prompts: runtime.Current);
            var expected = original.SystemInstruction(active.InitialInput()["instructions"]!);
            var transport = new RoundTransport((body, call) =>
            {
                Require((string?)body["system_instruction"] == expected, "An active request switched prompts during retrieval");
                if (call == 1)
                {
                    runtime.Apply(edits);
                    return InteractionFixture.Reply(new JObject { ["functionCall"] = new JObject
                    {
                        ["id"] = "prompt-round", ["name"] = "query_situation",
                        ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) }
                    } });
                }
                Require((string?)InteractionFixture.Result(body)["status"] == "ok", "The prompt update interrupted information retrieval");
                return InteractionFixture.Reply(new JObject { ["text"] = "Answer" });
            });
            var result = new GeminiFixture(transport).ConsultAsync(active, AdvisorSettings.Parse("{\"api_key\":\"mock-prompts-key\"}"),
                Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 2 && result.QueryRounds == 1 && runtime.HasOverrides &&
                runtime.Current.Fields.All(f => f.Value == edits[f.Id]), "Temporary edits were lost or changed the consultation rounds");
            var next = new BattleQuerySession(Battle(), "ja", "Again", prompts: runtime.Current);
            var nextSystem = runtime.Current.SystemInstruction(next.InitialInput()["instructions"]!);
            var nextTransport = new CaptureTransport(body => Require((string?)body["system_instruction"] == nextSystem &&
                nextSystem.Contains(edits["common.json:system"], StringComparison.Ordinal) &&
                nextSystem.Contains(edits["common.json:preferences_policy"], StringComparison.Ordinal) &&
                nextSystem.Contains(edits["battle.json:evidence_policy"], StringComparison.Ordinal), "The next actual request ignored the applied draft"));
            _ = new GeminiFixture(nextTransport).ConsultAsync(next, AdvisorSettings.Parse("{\"api_key\":\"mock-prompts-key\"}"),
                Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(before.All(file => File.ReadAllBytes(Path.Combine(directory, file.Key!)).SequenceEqual(file.Value)), "Applying edits wrote a prompt file");
            var restarted = new AdvisorPromptOverrides(AdvisorPrompts.Load(directory));
            Require(!restarted.HasOverrides && restarted.Current.SystemInstruction(active.InitialInput()["instructions"]!) == expected,
                "A new runtime inherited temporary edits from a previous runtime");
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            runtime.Reset();
            Require(!runtime.HasOverrides && ReferenceEquals(runtime.Current, original) &&
                next.Prompts.SystemInstruction(next.InitialInput()["instructions"]!) == nextSystem,
                "Reset reread files or mutated a consultation already holding temporary instructions");
        }));
        check("Unapplied and invalid drafts leave active instructions intact and blank temporary instructions can be tested", () => InTemp(directory =>
        {
            Prepare(directory); var original = AdvisorPrompts.Load(directory); var runtime = new AdvisorPromptOverrides(original);
            var edits = original.Fields.ToDictionary(f => f.Id, f => f.Value);
            edits["common.json:instructions.knowledge"] = "";
            Require(!runtime.HasOverrides && ReferenceEquals(runtime.Current, original), "Typing in a draft immediately changed active instructions");
            runtime.Apply(edits); var applied = runtime.Current;
            Require((string?)applied.Instructions(true)["knowledge"] == "" &&
                !string.IsNullOrEmpty((string?)original.Instructions(true)["knowledge"]), "Blanking a temporary instruction was rejected or changed the original");
            foreach (var change in new Action<Dictionary<string, string>>[]
            {
                value => value.Remove("common.json:system"),
                value => { value.Remove("common.json:system"); value["common.json:unexpected"] = "New structure"; },
                value => value["common.json:system"] = null!,
                value => value["common.json:system"] = new string('a', 65_537),
                value => value["common.json:system"] = new string((char)0xd800, 1)
            })
            {
                var invalid = new Dictionary<string, string>(edits); change(invalid);
                try { runtime.Apply(invalid); throw new InvalidOperationException("An invalid edit was accepted"); }
                catch (AdvisorPromptException error) when (error.Code == "prompts_invalid") { }
                Require(ReferenceEquals(runtime.Current, applied), "A rejected draft partially changed active instructions");
            }
            edits["common.json:instructions.knowledge"] = original.Fields.Single(f => f.Id == "common.json:instructions.knowledge").Value;
            runtime.Apply(edits);
            Require(!runtime.HasOverrides && ReferenceEquals(runtime.Current, original), "Applying startup text left an artificial override active");
        }));
        check("External shortcut requests reach Gemini and history unchanged, and resetting restores the startup questions", () => InTemp(directory =>
        {
            Prepare(directory); var original = AdvisorPrompts.Load(directory); var runtime = new AdvisorPromptOverrides(original);
            var edits = original.Fields.ToDictionary(f => f.Id, f => f.Value);
            foreach (var key in new[] { "next_steps", "key_risks", "radar_operations", "more_detail", "own_units", "contacts" }) edits["requests.json:" + key] = "EDITED_REQUEST_" + key;
            edits["requests.json:connection_test"] = "EDITED_CONNECTION_CHECK";
            edits["requests.json:connection_tool_test"] = "EDITED_TOOL_CONNECTION_CHECK";
            var oldQuestion = AdvisorShortcut.KeyRisks.Prepare("主なリスク", original);
            runtime.Apply(edits);
            var chat = new AdvisorConversation();
            foreach (var pair in new[] { (AdvisorShortcut.NextSteps, "next_steps"), (AdvisorShortcut.KeyRisks, "key_risks"), (AdvisorShortcut.RadarOperations, "radar_operations"), (AdvisorShortcut.MoreDetail, "more_detail"), (AdvisorShortcut.OwnUnits, "own_units"), (AdvisorShortcut.Contacts, "contacts") })
            {
                var question = pair.Item1.Prepare("Visible caption", runtime.Current);
                var transport = new CaptureTransport(body => Require((string?)InteractionFixture.Initial(body)["question"] == "EDITED_REQUEST_" + pair.Item2,
                    "An edited shortcut did not reach the real request input"));
                var session = new BattleQuerySession(Battle(), "ja", question.Text, prompts: runtime.Current);
                _ = new GeminiFixture(transport).ConsultAsync(session, AdvisorSettings.Parse("{\"api_key\":\"mock-prompts-key\"}"),
                    Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
                Require(question.DisplayText == "Visible caption" && transport.Calls == 1, "The hidden question changed its visible caption or added API calls");
                Require(chat.TryAdd(question, "Answer", out _), "An edited shortcut could not be stored in conversation history");
            }
            Require(AdvisorPrompts.ConnectionTestRequest(runtime.Current) == "EDITED_CONNECTION_CHECK", "Connection checks ignored applied request edits");
            Require(AdvisorPrompts.ToolConnectionTestRequest(runtime.Current) == "EDITED_TOOL_CONNECTION_CHECK", "Tool probe instructions ignored temporary prompt edits");
            runtime.Reset();
            Require(AdvisorShortcut.KeyRisks.Prepare("主なリスク", runtime.Current).Text == oldQuestion.Text &&
                chat.Exchanges[1].Question == "EDITED_REQUEST_key_risks", "Reset failed to restore future questions or rewrote an existing conversation");
            File.WriteAllText(Path.Combine(directory, "common.json"), "{");
            Require((string?)AdvisorPrompts.ReadRequests(directory)["connection_test"] == original.Text("requests.json", "connection_test"),
                "A connection request depended on malformed advisory policies");
        }));
        check("Common preferences default to blank, round-trip multiline text and reject invalid persisted values", () =>
        {
            Require(AdvisorOptions.Parse("{}").CommonPrompt == "", "An existing installation received an unsolicited common preference");
            const string text = "距離は海里で説明する。\nGive reasons. <>&\" 𠮷";
            var saved = AdvisorOptions.Parse(System.Text.Encoding.UTF8.GetString(new AdvisorOptions(commonPrompt: text).Data()));
            Require(saved.CommonPrompt == text && saved.Information.Key == AdvisorInformation.Default.Key && saved.MaxQueryRounds == AdvisorQueryLimits.DefaultMaxRounds,
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
            Rewrite(directory, "campaign", value =>
            {
                value["evidence_policy"] = "EXTERNAL_CAMPAIGN"; value["aviation_semantics"] = "EXTERNAL_AVIATION";
                value["tool_description"] = "EXTERNAL_CAMPAIGN_TOOL"; value["victory_description"] = "EXTERNAL_VICTORY_RULE";
                value["catalog_observed_contacts"] = "EXTERNAL_CAMPAIGN_CONTACTS"; value["error_integer_type"] = "{0}: external integer requirement";
            });
            Rewrite(directory, "battle", value => value["evidence_policy"] = "OTHER_MODE_ONLY");
            Rewrite(directory, "common", value => value["instructions"]!["advice_policy"] = "EXTERNAL_ADVICE_POLICY");
            var prompts = AdvisorPrompts.Load(directory);
            var snapshot = QueryTests.Capture(new SituationTests.Fixture(Assembly.LoadFrom(campaignPath)));
            var session = new AdvisorQuerySession(snapshot, "fr", "Question", prompts: prompts);
            var input = session.InitialInput();
            Require((string?)input["query_tool"]!["description"] == "EXTERNAL_CAMPAIGN_TOOL" &&
                (string?)input["situation"]!["victory_conditions"]!["rules"]!["description"] == "EXTERNAL_VICTORY_RULE" &&
                ((JArray)input["knowledge_catalog"]!).Any(x => (string?)x["category"] == "observed_contacts" && (string?)x["description"] == "EXTERNAL_CAMPAIGN_CONTACTS"),
                "The campaign retained hardcoded descriptions");
            var invalid = session.Execute(new JObject { ["consultation_id"] = session.Id,
                ["requests"] = new JArray(new JObject { ["query"] = "list_contacts", ["offset"] = "invalid" }) });
            Require((string?)invalid["error"]!["message"] == "offset: external integer requirement", "External error arguments were not formatted for the rejected query");
            Require((string?)input["instructions"]!["advice_policy"] == "EXTERNAL_ADVICE_POLICY" &&
                (string?)input["instructions"]!["evidence_policy"] == "EXTERNAL_CAMPAIGN" && (string?)input["aviation_semantics"] == "EXTERNAL_AVIATION" &&
                !input.ToString().Contains("OTHER_MODE_ONLY", StringComparison.Ordinal), "Campaign policy was hardcoded or mixed with battle instructions");
            var runtime = new AdvisorPromptOverrides(prompts);
            var edits = prompts.Fields.ToDictionary(f => f.Id, f => f.Value);
            edits["campaign.json:evidence_policy"] = "TEMP_CAMPAIGN";
            edits["campaign.json:retrieval"] = "TEMP_RETRIEVAL";
            edits["campaign.json:aviation_semantics"] = "TEMP_AVIATION";
            runtime.Apply(edits);
            var next = new AdvisorQuerySession(snapshot, "fr", "Again", prompts: runtime.Current).InitialInput();
            Require((string?)next["instructions"]!["evidence_policy"] == "TEMP_CAMPAIGN" &&
                (string?)next["instructions"]!["retrieval"] == "TEMP_RETRIEVAL" && (string?)next["aviation_semantics"] == "TEMP_AVIATION" &&
                (string?)session.InitialInput()["aviation_semantics"] == "EXTERNAL_AVIATION" &&
                (string?)runtime.Current.Instructions(true)["evidence_policy"] == "OTHER_MODE_ONLY", "Campaign edits leaked into battle rules or an older campaign consultation");

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
        ["public_objectives"] = new JArray(), ["mission_map_areas"] = new JArray(), ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(),
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
    private sealed class RoundTransport : IGeminiTransport
    {
        private readonly Func<JObject, int, JObject> reply;
        private int calls;
        internal RoundTransport(Func<JObject, int, JObject> reply) { this.reply = reply; }
        public Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken cancellation) => Task.FromResult(reply(body, ++calls));
    }
    private sealed class CaptureTransport : IGeminiTransport
    {
        private readonly Action<JObject> inspect;
        internal int Calls;
        internal CaptureTransport(Action<JObject> inspect) { this.inspect = inspect; }
        public Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken cancellation)
        {
            Calls++; inspect(body);
            return Task.FromResult(InteractionFixture.Reply(new JObject { ["text"] = "Answer" }));
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
