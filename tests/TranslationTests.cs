using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Translation;

internal static class TranslationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Batch translation maps reordered replies by identity and preserves duplicate source blocks and line breaks in one request", () => Run(async () =>
        {
            var batch = new TranslationBatch(new[] { "Heading", "Same", "Same" }, "ja");
            var provider = new FakeProvider((input, _) =>
            {
                Require(input.Tools.Count == 1 && (string?)input.Tools[0]["name"] == TranslationBatch.ToolName && input.Data["texts"]!.Count() == 3,
                    "The batch was split or advisor tools entered translation");
                return Task.FromResult(Reply(new JArray(Row("t3", "同じ3"), Row("t1", "見出し\n改行"), Row("t2", "同じ2"))));
            });
            var result = await TranslationEngine.RunAsync(batch, provider, null, "", "", _ => { }, CancellationToken.None);
            Require(result.Texts.SequenceEqual(new[] { "見出し\n改行", "同じ2", "同じ3" }) && provider.Sends == 1 && provider.Disposed,
                "Translations were split heuristically, deduplicated, or followed by another completion request");
        }));
        check("Translation rejects missing, duplicate, foreign and extra IDs instead of displaying mismatched or partial text", () =>
        {
            var batch = new TranslationBatch(new[] { "First", "Second" }, "ja");
            foreach (var items in new[] { new JArray(Row("t1", "1")), new JArray(Row("t1", "1"), Row("t1", "2")),
                new JArray(Row("t1", "1"), Row("other", "2")), new JArray(Row("t1", "1"), Row("t2", "2"), Row("t3", "3")),
                new JArray(Row("t1", "1"), Row("t2", " ")) })
                Throws<TranslationException>(() => batch.Read(Reply(items)), e => e.Code == "invalid_response");
            Throws<TranslationException>(() => batch.Read(new AiReply("Unstructured translation", Array.Empty<AiToolCall>(), 1)), e => e.Code == "invalid_response");
        });
        check("Invalid translation submissions do not trigger a fallback or an automatic repeated request", () => Run(async () =>
        {
            var first = new FakeProvider((_, _) => Task.FromResult(Reply(new JArray(Row("unknown", "text")))));
            var sub = new FakeProvider((_, _) => throw new InvalidOperationException("Fallback was used for an invalid result"));
            try { await TranslationEngine.RunAsync(new TranslationBatch(new[] { "Source" }, "ja"), first, sub, "", "", _ => { }, CancellationToken.None); }
            catch (TranslationException error) { Require(error.Code == "invalid_response" && first.Sends == 1 && sub.Sends == 0 && first.Disposed, "Invalid result changed request routing"); return; }
            throw new InvalidOperationException("Invalid result was accepted");
        }));
        check("Translation cancellation reaches the active request and disposes its provider session without fallback", () => Run(async () =>
        {
            using var cancel = new CancellationTokenSource(); var entered = new TaskCompletionSource<bool>();
            var provider = new FakeProvider(async (_, token) => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); });
            var task = TranslationEngine.RunAsync(new TranslationBatch(new[] { "Source" }, "ja"), provider, null, "", "", _ => { }, cancel.Token);
            await entered.Task; cancel.Cancel();
            try { await task; } catch (OperationCanceledException) { Require(provider.Disposed && provider.Sends == 1, "Cancelled translation leaked its session"); return; }
            throw new InvalidOperationException("Cancellation did not reach the request");
        }));
        check("Translation uses its own selected connection and prompt while leaving advisor assignments and saved profiles unchanged", () => Run(async () =>
        {
            var settings = Settings(); settings.TranslationId = "translation"; var before = settings.Data(); var requests = 0;
            var batch = new TranslationBatch(new[] { "Objective", "Background" }, "ja");
            var result = await AdvisorTranslationService.RunAsync(settings, "unused", batch, CancellationToken.None, transportFactory: _ => new AiHttpTransport(new Handler(async (request, token) =>
            {
                requests++; var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                Require((string?)body["model"] == "deepseek/translation" && (string)body["messages"]![0]!["content"]! is var system &&
                    system.EndsWith("TRANSLATION_SUPPLEMENT", StringComparison.Ordinal) && !system.Contains("ADVISOR_SUPPLEMENT", StringComparison.Ordinal),
                    "The wrong connection prompt or model was sent");
                var sent = JObject.Parse((string)body["messages"]![1]!["content"]!);
                Require(sent.Count == 2 && (string?)sent["target_language"] == "ja" && sent["texts"]!.Count() == 2 && body["tools"]!.Count() == 1,
                    "Advisor history, persona, situation or commands entered the translation request");
                return Response(new JArray(Row("t2", "背景"), Row("t1", "目的")));
            })));
            Require(result.Texts.SequenceEqual(new[] { "目的", "背景" }) && requests == 1 && settings.Data().SequenceEqual(before),
                "Batch translation required another completion or changed saved settings");
        }));
        check("The translation role follows a changed advisor assignment, round-trips explicit choices and avoids using the same fallback twice", () => Run(async () =>
        {
            var settings = Settings(); settings.TranslationId = AdvisorApiLibrary.FollowAdvisor;
            settings = new AdvisorSettingsDraft(JObject.Parse(Encoding.UTF8.GetString(settings.Data())));
            Require(settings.Translation?.Id == "advisor", "The saved translation role did not follow advisor");
            settings.MainId = "translation"; Require(settings.Translation?.Id == "translation", "Following copied a stale advisor ID");
            settings.TranslationId = "fallback"; settings = new AdvisorSettingsDraft(JObject.Parse(Encoding.UTF8.GetString(settings.Data())));
            Require(settings.TranslationId == "fallback" && settings.MainId == "translation" && settings.SubId == "fallback", "Translation assignment did not survive serialization");
            var sends = 0;
            try
            {
                await AdvisorTranslationService.RunAsync(settings, "unused", new TranslationBatch(new[] { "Source" }, "ja"), CancellationToken.None,
                    transportFactory: _ => new AiHttpTransport(new Handler((_, _) => { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") }); })));
            }
            catch (AiConnectionException error) { Require(error.HttpStatus == 429 && sends == 1, "The selected fallback connection retried itself"); return; }
            throw new InvalidOperationException("Service error was swallowed");
        }));
        check("Translation fallback shares the existing routing policy and uses the fallback connection's prompt", () => Run(async () =>
        {
            var settings = Settings(); settings.TranslationId = "translation"; var sends = new List<string>();
            var result = await AdvisorTranslationService.RunAsync(settings, "unused", new TranslationBatch(new[] { "Source" }, "ja"), CancellationToken.None,
                transportFactory: _ => new AiHttpTransport(new Handler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends.Add((string)body["model"]!);
                    if (sends.Count == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                    Require(((string)body["messages"]![0]!["content"]!).EndsWith("FALLBACK_SUPPLEMENT", StringComparison.Ordinal), "Fallback used the first connection's supplement");
                    return Response(new JArray(Row("t1", "訳文")));
                })));
            Require(sends.SequenceEqual(new[] { "deepseek/translation", "deepseek/fallback" }) && result.ApiRequests == 2 && result.TotalTokens == null,
                "Fallback policy reset accounting or hid uncertainty");
        }));
        check("Translation defaults disabled, refuses requests before transport creation, persists choices and disables a deleted connection", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "translation-setting-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "api-settings.json"); var store = new AdvisorApiSettingsStore(path, () => "Connection {0}");
                Require(store.Read().Translation == null && !File.Exists(path), "Opening settings enabled or persisted translation");
                AdvisorSettings.WriteBytes(path, Settings().Data());
                var transports = 0;
                Throws<TranslationException>(() => AdvisorTranslationService.RunAsync(store.Read(), "unused", new TranslationBatch(new[] { "Source" }, "ja"), CancellationToken.None,
                    transportFactory: _ => { transports++; throw new InvalidOperationException("Disabled translation opened transport"); }).GetAwaiter().GetResult(), e => e.Code == "disabled");
                Require(transports == 0, "Disabled translation created a transport");
                store.Assign("advisor", "fallback", AdvisorApiLibrary.FollowAdvisor);
                Require(store.Read().Translation?.Id == "advisor", "Follow-advisor selection was not saved");
                store.Assign("translation", "fallback", AdvisorApiLibrary.FollowAdvisor);
                Require(store.Read().Translation?.Id == "translation", "Translation retained the previous advisor assignment");
                store.Assign("advisor", "fallback", "translation"); store.SaveLogging(true);
                var entry = store.Read().Find("translation")!; entry.Name = "Renamed translation"; store.SaveEntry(entry, false);
                Require(store.Read().Translation?.Id == "translation", "Another connection edit reset the translation choice");
                store.Remove("translation");
                Require(store.Read().TranslationId == null && store.Read().Translation == null, "Deleting the assigned connection silently enabled another connection");
                store.Assign("advisor", "fallback", AdvisorApiLibrary.FollowAdvisor);
                store.Assign("advisor", "fallback", null);
                Require(store.Read().Translation == null && store.Read().MainId == "advisor" && store.Read().SubId == "fallback", "Disabling translation changed other assignments");
            }
            finally { Directory.Delete(directory, true); }
        });
    }
    private static AdvisorSettingsDraft Settings()
    {
        var settings = new AdvisorSettingsDraft(new JObject());
        foreach (var id in new[] { "advisor", "translation", "fallback" })
            settings.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, id, "nanogpt",
                new JObject { ["api_key"] = "mock_no_network_" + id, ["model"] = "deepseek/" + id }, prompt: id.ToUpperInvariant() + "_SUPPLEMENT")));
        settings.MainId = "advisor"; settings.SubId = "fallback"; return settings;
    }
    private static JObject Row(string id, string text) => new() { ["id"] = id, ["text"] = text };
    private static AiReply Reply(JArray items) => new("", new[] { new AiToolCall("submitted", TranslationBatch.ToolName, new JObject { ["translations"] = items }) }, 7);
    private static HttpResponseMessage Response(JArray items) => new(HttpStatusCode.OK) { Content = new StringContent(new JObject {
        ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["role"] = "assistant", ["tool_calls"] = new JArray(new JObject {
            ["id"] = "submitted", ["type"] = "function", ["function"] = new JObject { ["name"] = TranslationBatch.ToolName,
            ["arguments"] = new JObject { ["translations"] = items }.ToString(Newtonsoft.Json.Formatting.None) } }) }, ["finish_reason"] = "tool_calls" }),
        ["usage"] = new JObject { ["total_tokens"] = 7 } }.ToString()) };
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, Func<T, bool> valid) where T : Exception
    { try { action(); } catch (T error) { Require(valid(error), "Wrong error"); return; } throw new InvalidOperationException("Expected failure was accepted"); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class FakeProvider : IAiProvider, IAiProviderSession
    {
        private readonly Func<AiProviderInput, CancellationToken, Task<AiReply>> send;
        private AiProviderInput input = null!;
        internal int Sends; internal bool Disposed;
        public string Model => "mock";
        internal FakeProvider(Func<AiProviderInput, CancellationToken, Task<AiReply>> send) { this.send = send; }
        public IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation) { this.input = input; return this; }
        public AiProviderRequest Prepare(bool allowTools) { Require(allowTools, "Translation tools were disabled"); return new AiProviderRequest(100, token => { Sends++; return send(input, token); }); }
        public void AcceptResults(IReadOnlyList<AiToolResult> results) => throw new InvalidOperationException("A terminal submission requested another round");
        public void Dispose() { Disposed = true; }
    }
}
