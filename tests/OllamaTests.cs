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

internal static partial class ProviderTests
{
    private static void RunOllama(Action<string, Action> check)
    {
        check("Legacy and registered Gemini settings migrate without altering saved bytes, credentials, IDs, names or assignments", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                foreach (var version in new[] { 0, 2 })
                {
                    var old = new JObject { ["api_key"] = "mock_migration_main", ["fallback_api_key"] = "mock_migration_sub",
                        ["model"] = "gemini-original", ["fallback_model"] = "gemini-original-sub",
                        ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90, ["max_output_tokens"] = 2048, ["log_ai_json"] = false };
                    if (version == 2)
                    {
                        old = new JObject { ["schema_version"] = 2, ["main_api_id"] = "stable-main", ["sub_api_id"] = "stable-sub",
                            ["api_configurations"] = new JArray(
                                new JObject { ["id"] = "stable-main", ["name"] = "Free", ["api_key"] = "mock_migration_main", ["model"] = "gemini-original" },
                                new JObject { ["id"] = "stable-sub", ["name"] = "Paid", ["api_key"] = "mock_migration_sub", ["model"] = "gemini-original-sub" }),
                            ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90, ["max_output_tokens"] = 2048, ["log_ai_json"] = false };
                    }
                    var path = Path.Combine(directory, "migration.json"); File.WriteAllText(path, old.ToString()); var original = File.ReadAllBytes(path);
                    var draft = AdvisorSettings.Edit(path); var before = draft.Main.Build();
                    var handler = new BackendHttpHandler(async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                        Require(request.RequestUri!.Host == "generativelanguage.googleapis.com" && request.RequestUri.AbsolutePath == "/v1beta/interactions" &&
                            request.Headers.GetValues("x-goog-api-key").Single() == "mock_migration_main" && (string?)body["model"] == "gemini-original" &&
                            (int?)body["generation_config"]?["max_output_tokens"] == 2048, "Existing Gemini connection changed after migration");
                        return JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Original connection" }));
                    });
                    var registry = AdvisorServices.CreateRegistry(path, transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                    Require((await RunPrepared(registry.Prepare())).Text == "Original connection" && handler.Disposed && File.ReadAllBytes(path).SequenceEqual(original),
                        "Merely loading or using a legacy registration rewrote the user's file");
                    File.WriteAllBytes(path, draft.Data()); var after = AdvisorSettings.Edit(path);
                    Require(after.Main.Id == before.Id && after.Main.Name == before.Name && after.Main.ApiKey == before.ApiKey && after.Main.Model == before.Model &&
                        after.Main.Provider == "gemini" && after.Sub!.ApiKey == "mock_migration_sub" && after.Sub.Model == "gemini-original-sub" &&
                        after.Options.RequestTimeoutSeconds == 45 && after.Options.MaxOutputTokens == 2048,
                        "Saving migration lost a credential, identity, assignment or numeric option");
                }
            });
        }));
        check("Provider-specific registrations persist independently and unknown providers survive unrelated edits but cannot execute", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json"); var store = new AdvisorApiSettingsStore(path, () => "Connection {0}");
                var gemini = store.Read().Add(); gemini.ApiKey = "mock_preserved_key"; store.SaveEntry(gemini, true);
                var local = LocalConfiguration("local"); store.SaveEntry(new AdvisorApiConfigurationDraft(local), true); store.Assign(local.Id, gemini.Id);
                var unknown = new AdvisorApiConfiguration("unknown", "Extension", "future-provider", new JObject { ["opaque"] = new JObject { ["flag"] = true, ["array"] = new JArray(1, JValue.CreateNull(), "x") } });
                store.SaveEntry(new AdvisorApiConfigurationDraft(unknown), true); store.SaveLogging(true);
                var stored = store.Read(); Require(stored.Main.Provider == "ollama" && stored.Sub!.ApiKey == "mock_preserved_key" &&
                    JToken.DeepEquals(stored.Find(unknown.Id)!.Settings, unknown.Settings), "Unrelated edits lost registered values or unknown-provider settings");
                var pending = stored.Find(local.Id)!; pending.Model = "unsaved";
                Require(store.Read().Main.Model == OllamaService.DefaultModel, "An unconfirmed edit was persisted");
                store.Assign(unknown.Id, null); var before = File.ReadAllBytes(path);
                ExpectServiceError("provider_unavailable", () => AdvisorServices.Prepare(store.Read()));
                Require(File.ReadAllBytes(path).SequenceEqual(before), "Missing provider silently changed stored settings");
                await Task.CompletedTask;
            });
        }));
        check("Invalid Ollama URLs, models and context sizes fail before registration changes are written", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json"); var store = new AdvisorApiSettingsStore(path, () => "Connection {0}");
                var entry = new AdvisorApiConfigurationDraft(LocalConfiguration("local")); store.SaveEntry(entry, true); var before = File.ReadAllBytes(path);
                foreach (var value in new[] { "file:///C:/data", "http://user:secret@localhost:11434", "http://localhost:11434?token=secret", "http://localhost:11434/#fragment", "relative" })
                { entry.Settings["base_url"] = value; ExpectServiceError("server_url_invalid", () => store.SaveEntry(entry, false)); }
                entry.Settings["base_url"] = OllamaService.DefaultUrl;
                foreach (var value in new[] { "", "not a model", "bad\nmodel" })
                { entry.Model = value; ExpectServiceError("ollama_model_invalid", () => store.SaveEntry(entry, false)); }
                entry.Model = "user/model:tag";
                foreach (var value in new[] { "0", "262145", "not-an-integer" })
                { entry.Settings["num_ctx"] = value; ExpectServiceError("context_length_invalid", () => store.SaveEntry(entry, false)); }
                Require(File.ReadAllBytes(path).SequenceEqual(before), "A rejected provider setting changed storage");
                await Task.CompletedTask;
            });
        }));
        check("Ollama batches independent calls, returns correlated results, preserves thinking and stops tools at the retrieval limit", () => Async(async () =>
        {
            var sent = new List<JObject>(); var events = new List<AdvisorProgress>(); var knowledge = new Knowledge(1);
            var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sent.Add(body);
                Require(request.RequestUri!.AbsoluteUri == "http://localhost:11434/api/chat" && !request.Headers.Contains("x-goog-api-key") &&
                    !request.Headers.Contains("Authorization") && (bool?)body["stream"] == false &&
                    (int?)body["options"]?["num_ctx"] == 8192 && (int?)body["options"]?["num_predict"] == 4096,
                    "Ollama received credentials, an incorrect endpoint or unusable generation options");
                if (sent.Count == 1)
                {
                    var input = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require((string?)body["messages"]![0]!["role"] == "system" && (string?)input["user_name"] == "Commander" &&
                        (string?)input["reply_language"] == "ja" && (string?)body["tools"]![0]!["function"]!["name"] == ToolName,
                        "Local provider lost shared instructions, language, preferences or the retrieval tool");
                    return JsonResponse(LocalReply("", "PRIVATE_THINKING", LocalCall("catalog"), LocalCall("get_briefing")));
                }
                var messages = (JArray)body["messages"]!; var toolResults = messages.OfType<JObject>().Where(m => (string?)m["role"] == "tool").ToArray();
                Require(body["tools"] == null && (string?)messages[2]!["thinking"] == "PRIVATE_THINKING" && toolResults.Length == 2 &&
                    (string?)JObject.Parse((string)toolResults[0]["content"]!)["results"]![0]!["query"] == "catalog" &&
                    (string?)JObject.Parse((string)toolResults[1]["content"]!)["results"]![0]!["query"] == "get_briefing",
                    "Ollama tool results, thinking history or final-answer tool restriction were lost");
                return JsonResponse(LocalReply("Local advice"));
            });
            var setup = AdvisorServices.Prepare(LocalDraft(), transportFactory: trace => new AdvisorHttpTransport(handler, trace));
            var result = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(),
                userName: "Commander", progress: events.Add), "unused-ollama-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(result.Text == "Local advice" && result.ApiRequests == 2 && result.QueryRounds == 1 && result.TotalTokens == 220 && handler.Disposed &&
                events.Any(e => e.Stage == AdvisorStage.Retrieving) && events.Last().Stage == AdvisorStage.FinalAnswer,
                "Ollama exposed hidden reasoning, lost progress/accounting or leaked its transport");
        }));
        check("Local provider retrieval cannot bypass a disabled knowledge category", () => Async(async () =>
        {
            var sends = 0; var knowledge = new Knowledge(1, AdvisorInformation.Default.With(InformationKind.BattleBriefing, false));
            var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                if (++sends == 1) return JsonResponse(LocalReply("", null, LocalCall("get_briefing")));
                var messages = (JArray)body["messages"]!;
                var result = JObject.Parse((string)messages.Last!["content"]!);
                Require((string?)result["error"]?["code"] == "information_disabled" && !result.ToString().Contains("Public briefing", StringComparison.Ordinal),
                    "Ollama received excluded data through the knowledge gateway");
                return JsonResponse(LocalReply("Category excluded"));
            });
            var setup = AdvisorServices.Prepare(LocalDraft(), transportFactory: trace => new AdvisorHttpTransport(handler, trace));
            var result = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()),
                "unused-ollama-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(result.Text == "Category excluded" && knowledge.RoundsUsed == 1, "Disabled retrieval was not bounded");
        }));
        check("Gemini-to-Ollama handoff preserves retrieved evidence without transferring Gemini IDs or credentials", () => Async(async () =>
        {
            var draft = new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock_cross_provider_key", ["model"] = "gemini-main" });
            var local = LocalConfiguration("local"); draft.Put(new AdvisorApiConfigurationDraft(local)); draft.SubId = local.Id;
            var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                if (++sends == 1)
                {
                    var reply = InteractionFixture.Reply(new JObject { ["functionCall"] = new JObject { ["name"] = ToolName,
                        ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "get_briefing" }) } } });
                    reply["id"] = "PRIVATE_GEMINI_ID"; return JsonResponse(reply);
                }
                if (sends == 2) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                Require(request.RequestUri!.Host == "localhost" && !request.Headers.Contains("x-goog-api-key") &&
                    !body.ToString().Contains("PRIVATE_GEMINI_ID", StringComparison.Ordinal) && !body.ToString().Contains("mock_cross_provider_key", StringComparison.Ordinal),
                    "Gemini-specific state or credentials crossed to Ollama");
                var input = JObject.Parse((string)body["messages"]![1]!["content"]!);
                Require((int?)input["continuation"]?["query_rounds_used"] == 1 && input["continuation"]!["completed_retrievals"]!.Count() == 1 &&
                    input.ToString().Contains("Public briefing", StringComparison.Ordinal) && body["tools"] == null,
                    "Cross-provider recovery re-fetched data, reset limits or lost evidence");
                return JsonResponse(LocalReply("Recovered locally"));
            });
            var result = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(result.Text == "Recovered locally" && result.ApiRequests == 3 && result.QueryRounds == 1 && result.TotalTokens == null && handler.Disposed,
                "Handoff failed or reported incomplete usage as a known total");
        }));
        check("Ollama-to-Gemini handoff starts a fresh interaction with evidence and no local thinking history", () => Async(async () =>
        {
            var draft = LocalDraft(); var remote = new AdvisorApiConfiguration("remote", "Cloud", "mock_remote_key", "gemini-sub");
            draft.Put(new AdvisorApiConfigurationDraft(remote)); draft.SubId = remote.Id;
            var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                if (++sends == 1) return JsonResponse(LocalReply("", "PRIVATE_LOCAL_THINKING", LocalCall("get_briefing")));
                if (sends == 2) throw new HttpRequestException("Disconnected");
                Require(request.RequestUri!.Host == "generativelanguage.googleapis.com" && request.Headers.GetValues("x-goog-api-key").Single() == "mock_remote_key" &&
                    body["previous_interaction_id"] == null && !body.ToString().Contains("PRIVATE_LOCAL_THINKING", StringComparison.Ordinal),
                    "Cross-provider fallback used another provider's session or authentication");
                var input = JObject.Parse((string)body["input"]![0]!["content"]![0]!["text"]!);
                Require((int?)input["continuation"]?["query_rounds_used"] == 1 && input.ToString().Contains("Public briefing", StringComparison.Ordinal) &&
                    (string?)body["generation_config"]?["tool_choice"] == "none", "Gemini did not inherit evidence and the exhausted limit");
                return JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Recovered remotely" }));
            });
            var result = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(result.Model == "gemini-sub" && result.ApiRequests == 3 && result.QueryRounds == 1 && result.TotalTokens == null,
                "Reverse handoff changed retrieval or usage accounting");
        }));
        check("A failed sub connection never switches back or loops between providers", () => Async(async () =>
        {
            var draft = LocalDraft(); var remote = new AdvisorApiConfiguration("remote", "Cloud", "mock_sub_key", "gemini-sub");
            draft.Put(new AdvisorApiConfigurationDraft(remote)); draft.SubId = remote.Id;
            var sends = 0; var handler = new BackendHttpHandler((_, _) =>
            { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") }); });
            try { await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace))); }
            catch (AdvisorConnectionException e)
            { Require(e.HttpStatus == 503 && e.UsingFallback && sends == 2 && handler.Disposed, "Sub failure looped or leaked resources"); return; }
            throw new InvalidOperationException("Both providers failed but consultation succeeded");
        }));
        check("Provider-specific connection tests send only the test message and do not activate entries or mutate saved settings", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json"); var store = new AdvisorApiSettingsStore(path, () => "Connection {0}");
                var local = new AdvisorApiConfigurationDraft(LocalConfiguration("local")); store.SaveEntry(local, true); var before = File.ReadAllBytes(path);
                foreach (var configuration in new[] { local.Build(), new AdvisorApiConfiguration("remote", "Cloud", "mock_test_key", "gemini-test") })
                {
                    var calls = 0; var handler = new BackendHttpHandler(async (request, token) =>
                    {
                        calls++; var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                        if (configuration.Provider == "ollama")
                        {
                            Require(body["tools"] == null && body["messages"]!.Count() == 1 && (string?)body["messages"]![0]!["content"] == "Only this test",
                                "Local check sent game data or tools"); return JsonResponse(LocalReply("OK"));
                        }
                        Require((bool?)body["store"] == false && body["tools"] == null && body["system_instruction"] == null &&
                            (string?)body["input"]![0]!["content"]![0]!["text"] == "Only this test", "Gemini check sent unrelated data");
                        var response = InteractionFixture.Reply(new JObject { ["text"] = "OK" }); response.Remove("id"); return JsonResponse(response);
                    });
                    var result = await AdvisorServices.TestAsync(directory, configuration, store.TestOptions(), "Only this test", CancellationToken.None,
                        transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                    Require(result.Text == "OK" && result.ApiRequests == 1 && result.QueryRounds == 0 && calls == 1 && handler.Disposed &&
                        File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(Path.Combine(directory, "ai-communication.jsonl")),
                        "Test activated unsaved logging, saved changes or leaked resources");
                }
            });
        }));
        check("Ollama timeout and user cancellation stay distinct and stop without extra sends", () => Async(async () =>
        {
            foreach (var cancelled in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource(); var calls = 0;
                var handler = new BackendHttpHandler((_, token) => { calls++; if (cancelled) cancellation.Cancel(); throw new OperationCanceledException(token); });
                try { await RunPrepared(AdvisorServices.Prepare(LocalDraft(), transportFactory: trace => new AdvisorHttpTransport(handler, trace)), cancellation.Token); }
                catch (AdvisorConnectionException e) when (!cancelled) { Require(e.Code == "request_timeout" && calls == 1 && handler.Disposed, "Timeout was mislabeled or retried"); continue; }
                catch (OperationCanceledException) when (cancelled) { Require(calls == 1 && handler.Disposed, "Cancelled request continued or leaked resources"); continue; }
                throw new InvalidOperationException("Timeout or cancellation was hidden");
            }
        }));
        check("Ollama rejects invalid tool calls, incomplete and truncated responses, and never treats absent usage as zero", () =>
        {
            var reply = LocalReply("OK"); reply.Remove("prompt_eval_count"); Require(OllamaProvider.Read(reply, 1).TotalTokens == null, "Missing usage became zero");
            var truncated = LocalReply("Truncated"); truncated["done_reason"] = "length"; ExpectServiceError("output_limit", () => OllamaProvider.Read(truncated, 1));
            var incomplete = LocalReply("Incomplete"); incomplete["done"] = false; ExpectServiceError("invalid_response", () => OllamaProvider.Read(incomplete, 1));
            var malformed = LocalReply("", null, new JObject { ["function"] = new JObject { ["name"] = ToolName, ["arguments"] = "not an object" } });
            ExpectServiceError("invalid_function_call", () => OllamaProvider.Read(malformed, 1));
            ExpectServiceError("empty_response", () => OllamaProvider.Read(LocalReply("", "Private thought only"), 1));
        });
        check("Cross-provider logs retain unique request numbers, actual JSON, key redaction and the final outcome", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var draft = LocalDraft(); draft.LogAiJson = true;
                var optionsDocument = JObject.Parse(Encoding.UTF8.GetString(draft.Data())); draft = new AdvisorSettingsDraft(optionsDocument);
                var remote = new AdvisorApiConfiguration("remote", "Cloud", "mock_cross_log_secret", "gemini-sub"); draft.Put(new AdvisorApiConfigurationDraft(remote)); draft.SubId = remote.Id;
                var count = 0; var handler = new BackendHttpHandler((_, _) =>
                {
                    if (++count == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("mock_cross_log_secret") });
                    return Task.FromResult(JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Answer" })));
                });
                var setup = AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>()), directory,
                    new AdvisorQuestion("Question"), CancellationToken.None);
                var raw = File.ReadAllText(Path.Combine(directory, "ai-communication.jsonl")); var rows = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();
                var requests = rows.Where(r => (string?)r["event"] == "request").ToArray();
                Require(!raw.Contains("mock_cross_log_secret", StringComparison.Ordinal) && requests.Select(r => (int)r["api_request"]!).SequenceEqual(new[] { 1, 2 }) &&
                    (string?)requests[0]["key_slot"] == "primary" && (string?)requests[1]["key_slot"] == "fallback" &&
                    JObject.Parse((string)requests[0]["body_json"]!)["messages"] != null && JObject.Parse((string)requests[1]["body_json"]!)["input"] != null &&
                    (string?)rows.Last()["outcome"] == "completed", "Mixed provider diagnostics lost wire identity, leaked a secret or omitted completion");
            });
        }));
    }
    private static AdvisorApiConfiguration LocalConfiguration(string id) => new(id, "Local " + id, "ollama", new OllamaService().Defaults());
    private static AdvisorSettingsDraft LocalDraft()
    { var draft = new AdvisorSettingsDraft(new JObject()); var entry = LocalConfiguration("local"); draft.Put(new AdvisorApiConfigurationDraft(entry)); draft.MainId = entry.Id; return draft; }
    private static JObject LocalCall(string query) => new() { ["function"] = new JObject { ["name"] = ToolName,
        ["arguments"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) } } };
    private static JObject LocalReply(string text, string? thinking = null, params JObject[] calls)
    {
        var message = new JObject { ["role"] = "assistant", ["content"] = text };
        if (thinking != null) message["thinking"] = thinking;
        if (calls.Length != 0) message["tool_calls"] = new JArray(calls);
        return new JObject { ["message"] = message, ["done"] = true, ["done_reason"] = "stop", ["prompt_eval_count"] = 100, ["eval_count"] = 10 };
    }
    private static HttpResponseMessage JsonResponse(JObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToString()) };
    private static void ExpectServiceError(string code, Action action)
    {
        try { action(); }
        catch (AdvisorConnectionException e) { Require(e.Code == code, "Expected " + code + ", received " + e.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task InServiceDirectory(Func<string, Task> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-services-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { await run(directory); } finally { Directory.Delete(directory, true); }
    }
}
