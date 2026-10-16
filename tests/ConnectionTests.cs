using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ConnectionTests
{
    private const string Key = "mock_connection_test_key_never_sent";
    internal static void Run(Action<string, Action> check)
    {
        check("Connection testing uses the pending key and model in one isolated generation without saving settings", () => Async(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-connection-" + Guid.NewGuid().ToString("N"));
            var draft = new AdvisorSettingsDraft(new JObject { ["api_key"] = Key, ["model"] = "gemini-user-model" });
            draft.ApiKey = "changed_mock_key"; draft.Model = "gemini-another-model"; draft.LogAiJson = true;
            var handler = new Handler(async (request, token) =>
            {
                Require(request.RequestUri!.AbsolutePath.EndsWith("/gemini-another-model:generateContent", StringComparison.Ordinal) &&
                    request.Headers.GetValues("x-goog-api-key").Single() == "changed_mock_key", "Saved credentials were used instead of the pending edits");
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.Properties().Select(p => p.Name).OrderBy(x => x).SequenceEqual(new[] { "contents", "generationConfig" }) &&
                    body["contents"] is JArray contents && contents.Count == 1 && contents[0]!["parts"] is JArray parts && parts.Count == 1 &&
                    (string?)parts[0]!["text"] == "Connection test. Reply only with OK.",
                    "A connection check included game data, tools, personality or conversation history");
                return Response(Reply());
            });
            var settings = draft.TestSettings();
            Require(!settings.LogAiJson, "The test applied an unsaved logging preference");
            var result = await GeminiConnectionTest.RunAsync(path, settings, CancellationToken.None, trace => new GeminiTransport(handler, trace));
            Require(result.ApiRequests == 1 && result.QueryRounds == 0 && handler.Calls == 1 && result.Text == "OK" && !Directory.Exists(path),
                "The check made extra requests, created files while logging is disabled, or failed to verify generation");
        }));
        check("Connection checks reject missing settings and provider failures instead of reporting false success", () => Async(async () =>
        {
            Throws("api_key_missing", () => new AdvisorSettingsDraft(new JObject()).TestSettings());
            Throws("model_invalid", () => new AdvisorSettingsDraft(new JObject { ["api_key"] = Key, ["model"] = "" }).TestSettings());
            foreach (var pair in new[] { (400, "api_bad_request"), (403, "api_access_denied"), (404, "model_unavailable"), (429, "rate_limited"), (503, "api_error") })
                await Fails(pair.Item2, () => Run(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)pair.Item1)
                    { Content = new StringContent("{\"error\":\"rejected\"}") }))));
            await Fails("response_blocked", () => Run(new Handler((_, _) => Task.FromResult(Response(new JObject { ["candidates"] = new JArray() })))));
            var truncated = Reply(); truncated["candidates"]![0]!["finishReason"] = "MAX_TOKENS";
            await Fails("output_limit", () => Run(new Handler((_, _) => Task.FromResult(Response(truncated)))));
            var thought = Reply(); thought["candidates"]![0]!["content"]!["parts"]![0]!["thought"] = true;
            await Fails("empty_response", () => Run(new Handler((_, _) => Task.FromResult(Response(thought)))));
        }));
        check("Connection tests distinguish timeouts from explicit cancellation and do not retry automatically", () => Async(async () =>
        {
            var timedOut = new Handler((_, _) => throw new OperationCanceledException());
            await Fails("request_timeout", () => Run(timedOut));
            Require(timedOut.Calls == 1, "A timed-out check was retried automatically");
            using var cancellation = new CancellationTokenSource();
            var cancelled = new Handler((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
            try { await Run(cancelled, cancellation.Token); }
            catch (OperationCanceledException)
            {
                Require(cancelled.Calls == 1, "A cancelled check was retried");
                return;
            }
            throw new InvalidOperationException("Cancellation was shown as success or a provider error");
        }));
    }
    private static Task<AdviceResult> Run(Handler handler, CancellationToken cancellation = default) => GeminiConnectionTest.RunAsync(
        "unused-connection-test-directory", new AdvisorSettingsDraft(new JObject { ["api_key"] = Key }).TestSettings(), cancellation,
        trace => new GeminiTransport(handler, trace));
    private static JObject Reply() => new() { ["candidates"] = new JArray(new JObject { ["finishReason"] = "STOP",
        ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray(new JObject { ["text"] = "OK" }) } }) };
    private static HttpResponseMessage Response(JObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToString()) };
    private static async Task Fails(string code, Func<Task<AdviceResult>> action)
    {
        try { await action(); }
        catch (AdvisorConnectionException error) { Require(error.Code == code && !error.ToString().Contains(Key, StringComparison.Ordinal), "Wrong or unsafe error"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Throws(string code, Action action)
    {
        try { action(); }
        catch (AdvisorConnectionException error) { Require(error.Code == code, "Wrong validation error"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal int Calls;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
