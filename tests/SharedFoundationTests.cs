using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

internal static class SharedFoundationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Shared providers keep independent caller instructions, credentials, budgets and tool histories", () => RunSessions().GetAwaiter().GetResult());
        check("Shared Markdown accepts caller-owned reference schemes without assuming adviser links or loading images", () =>
        {
            var targets = new List<string>();
            var rendered = MarkdownRenderer.Render("[Objective](mission:objective-1) [Unit](unit:42) [Web](https://example.invalid) ![Image](mission:objective-2)",
                (target, label) =>
                {
                    if (!target.StartsWith("mission:", StringComparison.Ordinal)) return null;
                    targets.Add(target); return UiMarkup.Element("Button", label);
                });
            Require(targets.SequenceEqual(new[] { "mission:objective-1" }) && rendered.Descendants().Count(e => e.Name.LocalName == "Button") == 1,
                "Shared rendering imposed adviser schemes, activated an external URL or treated an image as an action");
            var text = rendered.ToString();
            Require(text.Contains("unit:42", StringComparison.Ordinal) && text.Contains("https://example.invalid", StringComparison.Ordinal) &&
                text.Contains("mission:objective-2", StringComparison.Ordinal) && !rendered.Descendants().Any(e => e.Name.LocalName == "Image"),
                "Unsupported references or image text disappeared");
        });
    }

    private static async Task RunSessions()
    {
        using var handler = new Handler();
        using var transport = new AiHttpTransport(handler);
        var source = new JObject { ["marker"] = "first connection" };
        var firstSettings = new AiConnectionSettings("model-A", "secret-A", 9, 101, source);
        var secondSettings = new AiConnectionSettings("model-B", "secret-B", 12, 202, source);
        source["marker"] = "changed after both snapshots";
        var firstProvider = new NanoGptProvider(transport, firstSettings);
        var secondProvider = new NanoGptProvider(transport, secondSettings);
        var data = new JObject { ["purpose"] = "adviser" };
        var tool = new JObject { ["name"] = "inspect-draft", ["description"] = "Caller-owned tool", ["parameters"] = new JObject { ["type"] = "object" } };
        var firstInput = new AiProviderInput(data, "First caller instructions", new[] { tool });
        using var first = firstProvider.Start(firstInput, null);
        using var second = secondProvider.Start(new AiProviderInput(new JObject { ["purpose"] = "creator" }, "Second caller instructions", Array.Empty<JObject>()), null);
        data["purpose"] = "changed after input capture"; tool["name"] = "changed tool";
        var reply = await first.Prepare(true).SendAsync(CancellationToken.None);
        Require(reply.Calls.Count == 1 && reply.Calls[0].Name == "inspect-draft", "The first session lost its supplied tools");
        var secondReply = await second.Prepare(false).SendAsync(CancellationToken.None);
        first.AcceptResults(new[] { new AiToolResult(reply.Calls[0], new JObject { ["result"] = "first caller data" }) });
        var firstReply = await first.Prepare(false).SendAsync(CancellationToken.None);
        Require(firstReply.Text == "first done" && secondReply.Text == "second done" && handler.Requests.Count == 3,
            "Interleaving callers lost a continuation or shared an answer");
        Require((string?)firstSettings.Settings["marker"] == "first connection" && (string?)secondSettings.Settings["marker"] == "first connection",
            "Caller mutations leaked into provider connection snapshots");
        var requests = handler.Requests;
        Require(requests.Select(x => (string?)x["model"]).SequenceEqual(new[] { "model-A", "model-B", "model-A" }) &&
            requests.Select(x => (int?)x["max_tokens"]).SequenceEqual(new int?[] { 101, 202, 101 }), "A caller inherited the other connection or output budget");
        Require(((JArray)requests[0]["messages"]!).Count == 2 && ((JArray)requests[1]["messages"]!).Count == 2 &&
            ((JArray)requests[2]["messages"]!).Count == 4 && requests[1]["tools"] == null, "Histories or default adviser tools crossed sessions");
        Require((string?)requests[0]["messages"]![0]!["content"] == "First caller instructions" &&
            (string?)requests[1]["messages"]![0]!["content"] == "Second caller instructions" &&
            JObject.Parse((string)requests[0]["messages"]![1]!["content"]!)["purpose"]!.Value<string>() == "adviser" &&
            JObject.Parse((string)requests[1]["messages"]![1]!["content"]!)["purpose"]!.Value<string>() == "creator" &&
            !requests[2].ToString().Contains("Second caller", StringComparison.Ordinal), "Caller instructions or input data were rewritten or mixed");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly List<JObject> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JObject.Parse(await request.Content!.ReadAsStringAsync()); Requests.Add(body);
            var first = (string?)body["model"] == "model-A";
            Require(request.Headers.GetValues("Authorization").Single() == "Bearer " + (first ? "secret-A" : "secret-B"), "Credentials crossed caller boundaries");
            var needsTool = first && Requests.Count == 1;
            var message = new JObject { ["role"] = "assistant", ["content"] = needsTool ? "" : first ? "first done" : "second done" };
            if (needsTool) message["tool_calls"] = new JArray(new JObject { ["id"] = "first-call", ["type"] = "function",
                ["function"] = new JObject { ["name"] = "inspect-draft", ["arguments"] = "{}" } });
            var reply = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = needsTool ? "tool_calls" : "stop", ["message"] = message }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToString(Formatting.None), Encoding.UTF8, "application/json") };
        }
    }
}
