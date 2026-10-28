using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;

internal static partial class ProviderTests
{
    private static int ErrorCount(string text) => text.Split("===== Provider error #", StringSplitOptions.None).Length - 1;

    private static void RunErrorHistory(Action<string, Action> check)
    {
        check("Provider errors retain redacted complete responses without full logging across successive connection tests", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(Path.Combine(directory, "errors"));
                var configuration = RouterConfiguration("test");
                var unicodeKey = string.Concat(configuration.ApiKey.Select(c => "\\u" + ((int)c).ToString("x4")));
                var response = "{\"error\":{\"message\":\"認証失敗: " + unicodeKey + "\",\"code\":401}}";
                const string rawResponse = "  {\r\n\t\"error\" : { \"code\" : 401, \"message\" : \"line one\\nline two\", \"path\" : \"C:\\\\temp\\\\sample\" }\r\n}\n";
                for (var run = 0; run < 2; run++)
                {
                    var handler = new BackendHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(run == 0 ? response : rawResponse) }));
                    try
                    {
                        await AdvisorServices.TestAsync(directory, configuration, new AdvisorConnectionOptions(new JObject()), "Check", CancellationToken.None,
                            transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                        throw new Exception("Invalid authentication succeeded");
                    }
                    catch (AiConnectionException error)
                    {
                        Require(error.Code == "api_access_denied" && error.HttpStatus == 401 && error.HasDetails, "The original error or its history hint was lost");
                        foreach (var language in new[] { "en", "ja" })
                        {
                            var text = AdvisorText.Load(language); var notice = AdvisorNotice.Connection(error);
                            Require(notice.Message(text).Contains(text["errorlog.hint"], StringComparison.Ordinal) &&
                                notice.Message(text, true).Contains(text["errorlog.test_hint"], StringComparison.Ordinal), "An error with retained details did not guide the user to them");
                        }
                    }
                    Require(handler.Disposed, "Connection testing leaked its transport");
                }
                var full = history.ReadAll(); var file = File.ReadAllText(history.FilePath);
                Require(ErrorCount(full) == 2 && full.Contains("connection_name: " + configuration.Name, StringComparison.Ordinal) &&
                    full.Contains("provider_id: openrouter", StringComparison.Ordinal) && full.Contains("model: " + configuration.Model, StringComparison.Ordinal) &&
                    full.Contains("operation: connection_test", StringComparison.Ordinal), "Connection identity or chronological errors were lost");
                Require(full.Contains("認証失敗", StringComparison.Ordinal) && full.Contains("<api-key-removed>", StringComparison.Ordinal) &&
                    !file.Contains(configuration.ApiKey, StringComparison.Ordinal) && !file.Contains(unicodeKey, StringComparison.Ordinal) &&
                    !file.Contains("Authorization", StringComparison.Ordinal), "Full error details were lost or a credential/header entered history");
                Require(full.Contains(rawResponse, StringComparison.Ordinal), "The response body's whitespace, quotes or escapes were rewritten");
                Require(!File.Exists(Path.Combine(directory, "ai-communication.jsonl")), "Error history enabled the optional full log");
                Require(full == file && full.IndexOf("error #1", StringComparison.Ordinal) < full.IndexOf("error #2", StringComparison.Ordinal), "History changed file contents or chronological order");
            });
        }));
        check("Fallback keeps the failed main attempt after success and keeps both failures if the sub also fails", () => Async(async () =>
        {
            foreach (var subFails in new[] { false, true }) await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(directory); var draft = RouterDraft();
                var sub = new AdvisorApiConfiguration("sub", "Paid connection", "openrouter", new JObject
                    { ["api_key"] = "mock_sub_router_secret", ["model"] = "other/sub-model" });
                draft.Put(new AdvisorApiConfigurationDraft(sub)); draft.SubId = sub.Id;
                var sends = 0; var handler = new BackendHttpHandler((_, _) =>
                {
                    sends++;
                    if (sends == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("primary limited mock_router_secret") });
                    return Task.FromResult(subFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("sub busy mock_sub_router_secret") } : JsonResponse(RouterReply("Recovered")));
                });
                var setup = AdvisorServices.Prepare(draft, transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                try { Require((await RunPrepared(setup)).Text == "Recovered" && !subFails, "Unexpected fallback outcome"); }
                catch (AiConnectionException error) { Require(subFails && error.UsingFallback && error.HasDetails && error.HttpStatus == 503, "Sub failure identity/details were lost"); }
                var raw = history.ReadAll();
                Require(sends == 2 && ErrorCount(raw) == (subFails ? 2 : 1) && raw.Contains("key_slot: main", StringComparison.Ordinal) &&
                    raw.Contains("http_status: 429", StringComparison.Ordinal), "The primary failure disappeared or was recorded twice");
                if (subFails) Require(raw.Contains("connection_name: " + sub.Name, StringComparison.Ordinal) && raw.Contains("key_slot: sub", StringComparison.Ordinal) &&
                    raw.Contains("model: " + sub.Model, StringComparison.Ordinal), "The sub failure used the main connection metadata");
                Require(!raw.Contains("mock_router_secret", StringComparison.Ordinal) && !raw.Contains("mock_sub_router_secret", StringComparison.Ordinal), "Fallback history exposed a credential");
            });
        }));
        check("A sub-adapter failure before HTTP gets its own connection details rather than reusing the main response", () => Async(async () =>
        {
            var broken = new SeaPowerAIAdvisor.ExtensionFixture.StandardExtension
                { Open = (_, _, _, _) => throw new Api.AdvisorException("model_unavailable", 404) };
            using var host = new PublicHost(broken);
            await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(directory); var draft = RouterDraft();
                var sub = ExternalConfiguration(broken, "broken-sub"); draft.Put(new AdvisorApiConfigurationDraft(sub)); draft.SubId = sub.Id;
                var sends = 0; var handler = new BackendHttpHandler((_, _) =>
                { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("primary limited") }); });
                try
                {
                    await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history));
                    throw new Exception("Broken sub adapter succeeded");
                }
                catch (AiConnectionException error) { Require(error.Code == "model_unavailable" && error.UsingFallback && error.HasDetails, "Sub startup failure lost its identity"); }
                var raw = history.ReadAll();
                var subDetails = raw.Substring(raw.IndexOf("===== Provider error #2", StringComparison.Ordinal));
                Require(sends == 1 && ErrorCount(raw) == 2 && subDetails.Contains("connection_id: " + sub.Id, StringComparison.Ordinal) &&
                    subDetails.Contains("response_received: false", StringComparison.Ordinal) && subDetails.Contains("http_status: 404", StringComparison.Ordinal) &&
                    !subDetails.Contains("primary limited", StringComparison.Ordinal), "Sub startup inherited the main response or disappeared from history");
            });
        }));
        check("HTTP 200 protocol failures keep their response while successful probes keep the log empty", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(directory); var handler = new BackendHttpHandler((_, _) => Task.FromResult(JsonResponse(RouterReply("wrong probe reply"))));
                try
                {
                    await AdvisorServices.TestAsync(directory, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), "Check", CancellationToken.None,
                        transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                    throw new Exception("Missing tool call succeeded");
                }
                catch (AiConnectionException error) { Require(error.Code == "tool_call_test_failed" && error.HasDetails, "Semantic failure did not expose details"); }
                var raw = history.ReadAll();
                Require(ErrorCount(raw) == 1 && raw.Contains("http_status: 200", StringComparison.Ordinal) &&
                    raw.Contains("wrong probe reply", StringComparison.Ordinal), "A successful HTTP status hid the provider failure response");
            });
            await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(directory); var calls = 0;
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    if (++calls == 1) return JsonResponse(RouterReply("", RouterCall("probe", "", "connection_check", new JObject())));
                    var result = JObject.Parse((string)body["messages"]!.Last!["content"]!);
                    return JsonResponse(RouterReply((string)result["confirmation"]!));
                });
                await AdvisorServices.TestAsync(directory, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), "Check", CancellationToken.None,
                    transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                Require(File.Exists(history.FilePath) && history.ReadAll() == "", "A successful probe was saved as an error");
            });
        }));
        check("Timeouts and network errors retain available details while user cancellation adds no error", () => Async(async () =>
        {
            foreach (var mode in new[] { "network", "timeout", "cancel" }) await InServiceDirectory(async directory =>
            {
                var history = new AdvisorErrorHistory(directory); using var cancellation = new CancellationTokenSource();
                if (mode == "cancel") cancellation.CancelAfter(30);
                var handler = new BackendHttpHandler(async (_, token) =>
                {
                    if (mode == "network") throw new HttpRequestException("network detail mock_router_secret");
                    await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable");
                });
                try
                {
                    await AdvisorServices.TestAsync(directory, RouterConfiguration("router", 1), new AdvisorConnectionOptions(new JObject()), "Check", cancellation.Token,
                        transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                    throw new Exception("Failed transport succeeded");
                }
                catch (OperationCanceledException) { Require(mode == "cancel", "Timeout became cancellation"); }
                catch (AiConnectionException error) { Require(mode != "cancel" && error.Code == (mode == "timeout" ? "request_timeout" : "network_error"), "Transport failure was reclassified"); }
                Require(ErrorCount(history.ReadAll()) == (mode == "cancel" ? 0 : 1), "Cancellation created an error or transport failure was lost");
                if (mode != "cancel")
                {
                    var detail = history.ReadAll();
                    Require(detail.Contains("response_received: false", StringComparison.Ordinal) && detail.Contains("(no response body received)", StringComparison.Ordinal) && !detail.Contains("mock_router_secret", StringComparison.Ordinal), "Missing responses or transport credentials were misrepresented");
                    if (mode == "network") Require(detail.Contains("network detail", StringComparison.Ordinal), "The underlying network exception was discarded");
                }
            });
        }));
        check("An error-log write failure is visible without replacing the provider error or disabling consultation fallback", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var warnings = new System.Collections.Generic.List<string>(); var history = new AdvisorErrorHistory(directory, warnings.Add);
                File.Delete(history.FilePath); Directory.CreateDirectory(history.FilePath);
                var handler = new BackendHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied") }));
                try
                {
                    await AdvisorServices.TestAsync(directory, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), "Check", CancellationToken.None,
                        transportFactory: trace => new AiHttpTransport(handler, trace), errorHistory: history);
                    throw new Exception("Provider failure disappeared");
                }
                catch (AiConnectionException error) { Require(error.Code == "api_access_denied" && error.HttpStatus == 401 && error.HasDetails, "Storage failure replaced the provider exception"); }
                Require(history.StorageError.Length > 0 && warnings.Count == 1 && !File.Exists(history.FilePath), "Storage failure was silently ignored or retried repeatedly");
            });
        }));
        check("A new run clears old errors immediately, reads externally appended text and preserves it when provider errors arrive", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var file = Path.Combine(directory, "provider-errors.log"); File.WriteAllText(file, "previous game log");
                var history = new AdvisorErrorHistory(directory);
                Require(history.ReadAll() == "" && File.ReadAllText(file) == "", "A new run did not clear the previous log during initialization");
                const string dummy = "Dummy error added externally.\n";
                File.AppendAllText(file, dummy);
                Require(history.ReadAll() == dummy, "An external error was hidden before the first provider failure");
                var configuration = RouterConfiguration("router"); var service = new BuiltInAdvisorService("openrouter");
                for (var i = 0; i < 3; i++)
                {
                    using var transport = new AiHttpTransport(new BackendHttpHandler((_, _) => throw new Exception("No network expected")));
                    transport.ObserveErrors(new AdvisorErrorCapture(history, new AdvisorErrorConnection(service, configuration), null, "consultation", service.Secrets(configuration)));
                    var settings = new Api.ConnectionSettings(configuration.Id, configuration.Name, service.Id, "{}", configuration.Model, 60, 4096, false);
                    var services = new AdvisorExtensionServices(transport, settings);
                    var id = services.BeginRequest("{}"); var body = new string('X', 100_000) + " mock_router_secret " + i;
                    services.EndRequest(id, 503, body, complete: false);
                }
                var raw = history.ReadAll();
                Require(ErrorCount(raw) == 3 && raw.StartsWith(dummy, StringComparison.Ordinal), "Recording the first provider error discarded externally appended text");
                Require(raw == File.ReadAllText(history.FilePath), "The viewer changed the raw file contents");
                for (var i = 0; i < 3; i++) Require(raw.Contains(new string('X', 100_000) + " <api-key-removed> " + i, StringComparison.Ordinal) &&
                    raw.Contains("body_complete: false", StringComparison.Ordinal), "File-backed history truncated or mixed response bodies");
                var next = new AdvisorErrorHistory(directory);
                Require(next.ReadAll() == "" && File.ReadAllText(file) == "", "A new run left the previous run's log in the file");
                next.Append(new JObject { ["error_code"] = "new_run" });
                Require(ErrorCount(next.ReadAll()) == 1 && !File.ReadAllText(next.FilePath).Contains("XXXXX", StringComparison.Ordinal), "The next run appended to the previous game's log");
            });
        }));
    }
}
