using System;
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
                Require(request.DraftAfterSend("下書き") == "下書き" && new AdvisorQuestion("入力").DraftAfterSend("入力") == "",
                    "Sending a message did not clear its own draft or cleared a quick-action draft");
                turn.Complete("**回答**");
                Require(chat.Turns.Count == 1 && chat.Exchanges.Single().Question == request.Text &&
                    chat.Exchanges.Single().Answer == "**回答**" && !View(chat, text).Contains(text["transcript.waiting"]),
                    "Completion duplicated the player message or left the waiting state behind");
            }
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
