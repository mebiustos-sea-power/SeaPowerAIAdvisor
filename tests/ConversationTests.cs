using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using SeaPowerAIAdvisor;

internal static class ConversationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Accepted messages are visible before a reply without entering AI history or exposing shortcut prompts", () =>
        {
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language);
                var chat = new AdvisorConversation();
                var request = AdvisorShortcut.NextSteps.Prepare(text[AdvisorShortcut.NextSteps.LabelKey]);
                var turn = chat.Begin(request);
                var display = View(chat, text);
                Require(turn.State == AdvisorTurnState.Waiting && chat.Exchanges.Count == 0 &&
                    display.Contains(request.DisplayText) && !display.Contains(request.Text) && display.Contains(text["transcript.waiting"]),
                    "A pending question is missing, leaks its internal prompt, or became prior dialogue");
                Require(!request.ConsumesDraft("下書き") && new AdvisorQuestion("入力").ConsumesDraft("入力"),
                    "Sending a message did not clear its own draft or cleared a quick-action draft");
                turn.Complete("**回答**");
                Require(chat.Turns.Count == 1 && chat.Exchanges.Single().Question == request.Text &&
                    chat.Exchanges.Single().Answer == "**回答**" && !View(chat, text).Contains(text["transcript.waiting"]),
                    "Completion duplicated the player message or left the waiting state behind");
            }
        });
        check("Only completed replies show stable elapsed seconds beside the original adviser without changing copied or AI dialogue", () =>
        {
            var chat = new AdvisorConversation();
            var turn = chat.Begin(new AdvisorQuestion("Original question"), "Alice <>&", "alice");
            var text = AdvisorText.Load("en");
            Require(turn.Elapsed == null && chat.Exchanges.Count == 0, "A pending answer exposed a completed duration");
            var pending = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Current adviser", "empty"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var pendingHeader = pending.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "AnswerHeader0");
            Require(pendingHeader.Descendants().Count(e => e.Name.LocalName == "TextBlock") == 1,
                "A pending answer displayed an elapsed-time counter");
            turn.Complete("**Original answer**");
            var elapsed = turn.Elapsed ?? throw new InvalidOperationException("The answer has no elapsed time");
            var expected = ((long)elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
            foreach (var language in new[] { "en", "ja" })
            foreach (var size in new[] { AdvisorTextSize.Small, AdvisorTextSize.Standard, AdvisorTextSize.Large })
            {
                var view = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load(language), "Renamed adviser", "empty", textSize: size));
                var header = view.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "AnswerHeader0");
                var labels = header.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToArray();
                Require(labels.Select(e => (string?)e.Attribute("Text")).SequenceEqual(new[] { "Alice <>&", expected }) &&
                    (double)labels[1].Attribute("FontSize")! < (double)labels[0].Attribute("FontSize")! &&
                    header.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "CopyAnswer0") && turn.Elapsed == elapsed,
                    "Elapsed time moved away from the adviser, changed after completion, or disrupted answer copying and text sizing");
            }
            var next = chat.Begin(new AdvisorQuestion("Retry question"), "Bob", "bob");
            next.Fail(AdvisorNotice.Connection(new AdvisorConnectionException("request_timeout")));
            chat.Retry(next);
            Require(next.Elapsed == null && turn.Elapsed == elapsed, "Retry reused a completed duration or changed earlier history");
            next.Complete("Retried answer");
            Require(next.Elapsed.HasValue && next.Elapsed.Value >= TimeSpan.Zero &&
                chat.Exchanges[0].Question == "Original question" && chat.Exchanges[0].Answer == "**Original answer**" &&
                chat.Exchanges[0].AdvisorName == "Alice <>&" && chat.Exchanges[1].Answer == "Retried answer",
                "Timing changed the retained question, answer, speaker or retry result");
        });
        check("Failed messages keep their place and retry replaces the error without duplicating AI dialogue", () =>
        {
            var chat = new AdvisorConversation(); var text = AdvisorText.Load("ja");
            chat.TryAdd("前の質問", "前の回答", out _);
            var turn = chat.Begin(new AdvisorQuestion("再試行する質問"));
            turn.Fail(AdvisorNotice.Connection(new AdvisorConnectionException("request_timeout")));
            var failed = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Advisor", "empty"));
            Require(chat.Exchanges.Count == 1 && chat.CanRetry(turn) &&
                failed.Descendants().Any(e => (string?)e.Attribute("Foreground") == "{DynamicResource AdvisorErrorBrush}" &&
                    ((string?)e.Attribute("Text"))?.Contains("request_timeout", StringComparison.Ordinal) == true),
                "The failed message was lost, lacks visible error styling, or entered AI history");
            chat.Retry(turn);
            Require(chat.Turns.Count == 2 && turn.Error == null && turn.State == AdvisorTurnState.Waiting && chat.Exchanges.Count == 1,
                "Retry appended a duplicate or retained stale failure state");
            turn.Complete("新しい回答");
            Require(chat.Turns.Count == 2 && chat.Exchanges.Count == 2 && chat.Exchanges[1].Question == "再試行する質問" && !chat.CanRetry(turn),
                "The successful retry duplicated or changed earlier dialogue");
        });
        check("Exchange-limit guidance recommends the default only for lower captured limits and stays out of payload limits and successful answers", () =>
        {
            foreach (var language in new[] { "en", "ja" })
            foreach (var limit in new[] { 5, AdvisorQueryLimits.DefaultMaxRounds - 1, AdvisorQueryLimits.DefaultMaxRounds, AdvisorQueryLimits.MaximumRounds })
            {
                var text = AdvisorText.Load(language); var chat = new AdvisorConversation();
                var turn = chat.Begin(new AdvisorQuestion("Limited question"));
                turn.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, limit));
                turn.Finish(new AdviceResult("", limit, 0, null, completionReason: AdvisorCompletionReason.RoundLimit), null);
                var expected = text["transcript.round_limit"] + (limit < AdvisorQueryLimits.DefaultMaxRounds
                    ? text.Format("transcript.round_limit_recommendation", AdvisorQueryLimits.DefaultMaxRounds) : "");
                var later = chat.Begin(new AdvisorQuestion("Later question"));
                later.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, AdvisorQueryLimits.MaximumRounds));
                later.Complete("Later answer");
                var view = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                Require(turn.Progress?.MaxRounds == limit && view.Descendants().Attributes("Text").Any(a => a.Value == expected),
                    "History used a later request limit or displayed a recommendation for an equal or higher limit");
                var payload = new AdvisorConversation();
                var oversized = payload.Begin(new AdvisorQuestion("Payload"));
                oversized.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, 1));
                oversized.Finish(new AdviceResult("", 1, 1, null, completionReason: AdvisorCompletionReason.PayloadLimit), null);
                Require(!View(payload, text).Contains(text["transcript.round_limit"], StringComparison.Ordinal) &&
                    View(payload, text).Contains(text["transcript.payload_limit"], StringComparison.Ordinal),
                    "An information limit misleadingly advised raising the request limit");
                var retry = new AdvisorConversation(); var retried = retry.Begin(new AdvisorQuestion("Retry"));
                retried.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, 5));
                retried.Fail(AdvisorNotice.Connection(new AdvisorConnectionException("request_timeout")));
                retry.Retry(retried);
                Require(retried.Progress == null, "Retry retained progress from the previous attempt");
                retried.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, AdvisorQueryLimits.DefaultMaxRounds));
                retried.Finish(new AdviceResult("", AdvisorQueryLimits.DefaultMaxRounds, 0, null, completionReason: AdvisorCompletionReason.RoundLimit), null);
                Require(retried.Progress?.MaxRounds == AdvisorQueryLimits.DefaultMaxRounds &&
                    XDocument.Parse(AdvisorTranscript.Render(retry, text, "Adviser", "empty")).Descendants().Attributes("Text")
                        .Any(a => a.Value == text["transcript.round_limit"]), "Retry retained its earlier limit");
                var successful = new AdvisorConversation();
                var answered = successful.Begin(new AdvisorQuestion("Successful question"));
                answered.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, 0, 5));
                answered.Complete("Successful answer");
                Require(!View(successful, text).Contains(text["transcript.round_limit"], StringComparison.Ordinal) &&
                    !View(successful, text).Contains(text.Format("transcript.round_limit_recommendation", AdvisorQueryLimits.DefaultMaxRounds), StringComparison.Ordinal),
                    "Limit advice appeared on a successful answer");
            }
        });
        check("Cancellation and configuration notices stay out of AI history and clearing removes retry targets", () =>
        {
            var chat = new AdvisorConversation(); var text = AdvisorText.Load("en");
            var cancelled = chat.Begin(new AdvisorQuestion("Cancelled message")); cancelled.Cancel();
            var notice = AdvisorNotice.Connection(new AdvisorConnectionException("api_key_missing"));
            var view = AdvisorTranscript.Render(chat, text, "Advisor", "empty", notice);
            Require(view.Contains("api_key_missing", StringComparison.Ordinal) && chat.Exchanges.Count == 0 && chat.CanRetry(cancelled),
                "Cancellation or a setup notice became AI dialogue or disappeared from the display");
            var next = chat.Begin(new AdvisorQuestion("New message"));
            Require(!chat.CanRetry(cancelled), "An older failure can be retried against a later conversation");
            next.Cancel(); chat.Clear();
            Require(chat.Turns.Count == 0 && chat.Exchanges.Count == 0 && !chat.CanRetry(next) && !chat.CanRetry(cancelled),
                "Clearing preserved a stale message or retry target");
        });
    }
    private static string View(AdvisorConversation chat, AdvisorText text) => string.Concat(
        XDocument.Parse(AdvisorTranscript.Render(chat, text, "Advisor", "empty")).Descendants().Attributes("Text").Select(a => a.Value));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
