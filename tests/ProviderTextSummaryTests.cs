using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;
using Client = SeaPowerAIAdvisor.ExtensionFixture;

internal static partial class ProviderTests
{
    private static void RunTextSummaries(Action<string, Action> check)
    {
        check("Text compression defaults off and unrelated settings saves preserve its explicit selection", () =>
        {
            Require(!AdvisorOptions.Parse("{}").SummarizeMissionText, "Compression was enabled before release evaluation");
            foreach (var invalid in new[] { "null", "1", "\"true\"" })
            {
                try { AdvisorOptions.Parse("{\"summarize_mission_text\":" + invalid + "}"); throw new InvalidOperationException("Invalid compression preference accepted"); }
                catch (AdvisorOptionsException e) { Require(e.Code == "options_invalid", "Wrong settings error"); }
            }
            var directory = Path.Combine(Path.GetTempPath(), "advisor-summaries-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
                store.SaveInformation(AdvisorInformation.Default, true);
                foreach (var change in new Action[] { () => store.EnsureUserName("Commander"), () => store.SaveGeneral(AdvisorTextSize.Small, "Commander"),
                    () => store.SaveCrew(true), () => store.SaveWip(false, false), () => store.SaveConsultation(15, 3, ""),
                    () => store.SaveInformation(AdvisorInformation.Default), () => store.SaveDeveloper(false),
                    () => store.SaveCommands(Array.Empty<string>()), () => store.SaveTemplates(new AdvisorTemplateOptions()) })
                { change(); Require(store.Read().SummarizeMissionText, "Another settings category erased compression preference"); }
                store.SaveInformation(AdvisorInformation.Default, false);
                Require(!store.Current.SummarizeMissionText && !store.Read().SummarizeMissionText, "Explicit OFF was not saved and applied");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            check("Compressed mission texts reuse without extra requests in " + mode, () => Async(async () =>
            {
                var cache = new AdvisorTextSummaryCache();
                for (var pass = 0; pass < 2; pass++)
                {
                    var knowledge = SummaryKnowledge(cache, true, reports: false);
                    var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>());
                    var seen = new List<JObject>();
                    var provider = new FakeProvider("summary-model", (session, n, allowed, token) =>
                    {
                        if (n == 1)
                        {
                            Require(session.Input.Tools.Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool) == (pass == 0), "Cached text retained the completion overhead or raw text lost it");
                            if (mode == AdvisorSituationDelivery.OnDemand) return Task.FromResult(Reply(Call("briefing", "get_briefing")));
                            if (mode == AdvisorSituationDelivery.OnRequest) return Task.FromResult(Reply(new AiToolCall("snapshot", "get_situation", new JObject())));
                        }
                        seen.AddRange(SummaryRows(n == 1 ? session.Input.Data : session.Results[0].Data));
                        Require(seen.Count == 1 && (bool?)seen[0]["summary_required"] == (pass == 0), "Delivery lost the compression flag or duplicated the source");
                        Require((string?)seen[0]["text"] == (pass == 0 ? BriefingText : "task: protect convoy; deadline: 18:00Z; subs: possible, unconfirmed"), "Projection did not replace only the text");
                        return Task.FromResult(pass == 0 ? SummaryReply(new JArray(SummaryRow(seen[0], "task: protect convoy; deadline: 18:00Z; subs: possible, unconfirmed"))) :
                            new AiReply("日本語の回答", Array.Empty<AiToolCall>(), 7));
                    });
                    var result = await new AdvisorEngine(provider, delivery: mode).ConsultAsync(context, CancellationToken.None);
                    Require(result.Completed && result.Text == "日本語の回答" && result.ApiRequests == (mode == AdvisorSituationDelivery.Upfront ? 1 : 2), "Compression added a finishing request or exposed its machine text as the answer");
                }
            }));
        foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            check("Mission-message summaries reuse independently when a new publication arrives in " + mode, () => Async(async () =>
            {
                var cache = new AdvisorTextSummaryCache();
                for (var pass = 0; pass < 2; pass++)
                {
                    var messages = new JArray(new JObject { ["id"] = "message_1", ["published_at_utc"] = "1985-06-01T00:00:00Z", ["text"] = "Initial mission orders" });
                    if (pass == 1) messages.Add(new JObject { ["id"] = "message_2", ["published_at_utc"] = "1985-06-01T00:01:00Z", ["text"] = "New mission orders" });
                    var knowledge = new BattleQuerySession(new BattleSnapshot(BattleMissionMessageTests.Snapshot(messages)), "ja", "Question",
                        information: AdvisorInformation.All, summaryCache: cache, summarizeText: true);
                    var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>());
                    var provider = new FakeProvider("messages", (session, n, _, _) =>
                    {
                        if (n == 1 && mode == AdvisorSituationDelivery.OnDemand) return Task.FromResult(Reply(Call("briefing", "get_briefing")));
                        if (n == 1 && mode == AdvisorSituationDelivery.OnRequest) return Task.FromResult(Reply(new AiToolCall("snapshot", "get_situation", new JObject())));
                        var rows = SummaryRows(n == 1 ? session.Input.Data : session.Results[0].Data).ToArray();
                        Require(rows.Length == (pass == 0 ? 2 : 3) && rows.Count(r => (bool?)r["summary_required"] == true) == (pass == 0 ? 2 : 1),
                            "A new message caused unchanged sources to be summarized again");
                        return Task.FromResult(SummaryReply(new JArray(rows.Where(r => (bool?)r["summary_required"] == true)
                            .Select(r => SummaryRow(r, "compact: " + (string)r["text"]!)))));
                    });
                    var result = await new AdvisorEngine(provider, delivery: mode).ConsultAsync(context, CancellationToken.None);
                    Require(result.Completed && result.ApiRequests == (mode == AdvisorSituationDelivery.Upfront ? 1 : 2), "Mission-message compression added an API round trip");
                }
            }));
        check("Standard-to-independent handoff retains compression for the standard engine and restores original query texts for the independent workflow", () => Async(async () =>
        {
            var standard = new Client.StandardExtension(); var independent = IndependentClient();
            using var host = new PublicHost(standard, independent);
            var sawStandard = false; var sawIndependent = false; var sends = 0;
            standard.Open = (_, _, input, _) =>
            {
                sawStandard = input.ToolsJson.Select(JObject.Parse).Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool);
                return new Client.Session(_ => new Api.ProviderRequest(1, _ => ++sends == 1
                    ? Task.FromResult(new Api.ProviderReply("", new[] { new Api.ToolCall("briefing", ToolName, ReadArguments(QueryTests.Q("get_briefing")).ToString()) }, 1))
                    : throw new Api.AdvisorException("rate_limited", 429)), _ => { }, () => { });
            };
            independent.Consult = (configuration, _, context, _) =>
            {
                var row = SummaryRows(JObject.Parse(context.Knowledge.Execute(PublicQuery("get_briefing")))).Single();
                sawIndependent = (string?)row["text"] == BriefingText && (bool?)row["summary_required"] == false &&
                    !context.Input.ToolsJson.Select(JObject.Parse).Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool);
                return Task.FromResult(new Api.AdvisorAnswer("Independent answer", configuration.Model));
            };
            var draft = ExtensionDraft(standard); var sub = ExternalConfiguration(independent, "sub");
            draft.Put(new AdvisorApiConfigurationDraft(sub)); draft.SubId = sub.Id;
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(draft),
                AdvisorContext.Create(SummaryKnowledge(new AdvisorTextSummaryCache(), true, reports: false), Array.Empty<ChatExchange>()),
                "unused-summary-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(sawStandard && sawIndependent && answer.Text == "Independent answer", "A mixed workflow lost compression or imposed it on the independent continuation");
        }));
        check("Independent-to-standard handoff can reuse cached compression without imposing it on independent queries", () => Async(async () =>
        {
            var cache = new AdvisorTextSummaryCache();
            var prime = AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false), Array.Empty<ChatExchange>());
            var original = SummaryRows(prime.PrepareStandardInput(AdvisorSituationDelivery.Upfront).Data).Single();
            prime.TextSummaries!.ReadReply(SummaryReply(new JArray(SummaryRow(original, "compact task notes"))));
            var independent = IndependentClient(); var standard = new Client.StandardExtension();
            using var host = new PublicHost(independent, standard);
            var sawIndependent = false; var sawStandard = false; var sends = 0;
            independent.Consult = (_, _, context, _) =>
            {
                var row = SummaryRows(JObject.Parse(context.Knowledge.Execute(PublicQuery("get_briefing")))).Single();
                sawIndependent = (string?)row["text"] == BriefingText && (bool?)row["summary_required"] == false;
                throw new Api.AdvisorException("rate_limited", 429);
            };
            standard.Open = (_, _, input, _) =>
            {
                Require(!input.ToolsJson.Select(JObject.Parse).Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool), "Cached handoff retained compression-output overhead");
                return new Client.Session(_ => new Api.ProviderRequest(1, _ => Task.FromResult(++sends == 1
                    ? new Api.ProviderReply("", new[] { new Api.ToolCall("briefing", ToolName, ReadArguments(QueryTests.Q("get_briefing")).ToString()) }, 1)
                    : new Api.ProviderReply("Standard answer", totalTokens: 1))), values =>
                    { sawStandard = (string?)SummaryRows(JObject.Parse(values.Single().DataJson)).Single()["text"] == "compact task notes"; }, () => { });
            };
            var draft = ExtensionDraft(independent); var sub = ExternalConfiguration(standard, "sub");
            draft.Put(new AdvisorApiConfigurationDraft(sub)); draft.SubId = sub.Id;
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(draft),
                AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false), Array.Empty<ChatExchange>()),
                "unused-summary-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(sawIndependent && sawStandard && answer.Text == "Standard answer", "Mixed fallback reused compressed text in the wrong workflow or lost its cache");
        }));
        check("Compression reuses report text after snapshot IDs shift and OFF still presents original texts", () =>
        {
            var cache = new AdvisorTextSummaryCache(); var knowledge = SummaryKnowledge(cache, true);
            var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()); var input = context.PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            var originals = SummaryRows(input.Data).ToArray();
            context.TextSummaries!.ReadReply(SummaryReply(new JArray(originals.Select(row => SummaryRow(row, "compact:" + (string)row["text"]!)))));
            var shifted = SummarySnapshot(); shifted["published_reports"]![0]!["id"] = "report_8";
            var next = new BattleQuerySession(new BattleSnapshot(shifted), "ja", "Follow-up", summaryCache: cache, summarizeText: true);
            var nextInput = AdvisorContext.Create(next, Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            Require(SummaryRows(nextInput.Data).All(row => (bool?)row["summary_required"] == false && ((string)row["text"]!).StartsWith("compact:", StringComparison.Ordinal)), "Snapshot-local IDs invalidated unchanged report memory");
            var off = SummaryKnowledge(cache, false); var offInput = AdvisorContext.Create(off, Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            Require(!offInput.Tools.Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool) &&
                SummaryRows(offInput.Data).All(row => (bool?)row["summary_required"] == false && !((string)row["text"]!).StartsWith("compact:", StringComparison.Ordinal)), "OFF reused compressed text or requested another summary");
        });
        check("Changed or retired sources cannot receive summaries from an older consultation", () =>
        {
            foreach (var clear in new[] { false, true })
            {
                var cache = new AdvisorTextSummaryCache(); var messages = new List<string>();
                var old = AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false, log: SummaryLog(messages)), Array.Empty<ChatExchange>());
                var sent = SummaryRows(old.PrepareStandardInput(AdvisorSituationDelivery.Upfront).Data).Single();
                if (clear) cache.Clear();
                var changed = SummaryKnowledge(cache, true, reports: false, briefing: clear ? BriefingText : "New mission constraints.");
                old.TextSummaries!.ReadReply(SummaryReply(new JArray(SummaryRow(sent, "stale compressed text"))));
                var input = AdvisorContext.Create(changed, Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
                Require((bool?)SummaryRows(input.Data).Single()["summary_required"] == true && messages.Any(w => w.Contains("source_changed", StringComparison.Ordinal)), "A late result poisoned changed or new-battle source memory");
            }
        });
        check("Malformed, missing, duplicate and unseen compression entries do not reject a valid final answer", () =>
        {
            for (var invalid = 0; invalid < 6; invalid++)
            {
                var cache = new AdvisorTextSummaryCache(); var messages = new List<string>();
                var context = AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false, log: SummaryLog(messages)), Array.Empty<ChatExchange>());
                var row = SummaryRows(context.PrepareStandardInput(AdvisorSituationDelivery.Upfront).Data).Single();
                var item = SummaryRow(row, "valid"); JToken entries = invalid switch
                {
                    0 => JValue.CreateNull(), 1 => new JArray(),
                    2 => new JArray(new JObject { ["summary_ref"] = new JObject(), ["text"] = "bad" }),
                    3 => new JArray(item, item.DeepClone()),
                    4 => new JArray(new JObject { ["summary_ref"] = "unknown", ["text"] = "bad" }),
                    _ => new JArray(SummaryRow(row, " "))
                };
                var reply = context.TextSummaries!.ReadReply(SummaryReply(entries));
                Require(reply.Text == "日本語の回答" && reply.Calls.Count == 0 && messages.Count > 0, "Auxiliary formatting failed the answer or concealed invalid memory");
                var fresh = AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false), Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
                Require((string?)SummaryRows(fresh.Data).Single()["text"] == BriefingText, "Invalid compression replaced the source text");
            }
        });
        check("Missing compression preserves a normal final reply and is not automatically retried", () => Async(async () =>
        {
            var messages = new List<string>(); var cache = new AdvisorTextSummaryCache();
            var provider = new FakeProvider("no-memory", (_, n, _, _) => Task.FromResult(new AiReply("Ordinary answer", Array.Empty<AiToolCall>(), 7)));
            var result = await new AdvisorEngine(provider, delivery: AdvisorSituationDelivery.Upfront).ConsultAsync(
                AdvisorContext.Create(SummaryKnowledge(cache, true, reports: false, log: SummaryLog(messages)), Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(result.Text == "Ordinary answer" && result.ApiRequests == 1 && messages.Count > 0, "Missing auxiliary output failed, retried or went undiagnosed");
        }));
        check("Unseen source IDs and disabled source categories cannot be summarized", () =>
        {
            var cache = new AdvisorTextSummaryCache(); var messages = new List<string>();
            var knowledge = SummaryKnowledge(cache, true, reports: false, log: SummaryLog(messages));
            var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()); var input = context.PrepareStandardInput();
            var id = (string)input.Tools.Single(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool)["parameters"]!["properties"]!["summaries"]!["items"]!["properties"]!["summary_ref"]!["enum"]![0]!;
            context.TextSummaries!.ReadReply(SummaryReply(new JArray(new JObject { ["summary_ref"] = id, ["text"] = "invented" })));
            Require(messages.Count > 0, "An unseen source was allowed to seed memory");
            var hidden = AdvisorInformation.All.With(InformationKind.BattleBriefing, false).With(InformationKind.BattleReports, false).With(InformationKind.BattleMissionMessages, false);
            var disabled = new BattleQuerySession(new BattleSnapshot(SummarySnapshot()), "ja", "Question", information: hidden, summaryCache: cache, summarizeText: true);
            var filtered = AdvisorContext.Create(disabled, Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            Require(SummaryRows(filtered.Data).Count() == 0 && !filtered.Tools.Any(t => (string?)t["name"] == AdvisorTextSummarySession.CompletionTool), "Compression bypassed information permissions");
        });
        check("A summary completion cannot silently discard concurrent game-operation calls or an invalid answer", () =>
        {
            var context = AdvisorContext.Create(SummaryKnowledge(new AdvisorTextSummaryCache(), true), Array.Empty<ChatExchange>());
            context.PrepareStandardInput(AdvisorSituationDelivery.Upfront); var final = SummaryReply(new JArray()).Calls[0];
            foreach (var reply in new[] { new AiReply("", new[] { final, Call("other", "catalog") }, 7),
                new AiReply("", new[] { new AiToolCall("done", AdvisorTextSummarySession.CompletionTool, new JObject { ["answer"] = "", ["summaries"] = new JArray() }) }, 7) })
            {
                try { context.TextSummaries!.ReadReply(reply); throw new InvalidOperationException("Invalid terminal advice accepted"); }
                catch (AiConnectionException) { }
            }
        });
    }
    private const string BriefingText = "Protect the convoy before 18:00Z. Submarines may be present, but are not confirmed.";
    private static JObject SummarySnapshot(string briefing = BriefingText, bool reports = true)
    {
        var data = new JObject { ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = briefing,
            ["weather"] = new JObject { ["wind_force_beaufort"] = 3, ["precipitation"] = new JArray(), ["fog"] = false, ["lightning"] = false } };
        foreach (var key in new[] { "friendly_units", "observed_contacts", "public_objectives", "mission_map_areas", "friendly_bases", "hosted_air_units", "aviation_hosts", "air_tasks", "unit_capabilities", "contact_threat_references", "weapon_references", "published_reports" }) data[key] = new JArray();
        if (reports) data["published_reports"] = new JArray(new JObject { ["id"] = "report_1", ["published_at_utc"] = "1985-06-01T00:00:00Z", ["text"] = "Four hostile surface contacts observed." });
        return data;
    }
    private static DiagnosticLog SummaryLog(List<string> messages) => new(messages.Add,
        message => throw new InvalidOperationException("Recoverable text compression emitted a warning: " + message),
        message => throw new InvalidOperationException("Recoverable text compression emitted an error: " + message));

    private static BattleQuerySession SummaryKnowledge(AdvisorTextSummaryCache cache, bool enabled, bool reports = true, string briefing = BriefingText, DiagnosticLog? log = null) =>
        new(new BattleSnapshot(SummarySnapshot(briefing, reports)), "ja", "Question", summaryCache: cache, summarizeText: enabled, summaryLog: log);
    private static IEnumerable<JObject> SummaryRows(JToken token) => (token is JContainer container ? container.DescendantsAndSelf() : Enumerable.Empty<JToken>()).OfType<JObject>().Where(row => row["summary_required"]?.Type == JTokenType.Boolean && row["summary_ref"] != null);
    private static JObject SummaryRow(JObject source, string text) => new() { ["summary_ref"] = source["summary_ref"]!.DeepClone(), ["text"] = text };
    private static AiReply SummaryReply(JToken rows) => new("", new[] { new AiToolCall("done", AdvisorTextSummarySession.CompletionTool,
        new JObject { ["answer"] = "日本語の回答", ["summaries"] = rows }) }, 7);
}
