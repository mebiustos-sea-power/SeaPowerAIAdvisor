using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Configuration;
using SeaPowerAIMissionCreator.Connections;
using SeaPowerAIMissionCreator.Development;

internal static class CreatorConnectionTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Creator connection storage is independent and never copies Advisor settings", () => InTemp(directory =>
        {
            var advisor = Path.Combine(directory, "SeaPowerAIAdvisor"); Directory.CreateDirectory(advisor);
            var sentinel = Encoding.UTF8.GetBytes("ADVISOR_SETTINGS_MUST_NOT_CHANGE"); File.WriteAllBytes(Path.Combine(advisor, "api-settings.json"), sentinel);
            var store = new MissionCreatorSettingsStore(Path.Combine(directory, "SeaPowerAIMissionCreator"));
            Require(store.Current.Connections.Count == 0 && store.Current.SelectedId == null && !store.Current.DeveloperApiEnabled && !File.Exists(store.FilePath), "Creator imported credentials, enabled its API or wrote empty settings");
            var first = Add(store, "Zulu"); var second = Add(store, "Alpha"); store.Select(first.Id); store.SaveDeveloper(true, 19991);
            var restored = new MissionCreatorSettingsStore(Path.GetDirectoryName(store.FilePath)!);
            Require(restored.ReadConnections().Select(c => c.Name).SequenceEqual(new[] { "Alpha", "Zulu" }) && restored.Current.SelectedId == first.Id && restored.Current.DeveloperApiEnabled && restored.Current.DeveloperApiPort == 19991 &&
                File.ReadAllBytes(Path.Combine(advisor, "api-settings.json")).SequenceEqual(sentinel), "Connection data, selection or developer settings crossed feature boundaries");
            store.Duplicate(first.Id, " - Copy"); var copy = store.ReadConnections().Single(c => c.Id != first.Id && c.Id != second.Id);
            Require(copy.Name == "Zulu - Copy" && copy.Prompt == "FEATURE_ONLY_PROMPT" && copy.ApiKey == first.ApiKey && store.Current.SelectedId == first.Id, "Duplicate copied a selection or lost saved values");
            store.Remove(first.Id); Require(store.Current.SelectedId == null && store.Current.Connections.Count == 2, "Deleting selected credentials silently picked another connection");
        }));
        check("Creator rejects invalid connection edits and malformed documents without writing partial settings", () => InTemp(directory =>
        {
            var store = new MissionCreatorSettingsStore(directory); var saved = Add(store, "Saved"); var before = File.ReadAllBytes(store.FilePath);
            var draft = store.Edit(saved.Id); draft.Model = "invalid model"; Throws("nanogpt_model_invalid", () => store.Save(draft, false));
            Throws("developer_port_invalid", () => store.SaveDeveloper(true, 80));
            Require(File.ReadAllBytes(store.FilePath).SequenceEqual(before) && store.Current.Require(saved.Id).Model == saved.Model, "A rejected edit changed settings");
            File.WriteAllText(store.FilePath, "{\"schema_version\":1,\"schema_version\":1}"); Throws("settings_invalid", () => new MissionCreatorSettingsStore(directory));
        }));
        check("Creator UI and API share a detached tool-capable connection test and keep prompts and credentials private", () => Async(async () =>
        {
            await InTempAsync(async directory =>
            {
                var store = new MissionCreatorSettingsStore(directory); var profile = Add(store, "Selected"); store.Select(profile.Id); var before = File.ReadAllBytes(store.FilePath);
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
                using var service = new MissionCreatorConnections(store, error => throw new InvalidOperationException("Unexpected test failure", error), () => new AiHttpTransport(new Handler(async (request, cancellation) =>
                {
                    Interlocked.Increment(ref count); var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                    Require(request.RequestUri!.AbsoluteUri == "https://nano-gpt.com/api/subscription/v1/chat/completions" && request.Headers.Authorization?.Parameter == "creator_mock_secret" &&
                        (int?)body["max_tokens"] == 1234 && !body.ToString().Contains("FEATURE_ONLY_PROMPT", StringComparison.Ordinal), "Connection probe changed endpoint/credentials/budget or included a feature prompt");
                    await hold.Task.WaitAsync(cancellation);
                    return Probe(body);
                })));
                var api = new MissionCreatorLocalApi(service); var run = service.StartTest();
                Require((await api.Dispatch("POST", "/test", new JObject())).Status == 409, "API bypassed the UI test's concurrency guard");
                hold.SetResult(); await run.Completion;
                var status = await api.Dispatch("GET", "/status", new JObject()); var discovery = await api.Dispatch("GET", "/connections", new JObject());
                Require((string?)status.Data["test"]?["run_id"] == run.Id && (string?)status.Data["test"]?["state"] == "completed" && (int?)status.Data["test"]?["api_requests"] == 2 &&
                    (long?)status.Data["test"]?["total_tokens"] == 20 && !discovery.Data.ToString().Contains("creator_mock_secret", StringComparison.Ordinal) && !discovery.Data.ToString().Contains("FEATURE_ONLY_PROMPT", StringComparison.Ordinal), "UI/API result accounting diverged or discovery disclosed private values");
                var second = await api.Dispatch("POST", "/test", new JObject { ["connection_id"] = profile.Id }); await service.Last!.Completion;
                Require(second.Status == 202 && count == 4 && File.ReadAllBytes(store.FilePath).SequenceEqual(before), "API used another execution path or persisted temporary test state");
                Require((await api.Dispatch("POST", "/test", new JObject { ["model"] = "unapproved override" })).Status == 400, "Unspecified request fields were silently accepted");
            });
        }));
        check("Creator connection tests expose provider failure without fallback or saved changes", () => Async(async () =>
        {
            await InTempAsync(async directory =>
            {
                var store = new MissionCreatorSettingsStore(directory); var profile = Add(store, "Limited"); var before = File.ReadAllBytes(store.FilePath); var calls = 0;
                using var service = new MissionCreatorConnections(store, _ => { }, () => new AiHttpTransport(new Handler((request, cancellation) =>
                { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") }); })));
                var run = service.StartTest(profile.Id); try { await run.Completion; } catch (AiConnectionException) { }
                Require((string?)run.Status()["state"] == "failed" && (string?)run.Status()["error"] == "rate_limited" && calls == 1 && File.ReadAllBytes(store.FilePath).SequenceEqual(before), "Provider error was hidden, retried or persisted");
            });
        }));
        check("Creator cancellation stops the active API test and leaves the saved connection intact", () => Async(async () =>
        {
            await InTempAsync(async directory =>
            {
                var store = new MissionCreatorSettingsStore(directory); var profile = Add(store, "Cancel"); var before = File.ReadAllBytes(store.FilePath);
                using var service = new MissionCreatorConnections(store, _ => { }, () => new AiHttpTransport(new Handler(async (_, token) =>
                { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Cancellation was ignored"); })));
                var api = new MissionCreatorLocalApi(service); await api.Dispatch("POST", "/test", new JObject { ["connection_id"] = profile.Id });
                await api.Dispatch("POST", "/test/cancel", new JObject()); try { await service.Last!.Completion; } catch (OperationCanceledException) { }
                Require((string?)service.Last!.Status()["state"] == "cancelled" && File.ReadAllBytes(store.FilePath).SequenceEqual(before), "Cancellation became a failure or changed saved settings");
            });
        }));
        check("Shared HTTP servers coexist with distinct endpoints and request headers, and reject duplicate bindings", () => Async(async () =>
        {
            var advisorEndpoint = Endpoint(); var creatorEndpoint = Endpoint(); while (creatorEndpoint == advisorEndpoint) creatorEndpoint = Endpoint();
            using var advisor = new LocalApiServer((_, _, _) => Task.FromResult(new LocalApiResponse(200, new JObject { ["feature"] = "advisor" })), _ => { }, advisorEndpoint, "X-Advisor-Developer");
            using var creator = new LocalApiServer((_, _, _) => Task.FromResult(new LocalApiResponse(200, new JObject { ["feature"] = "creator" })), _ => { }, creatorEndpoint, "X-MissionCreator-Developer");
            advisor.Start(); creator.Start();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            async Task<HttpResponseMessage> Send(string endpoint, string header)
            { using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/status"); request.Headers.Add(header, "1"); return await client.SendAsync(request); }
            using var a = await Send(advisorEndpoint, "X-Advisor-Developer"); using var c = await Send(creatorEndpoint, "X-MissionCreator-Developer"); using var wrong = await Send(creatorEndpoint, "X-Advisor-Developer");
            Require(a.IsSuccessStatusCode && c.IsSuccessStatusCode && (string?)JObject.Parse(await a.Content.ReadAsStringAsync())["feature"] == "advisor" &&
                (string?)JObject.Parse(await c.Content.ReadAsStringAsync())["feature"] == "creator" && wrong.StatusCode == HttpStatusCode.Forbidden, "Ports, feature identity or request headers crossed servers");
            using var duplicate = new LocalApiServer((_, _, _) => Task.FromResult(new LocalApiResponse(200, new JObject())), _ => { }, creatorEndpoint, "X-MissionCreator-Developer");
            try { duplicate.Start(); } catch (HttpListenerException) { return; }
            throw new InvalidOperationException("A duplicate endpoint was accepted");
        }));
    }
    private static AiConnectionProfile Add(MissionCreatorSettingsStore store, string name)
    {
        var draft = store.Create("Connection {0}"); draft.Name = name; draft.Provider = "nanogpt"; draft.Settings = AiProviderDefinition.Require("nanogpt").Defaults();
        draft.ApiKey = "creator_mock_secret"; draft.MaxOutputTokens = 1234; draft.Prompt = "FEATURE_ONLY_PROMPT"; store.Save(draft, true); return store.Current.Require(draft.Id);
    }
    private static HttpResponseMessage Probe(JObject body)
    {
        var messages = (JArray)body["messages"]!; var toolResult = messages.OfType<JObject>().LastOrDefault(m => (string?)m["role"] == "tool");
        var message = toolResult == null ? new JObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JArray(new JObject
        { ["id"] = "probe", ["type"] = "function", ["function"] = new JObject { ["name"] = "connection_check", ["arguments"] = "{}" } }) } :
            new JObject { ["role"] = "assistant", ["content"] = (string)JObject.Parse((string)toolResult["content"]!)["confirmation"]! };
        var response = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = toolResult == null ? "tool_calls" : "stop", ["message"] = message }), ["usage"] = new JObject { ["total_tokens"] = 10 } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToString(Formatting.None), Encoding.UTF8, "application/json") };
    }
    private static string Endpoint()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return "http://127.0.0.1:" + port; }
    private static void InTemp(Action<string> action) { var directory = Path.Combine(Path.GetTempPath(), "creator-connections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); try { action(directory); } finally { Directory.Delete(directory, true); } }
    private static Task InTempAsync(Func<string, Task> action) { InTemp(directory => action(directory).GetAwaiter().GetResult()); return Task.CompletedTask; }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Throws(string code, Action action) { try { action(); } catch (AiConnectionException error) when (error.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
