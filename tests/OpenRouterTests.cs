using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunOpenRouter(Action<string, Action> check)
    {
        check("OpenRouter registrations preserve custom models and reject incomplete settings before saving", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                var store = new AdvisorApiSettingsStore(path, () => "API");
                var configuration = RouterConfiguration("router");
                configuration.Settings["model"] = "vendor/custom.model-v2:free";
                store.SaveEntry(new AdvisorApiConfigurationDraft(configuration), true); store.Assign(configuration.Id, null);
                var original = File.ReadAllBytes(path);
                Require(store.Read().Main.Provider == "openrouter" && store.Read().Main.Model == "vendor/custom.model-v2:free",
                    "OpenRouter required a fixed model or lost the provider during saving");
                foreach (var model in new[] { "", "model-only", "/missing", "vendor/", "vendor/model with space" })
                {
                    var pending = store.Read().Main; pending.Model = model;
                    ExpectServiceError("openrouter_model_invalid", () => store.SaveEntry(pending, false));
                }
                var missing = store.Read().Main; missing.ApiKey = "";
                ExpectServiceError("api_key_missing", () => store.SaveEntry(missing, false));
                Require(File.ReadAllBytes(path).SequenceEqual(original), "Rejected settings changed the saved connection");
                await Task.CompletedTask;
            });
        }));
        check("OpenRouter maps batched tools by call ID, preserves reasoning and obeys disclosure and round limits", () => Async(async () =>
        {
            foreach (var includeBriefing in new[] { true, false })
            {
                var sends = 0; var knowledge = new Knowledge(2, AdvisorInformation.Default.With(InformationKind.BattleBriefing, includeBriefing));
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require(request.RequestUri!.AbsoluteUri == "https://openrouter.ai/api/v1/chat/completions" &&
                        request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "mock_router_secret" &&
                        !request.Headers.Contains("x-goog-api-key") && (bool?)body["stream"] == false &&
                        (bool?)body["provider"]?["require_parameters"] == true && (int?)body["max_tokens"] == 8192 &&
                        (string?)body["tools"]?[0]?["function"]?["name"] == ToolName,
                        "OpenRouter used the wrong endpoint, authentication, capability routing or tool schema");
                    if (++sends == 1)
                    {
                        var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                        Require((string?)data["reply_language"] == "ja" && (string?)data["user_name"] == "Commander" &&
                            (string?)body["tool_choice"] == "auto", "Shared player context did not reach OpenRouter");
                        var response = RouterReply("", RouterCall("c1", "catalog"), RouterCall("c2", "get_briefing"));
                        response["choices"]![0]!["message"]!["reasoning_details"] = new JArray(new JObject
                        { ["type"] = "reasoning.encrypted", ["data"] = "PRIVATE_ROUTER_SIGNATURE", ["format"] = "google-gemini-v1" });
                        return JsonResponse(response);
                    }
                    var messages = (JArray)body["messages"]!;
                    Require((string?)body["tool_choice"] == "auto" && messages.Count == 5 &&
                        messages[2]!["reasoning_details"]!.ToString().Contains("PRIVATE_ROUTER_SIGNATURE", StringComparison.Ordinal) &&
                        (string?)messages[3]!["tool_call_id"] == "c1" && (string?)messages[4]!["tool_call_id"] == "c2",
                        "Continuation lost reasoning/call IDs or disabled tools before the query budget");
                    var catalog = JObject.Parse((string)messages[3]!["content"]!);
                    var denied = JObject.Parse((string)messages[4]!["content"]!);
                    if (includeBriefing)
                        Require((string?)catalog["results"]?[0]?["query"] == "catalog" && (string?)denied["results"]?[0]?["query"] == "get_briefing",
                            "Successful parallel queries returned crossed result contents");
                    else Require((string?)catalog["results"]?[0]?["status"] == "ok" && (string?)denied["results"]?[0]?["error"]?["code"] == "information_disabled" &&
                        !body.ToString().Contains("Public briefing", StringComparison.Ordinal), "OpenRouter received excluded data or discarded an independent successful query");
                    return JsonResponse(RouterReply("Known evidence only"));
                });
                var setup = AdvisorServices.Prepare(RouterDraft(), transportFactory: trace => new AiHttpTransport(handler, trace));
                var answer = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), userName: "Commander"),
                    "unused-router-log", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Text == "Known evidence only" && answer.ApiRequests == 2 && answer.QueryRounds == 1 &&
                    answer.TotalTokens == 220 && !answer.Text.Contains("PRIVATE_ROUTER_SIGNATURE", StringComparison.Ordinal) && handler.Disposed,
                    "OpenRouter completion or usage accounting depended on another provider's format");
            }
        }));
        check("OpenRouter and Gemini fallback preserves executed evidence while excluding provider-private state", () => Async(async () =>
        {
            foreach (var routerFirst in new[] { true, false })
            {
                var draft = RouterDraft(); var gemini = new AdvisorApiConfiguration("gemini", "Gemini", "mock_gemini_secret", "gemini-sub");
                draft.Put(new AdvisorApiConfigurationDraft(gemini));
                draft.MainId = routerFirst ? "router" : gemini.Id; draft.SubId = routerFirst ? gemini.Id : "router";
                var sends = 0;
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends++;
                    if (sends == 1)
                    {
                        if (routerFirst)
                        {
                            var reply = RouterReply("", RouterCall("lookup", "get_briefing"));
                            reply["choices"]![0]!["message"]!["reasoning"] = "PRIVATE_PROVIDER_STATE"; return JsonResponse(reply);
                        }
                        var result = InteractionFixture.Reply(new JObject { ["functionCall"] = new JObject
                        { ["id"] = "first", ["name"] = ToolName, ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "get_briefing" }) } } });
                        result["id"] = "PRIVATE_PROVIDER_STATE"; return JsonResponse(result);
                    }
                    if (sends == 2)
                        return routerFirst ? JsonResponse(new JObject { ["error"] = new JObject { ["code"] = 429, ["message"] = "limited" } }) :
                            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                    var data = routerFirst ? InteractionFixture.Initial(body) : JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require(!body.ToString().Contains("PRIVATE_PROVIDER_STATE", StringComparison.Ordinal) &&
                        (int?)data["continuation"]?["query_rounds_used"] == 1 && data["continuation"]!["completed_retrievals"]!.Count() == 1 &&
                        data.ToString().Contains("Public briefing", StringComparison.Ordinal), "Fallback copied private state, repeated retrieval or lost public evidence");
                    if (routerFirst) Require(request.Headers.GetValues("x-goog-api-key").Single() == "mock_gemini_secret" &&
                        !request.Headers.Contains("Authorization") && body["previous_interaction_id"] == null, "Router authentication/session leaked to Gemini");
                    else Require(request.Headers.Authorization?.Parameter == "mock_router_secret" && !request.Headers.Contains("x-goog-api-key") &&
                        (string?)body["tool_choice"] == "auto", "Gemini state leaked to OpenRouter or the query allowance reset");
                    return routerFirst ? JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Recovered" })) : JsonResponse(RouterReply("Recovered"));
                });
                var answer = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AiHttpTransport(handler, trace)));
                Require(answer.Text == "Recovered" && answer.ApiRequests == 3 && answer.QueryRounds == 1 && answer.TotalTokens == null && handler.Disposed,
                    "Cross-provider fallback reported incomplete usage as known or restarted the consultation");
            }
        }));
        check("The OpenRouter connection probe verifies a tool result in two requests without saving settings and redacts logs", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var draft = RouterDraft(); draft.LogAiJson = true;
                var path = Path.Combine(directory, "settings.json"); AdvisorSettings.WriteBytes(path, draft.Data()); var original = File.ReadAllBytes(path);
                var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends++;
                    var messages = (JArray)body["messages"]!;
                    Require(!body.ToString().Contains("prior_dialogue", StringComparison.Ordinal) && !body.ToString().Contains("victory", StringComparison.Ordinal),
                        "Connection testing included gameplay or history");
                    if (sends == 1)
                    {
                        Require(messages.Count == 2 && (string?)messages[1]!["content"] == "Pending test only" &&
                            (string?)body["tool_choice"]?["function"]?["name"] == "connection_check", "Probe did not force its isolated dummy tool");
                        var reply = RouterReply("", RouterCall("test-id", "", "connection_check", new JObject()));
                        reply["choices"]![0]!["message"]!["reasoning"] = "mock_router_secret"; return JsonResponse(reply);
                    }
                    Require(messages.Count == 4 && (string?)messages.Last!["tool_call_id"] == "test-id" &&
                        (string?)body["tool_choice"] == "none", "The tool result was not correlated or the probe continued requesting tools");
                    return JsonResponse(RouterReply((string)JObject.Parse((string)messages.Last!["content"]!)["confirmation"]!));
                });
                var result = await AdvisorServices.TestAsync(directory, draft.Main.Build(), AdvisorSettings.Edit(path).Options, "Pending test only", CancellationToken.None,
                    transportFactory: trace => new AiHttpTransport(handler, trace));
                var log = File.ReadAllText(Path.Combine(directory, "ai-communication.jsonl"));
                Require(sends == 2 && result.ApiRequests == 2 && result.QueryRounds == 0 && result.TotalTokens == 220 && handler.Disposed &&
                    File.ReadAllBytes(path).SequenceEqual(original) && !log.Contains("mock_router_secret", StringComparison.Ordinal),
                    "Probe saved/selected a connection, miscounted calls, failed disposal or exposed its key");
            });
        }));
        check("The OpenRouter connection probe rejects absent calls, unexpected arguments and incorrect final confirmation", () => Async(async () =>
        {
            foreach (var mode in new[] { "no-call", "wrong-arguments", "wrong-confirmation", "extra-call" })
            {
                var sends = 0; var handler = new BackendHttpHandler((_, _) =>
                {
                    sends++;
                    if (sends == 1) return Task.FromResult(JsonResponse(mode == "no-call" ? RouterReply("OK") :
                        RouterReply("", RouterCall("test", "", "connection_check", mode == "wrong-arguments" ? new JObject { ["unexpected"] = 1 } : new JObject()))));
                    return Task.FromResult(JsonResponse(mode == "extra-call" ? RouterReply("", RouterCall("again", "", "connection_check", new JObject())) : RouterReply("OK")));
                });
                try
                {
                    await AdvisorServices.TestAsync("unused-router-test", RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), "Test", CancellationToken.None,
                        transportFactory: trace => new AiHttpTransport(handler, trace));
                    throw new Exception("A failed tool probe succeeded");
                }
                catch (AiConnectionException error) { Require(error.Code == "tool_call_test_failed" && sends == (mode is "no-call" or "wrong-arguments" ? 1 : 2) && handler.Disposed,
                    "The probe reported partial success, hid a tool failure or made extra requests"); }
            }
        }));
        check("OpenRouter rejects truncated/malformed replies and preserves unknown usage instead of counting zero", () =>
        {
            var noUsage = RouterReply("OK"); noUsage.Remove("usage"); Require(OpenRouterProvider.Read(noUsage).TotalTokens == null, "Missing usage became zero");
            foreach (var pair in new[] { ("length", "output_limit"), ("content_filter", "response_blocked"), ("error", "api_error") })
            { var reply = RouterReply("partial"); reply["choices"]![0]!["finish_reason"] = pair.Item1; ExpectServiceError(pair.Item2, () => OpenRouterProvider.Read(reply)); }
            foreach (var args in new[] { "[]", "{} trailing", "{\"a\":1,\"a\":2}", "null" })
            { var call = RouterCall("bad", "catalog"); call["function"]!["arguments"] = args; ExpectServiceError("invalid_function_call", () => OpenRouterProvider.Read(RouterReply("", call))); }
            ExpectServiceError("invalid_function_call", () => OpenRouterProvider.Read(RouterReply("", RouterCall("same", "catalog"), RouterCall("same", "catalog"))));
            var thought = RouterReply(""); thought["choices"]![0]!["message"]!["reasoning"] = "Private thought";
            ExpectServiceError("empty_response", () => OpenRouterProvider.Read(thought));
            var badType = RouterReply("OK"); badType["choices"]![0]!["finish_reason"] = new JObject(); ExpectServiceError("invalid_response", () => OpenRouterProvider.Read(badType));
        });
        check("OpenRouter surfaces in-body provider errors and credit exhaustion without exposing service text", () => Async(async () =>
        {
            foreach (var item in new[] { (402, "insufficient_credits"), (401, "api_access_denied"), (429, "rate_limited"), (503, "api_error") })
                ExpectServiceError(item.Item2, () => OpenRouterProvider.Read(new JObject { ["error"] = new JObject { ["code"] = item.Item1, ["message"] = "PRIVATE_SERVICE_TEXT" } }));
            var handler = new BackendHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)402) { Content = new StringContent("PRIVATE_SERVICE_TEXT") }));
            try
            {
                await RunPrepared(AdvisorServices.Prepare(RouterDraft(), transportFactory: trace => new AiHttpTransport(handler, trace)));
                throw new Exception("Credit exhaustion became a successful reply");
            }
            catch (AiConnectionException error) { Require(error.Code == "insufficient_credits" && error.HttpStatus == 402 && !error.Message.Contains("PRIVATE_SERVICE_TEXT", StringComparison.Ordinal),
                "HTTP credit failure was misclassified or exposed raw provider text"); }
        }));
        check("OpenRouter cancellation and per-request timeout apply during the second probe request", () => Async(async () =>
        {
            foreach (var cancel in new[] { true, false })
            {
                using var stop = new CancellationTokenSource(); var sends = 0;
                var handler = new BackendHttpHandler(async (_, token) =>
                {
                    if (++sends == 1) return JsonResponse(RouterReply("", RouterCall("test", "", "connection_check", new JObject())));
                    if (cancel) stop.Cancel();
                    await Task.Delay(Timeout.Infinite, token); return JsonResponse(RouterReply("unreachable"));
                });
                var configuration = RouterConfiguration("router", 1);
                try
                {
                    await AdvisorServices.TestAsync("unused-router-timeout", configuration, new AdvisorConnectionOptions(new JObject()), "Test", stop.Token,
                        transportFactory: trace => new AiHttpTransport(handler, trace));
                    throw new Exception("The second request ignored cancellation or its deadline");
                }
                catch (OperationCanceledException) when (cancel) { Require(sends == 2 && handler.Disposed, "Cancellation was retried or leaked the transport"); }
                catch (AiConnectionException error) when (!cancel) { Require(error.Code == "request_timeout" && sends == 2 && handler.Disposed, "Timeout was hidden, retried or mislabeled"); }
            }
        }));
    }
    private static AdvisorApiConfiguration RouterConfiguration(string id, int timeout = 60) => new(id, "Router " + id, "openrouter",
        new JObject { ["api_key"] = "mock_router_secret", ["model"] = OpenRouterService.DefaultModel }, timeout);
    private static AdvisorSettingsDraft RouterDraft()
    { var draft = new AdvisorSettingsDraft(new JObject()); var configuration = RouterConfiguration("router"); draft.Put(new AdvisorApiConfigurationDraft(configuration)); draft.MainId = configuration.Id; return draft; }
    private static JObject RouterCall(string id, string query, string name = ToolName, JObject? arguments = null) => new()
    {
        ["id"] = id, ["type"] = "function", ["function"] = new JObject { ["name"] = name,
            ["arguments"] = (arguments ?? new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) }).ToString(Formatting.None) }
    };
    private static JObject RouterReply(string text, params JObject[] calls) => new()
    {
        ["choices"] = new JArray(new JObject { ["finish_reason"] = calls.Length == 0 ? "stop" : "tool_calls",
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = new JArray(calls) } }),
        ["usage"] = new JObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10, ["total_tokens"] = 110 }
    };
}
