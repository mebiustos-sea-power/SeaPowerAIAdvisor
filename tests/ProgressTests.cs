using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ProgressTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Progress follows real API calls and batched retrievals through the final answer without leaking private profiles", () => Async(async () =>
        {
            var session = Session(3); var events = new List<AdvisorProgress>();
            var profile = new AdvisorProfile(new[] {
                new AdvisorPersona("alice-id", "Alice", "PRIVATE_ROLE", "PRIVATE_IMAGE.png", "Active style"),
                new AdvisorPersona("bob-id", "PRIVATE_NAME", "PRIVATE_ROLE", "PRIVATE_IMAGE.png", "PRIVATE_STYLE")
            }, "alice-id", false, "PRIVATE_DEFAULT_STYLE");
            var transport = new Fake((call, body, token) =>
            {
                Require(events.Last().ApiRequest == call && events.Last().Rounds == call - 1,
                    "API progress was not published before the matching network request");
                if (call == 1)
                {
                    var input = InteractionFixture.Initial(body);
                    Require((string?)input["advisor_preferences"]!["name"] == "Alice" &&
                        (string?)input["advisor_preferences"]!["personality_and_tone"] == "Active style", "Active preferences missing");
                }
                Require(
                    !body.ToString().Contains("PRIVATE_", StringComparison.Ordinal) && !body.ToString().Contains("alice-id", StringComparison.Ordinal),
                    "Inactive registrations, role, image path or private identity leaked to Gemini");
                if (call == 1) return Reply(Call("catalog"));
                if (call == 2) return Reply(Call("list_units", "get_briefing"));
                Require((string?)body["generation_config"]!["tool_choice"] == "auto", "The final answer was forced by disabling retrievals");
                return Reply(new JObject { ["text"] = "Answer" });
            });
            var answer = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None,
                profile: profile, progress: p => events.Add(p));
            Require(answer.ApiRequests == 3 && answer.QueryRounds == 2 && events.Select(e => e.Stage).SequenceEqual(new[] {
                AdvisorStage.Waiting, AdvisorStage.Retrieving, AdvisorStage.Waiting, AdvisorStage.Retrieving, AdvisorStage.Waiting, AdvisorStage.FinalAnswer }) &&
                events[1].Rounds == 1 && events[3].Rounds == 2 && events.All(e => e.MaxRounds == 3) &&
                events[4].Queries.SequenceEqual(new[] { "list_units", "get_briefing" }), "Progress phases differ from the performed requests");
            foreach (var language in new[] { "en", "ja", "fr" })
            {
                var text = AdvisorText.Load(language); var display = AdvisorProgress.Format(events[4], text);
                Require(display.Contains(text["progress.topic.units"], StringComparison.Ordinal) &&
                    display.Contains(text["progress.topic.briefing"], StringComparison.Ordinal), "Completed retrieval categories are missing");
                Require(!AdvisorProgress.Format(new AdvisorProgress(AdvisorStage.Retrieving, 1, 1, 2, new[] { "PRIVATE_UNKNOWN_QUERY" }), text)
                    .Contains("PRIVATE_UNKNOWN_QUERY", StringComparison.Ordinal), "An untrusted query name became UI text");
            }
        }));
        check("Rejected retrievals and cancellation do not report successful information acquisition", () => Async(async () =>
        {
            var events = new List<AdvisorProgress>();
            var session = Session(1);
            var transport = new Fake((call, body, token) => call == 1 ? Reply(Call("PRIVATE_UNKNOWN_QUERY")) : Reply(new JObject { ["text"] = "Insufficient data" }));
            var incomplete = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None, progress: events.Add);
            Require(!incomplete.Completed && events.All(p => p.Stage != AdvisorStage.FinalAnswer) &&
                events.All(p => !p.Queries.Contains("PRIVATE_UNKNOWN_QUERY")), "Rejected queries were reported as acquired data or leaked model arguments");
            using var cancelled = new CancellationTokenSource(); events.Clear(); session = Session(1);
            transport = new Fake((call, body, token) => { cancelled.Cancel(); return Reply(Call("catalog")); });
            try { await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), cancelled.Token, progress: events.Add); }
            catch (OperationCanceledException)
            { Require(session.RoundsUsed == 0 && events.Count == 1 && events[0].Stage == AdvisorStage.Waiting, "Cancelled communication continued retrieval progress"); return; }
            throw new InvalidOperationException("Cancellation was ignored");
        }));
        check("Background progress retains only the newest update and retired jobs cannot affect a later consultation", () => Async(async () =>
        {
            var ready = new TaskCompletionSource<Action<AdvisorProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<AdviceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var old = new AdvisorJob((token, report) => { ready.SetResult(report); return finish.Task; }, _ => { });
            var report = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            report(new AdvisorProgress(AdvisorStage.Waiting, 1, 0, 5));
            var latest = new AdvisorProgress(AdvisorStage.Waiting, 2, 1, 5); report(latest);
            Require(ReferenceEquals(old.TakeProgress(), latest) && old.TakeProgress() == null, "Progress queued obsolete updates or repeated one");
            old.Dispose(); report(latest);
            Require(old.TakeProgress() == null, "Retired job retained a late progress update");
            finish.SetResult(new AdviceResult("Late", 2, 1, null));
            Require((await old.Completion).IsCancelled, "A retired job published a late answer");
            using var next = new AdvisorJob((token, publish) =>
            {
                publish(new AdvisorProgress(AdvisorStage.Waiting, 1, 0, 5)); return Task.FromResult(new AdviceResult("Current", 1, 0, null));
            }, _ => { });
            await next.Completion;
            Require(next.TakeCompleted().Result?.Text == "Current" && next.TakeProgress() == null, "Completion left stale progress on the new job");
        }));
        check("Each visible answer and AI history retain their original adviser while retry uses the newly selected identity", () =>
        {
            var chat = new AdvisorConversation(); var text = AdvisorText.Load("ja");
            var first = chat.Begin(new AdvisorQuestion("First"), "Alice", "alice-id");
            first.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 2, 1, 5, new[] { "catalog" }));
            var pending = AdvisorTranscript.Render(chat, text, "Bob", "empty");
            var pendingView = XDocument.Parse(pending);
            var pendingName = pendingView.Descendants().Single(e => (string?)e.Attribute("Foreground") == "{DynamicResource AdvisorNameBrush}");
            Require(pending.Contains(text["progress.topic.catalog"], StringComparison.Ordinal) &&
                (string?)pendingName.Attribute("Text") == "Alice" && (string?)pendingName.Attribute("FontWeight") == "Bold" &&
                !pendingView.Descendants().Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "CopyAnswer0"),
                "The waiting reply lost its actual adviser identity/style or exposed an unavailable answer copy action");
            var waitingHeader = pendingName.Parent!;
            first.Complete("Alice answer");
            var completedHeader = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Bob", "empty")).Descendants()
                .Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "AnswerHeader0"));
            Require(waitingHeader.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "AnswerHeader0") &&
                completedHeader.Descendants().Any(e => (string?)e.Attribute("Text") == "Alice") &&
                completedHeader.Descendants().Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "CopyAnswer0"),
                "Completion did not retain the waiting response's scroll header and add answer copying");
            var second = chat.Begin(new AdvisorQuestion("Second"), "Bob", "bob-id"); second.Complete("Bob answer");
            var view = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Renamed current adviser", "empty"));
            var names = view.Descendants().Where(e => (string?)e.Attribute("Foreground") == "{DynamicResource AdvisorNameBrush}")
                .Select(e => (string?)e.Attribute("Text")).ToArray();
            Require(names.SequenceEqual(new[] { "Alice", "Bob" }) && chat.Exchanges.Select(e => e.AdvisorName).SequenceEqual(names) &&
                !view.ToString().Contains("ProgressTurn", StringComparison.Ordinal), "Changing the active adviser rewrote earlier speakers or retained waiting labels");
            var failed = chat.Begin(new AdvisorQuestion("Retry"), "Alice", "alice-id");
            failed.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 4, 3, 5)); failed.Cancel();
            chat.Retry(failed, "Bob", "bob-id");
            Require(failed.Progress == null && failed.AdvisorId == "bob-id" && failed.AdvisorName == "Bob", "Retry retained old progress or the former adviser");
            failed.Complete("Retried answer");
            Require(chat.Exchanges.Last().AdvisorName == "Bob", "A retried answer has the wrong speaker");
        });
    }
    private static BattleQuerySession Session(int maxRounds) => new(new BattleSnapshot(new JObject {
        ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
        ["public_objectives"] = new JArray(),
        ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(), ["unit_capabilities"] = new JArray(),
        ["contact_threat_references"] = new JArray(), ["weapon_references"] = new JArray(), ["published_reports"] = new JArray(), ["mission_briefing"] = "Briefing"
    }), "ja", "What next?", maxRounds: maxRounds);
    private static AdvisorSettings Settings() => AdvisorSettings.Parse("{\"api_key\":\"mock_key_no_network\"}");
    private static JObject Call(params string[] names) => new() { ["functionCall"] = new JObject {
        ["name"] = "query_game", ["args"] = new JObject { ["requests"] = new JArray(names.Select(n => new JObject { ["source"] = "situation", ["query"] = n })) } } };
    private static JObject Reply(params JObject[] parts) => InteractionFixture.Reply(parts);
    private sealed class Fake : IGeminiTransport
    {
        private readonly Func<int, JObject, CancellationToken, JObject> generate; private int count;
        internal Fake(Func<int, JObject, CancellationToken, JObject> generate) { this.generate = generate; }
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken token) => Task.FromResult(generate(++count, body, token));
    }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
