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

internal static partial class ProviderTests
{
    private static void RunNanoGpt(Action<string, Action> check)
    {
        check("NanoGPT registrations preserve custom models and existing connections while rejecting incomplete edits", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                var store = new AdvisorApiSettingsStore(path, () => "API");
                var existing = new AdvisorApiConfiguration("existing", "Existing", "mock_existing_secret", "gemini-existing");
                store.SaveEntry(new AdvisorApiConfigurationDraft(existing), true);
                var configuration = NanoConfiguration(); configuration.Settings["model"] = "vendor/custom-model:variant";
                store.SaveEntry(new AdvisorApiConfigurationDraft(configuration), true); store.Assign(configuration.Id, existing.Id);
                var original = File.ReadAllBytes(path); var saved = store.Read();
                Require(saved.Main.Provider == "nanogpt" && saved.Main.Model == "vendor/custom-model:variant" &&
                    saved.Sub?.ApiKey == "mock_existing_secret" && saved.Sub?.Model == "gemini-existing" &&
                    saved.Main.RequestTimeoutSeconds == 90, "NanoGPT saving changed credentials, model, assignments or timeout");
                foreach (var model in new[] { "", "model with spaces", "model\ncontrol", new string('x', 201) })
                {
                    var pending = store.Read().Main; pending.Model = model;
                    ExpectServiceError("nanogpt_model_invalid", () => store.SaveEntry(pending, false));
                }
                var missing = store.Read().Main; missing.ApiKey = "";
                ExpectServiceError("api_key_missing", () => store.SaveEntry(missing, false));
                var extra = store.Read().Main; extra.Settings["provider"] = "paid-route";
                ExpectServiceError("settings_invalid", () => store.SaveEntry(extra, false));
                Require(File.ReadAllBytes(path).SequenceEqual(original), "An invalid NanoGPT edit changed saved settings");
                await Task.CompletedTask;
            });
        }));
        check("NanoGPT consultations use only the subscription route and correlate tool results with current evidence", () => Async(async () =>
        {
            foreach (var includeBriefing in new[] { true, false })
            {
                var sends = 0; var knowledge = new Knowledge(1, AdvisorInformation.Default.With(InformationKind.BattleBriefing, includeBriefing));
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    CheckNanoRequest(request, body);
                    if (++sends == 1)
                    {
                        var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                        Require((string?)data["reply_language"] == "ja" && (string?)data["user_name"] == "Commander" &&
                            (string?)body["tool_choice"] == "auto", "NanoGPT lost current context or disabled initial tools");
                        var reply = NanoReply("", NanoCall("catalog-call", "catalog"), NanoCall("briefing-call", "get_briefing"));
                        reply["choices"]![0]!["message"]!["reasoning_content"] = "PRIVATE_NANO_CONTEXT";
                        return JsonResponse(reply);
                    }
                    var messages = (JArray)body["messages"]!;
                    Require((string?)body["tool_choice"] == "none" && messages.Count == 5 &&
                        (string?)messages[2]!["reasoning_content"] == "PRIVATE_NANO_CONTEXT" &&
                        (string?)messages[3]!["tool_call_id"] == "catalog-call" && (string?)messages[4]!["tool_call_id"] == "briefing-call",
                        "NanoGPT lost tool correlation/reasoning or exceeded its retrieval limit");
                    var briefing = JObject.Parse((string)messages[4]!["content"]!);
                    Require(includeBriefing ? (string?)briefing["results"]?[0]?["query"] == "get_briefing" :
                        (string?)briefing["error"]?["code"] == "information_disabled" && !body.ToString().Contains("Public briefing", StringComparison.Ordinal),
                        "NanoGPT ignored the player's data exclusions or lost a successful result");
                    return JsonResponse(NanoReply("Known evidence only"));
                });
                var setup = AdvisorServices.Prepare(NanoDraft(), transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                var answer = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), userName: "Commander"),
                    "unused-nano-log", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Text == "Known evidence only" && answer.ApiRequests == 2 && answer.QueryRounds == 1 &&
                    answer.TotalTokens == 220 && handler.Disposed, "NanoGPT lost its answer, usage, limits or transport disposal");
            }
        }));
        check("NanoGPT connection tests verify the subscription tool round trip without saving or exposing secrets", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var draft = NanoDraft(); draft.LogAiJson = true;
                var path = Path.Combine(directory, "settings.json"); AdvisorSettings.WriteBytes(path, draft.Data()); var original = File.ReadAllBytes(path);
                foreach (var compatible in new[] { true, false })
                {
                    var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); CheckNanoRequest(request, body);
                        Require(!body.ToString().Contains("prior_dialogue", StringComparison.Ordinal) && !body.ToString().Contains("victory", StringComparison.Ordinal),
                            "The NanoGPT probe sent game information");
                        var messages = (JArray)body["messages"]!;
                        if (++sends == 1)
                        {
                            Require((string?)body["tool_choice"]?["function"]?["name"] == "connection_check", "The NanoGPT probe did not request its test tool");
                            if (!compatible) return JsonResponse(NanoReply("OK"));
                            var response = NanoReply("", NanoCall("probe", "", "connection_check", new JObject()));
                            response["choices"]![0]!["message"]!["reasoning_content"] = "mock_nano_secret"; return JsonResponse(response);
                        }
                        Require((string?)body["tool_choice"] == "none" && (string?)messages.Last!["tool_call_id"] == "probe",
                            "NanoGPT did not return the matching tool result or kept tools enabled");
                        return JsonResponse(NanoReply((string)JObject.Parse((string)messages.Last!["content"]!)["confirmation"]!));
                    });
                    try
                    {
                        var answer = await AdvisorServices.TestAsync(directory, draft.Main.Build(), AdvisorSettings.Edit(path).Options, "Test", CancellationToken.None,
                            transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                        Require(compatible && sends == 2 && answer.ApiRequests == 2 && answer.TotalTokens == 220,
                            "An incompatible NanoGPT model passed its test");
                    }
                    catch (AdvisorConnectionException error)
                    { Require(!compatible && error.Code == "tool_call_test_failed" && sends == 1, "NanoGPT hid a tool-test failure or sent extra requests"); }
                    Require(handler.Disposed && File.ReadAllBytes(path).SequenceEqual(original), "NanoGPT testing saved settings or leaked its transport");
                }
                Require(!File.ReadAllText(Path.Combine(directory, "ai-communication.jsonl")).Contains("mock_nano_secret", StringComparison.Ordinal),
                    "NanoGPT's API key appeared in its communication log");
            });
        }));
        check("NanoGPT subscription failures never retry through pay-as-you-go or a forbidden sub connection", () => Async(async () =>
        {
            foreach (var failure in new[] { (402, false, "subscription_unavailable"), (402, true, "subscription_unavailable"),
                (429, false, "rate_limited"), (503, false, "api_error") })
            {
                var draft = NanoDraft(); var paid = new AdvisorApiConfiguration("paid", "Paid", "mock_paid_secret", "gemini-paid");
                draft.Put(new AdvisorApiConfigurationDraft(paid)); draft.SubId = paid.Id;
                var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
                {
                    CheckNanoRequest(request, JObject.Parse(await request.Content!.ReadAsStringAsync(token))); sends++;
                    return failure.Item2 ? JsonResponse(new JObject { ["error"] = new JObject { ["code"] = failure.Item1, ["message"] = "PRIVATE_SERVICE_TEXT" } }) :
                        new HttpResponseMessage((HttpStatusCode)failure.Item1) { Content = new StringContent("PRIVATE_SERVICE_TEXT") };
                });
                try
                {
                    await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace), allowFallback: false));
                    throw new Exception("A failed subscription request succeeded");
                }
                catch (AdvisorConnectionException error)
                { Require(error.Code == failure.Item3 && error.HttpStatus == failure.Item1 && sends == 1 && handler.Disposed &&
                    !error.Message.Contains("PRIVATE_SERVICE_TEXT", StringComparison.Ordinal), "A subscription error was hidden, misclassified, retried or exposed raw text"); }
            }
        }));
        check("Fallback to NanoGPT keeps executed evidence without carrying another provider's private history", () => Async(async () =>
        {
            var draft = RouterDraft(); var nano = NanoConfiguration(); draft.Put(new AdvisorApiConfigurationDraft(nano)); draft.SubId = nano.Id;
            var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends++;
                if (sends == 1)
                {
                    var reply = NanoReply("", NanoCall("old-provider-call", "get_briefing"));
                    reply["choices"]![0]!["message"]!["reasoning_content"] = "PRIVATE_OLD_PROVIDER"; return JsonResponse(reply);
                }
                if (sends == 2) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                CheckNanoRequest(request, body);
                var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                Require((int?)data["continuation"]?["query_rounds_used"] == 1 && data["continuation"]!["completed_retrievals"]!.Count() == 1 &&
                    data.ToString().Contains("Public briefing", StringComparison.Ordinal) && (string?)body["tool_choice"] == "none" &&
                    !body.ToString().Contains("PRIVATE_OLD_PROVIDER", StringComparison.Ordinal) && !body.ToString().Contains("old-provider-call", StringComparison.Ordinal),
                    "Fallback lost evidence, reset the query budget or copied private provider history");
                return JsonResponse(NanoReply("Recovered"));
            });
            var answer = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(answer.Text == "Recovered" && answer.ApiRequests == 3 && answer.QueryRounds == 1 && answer.TotalTokens == null && handler.Disposed,
                "NanoGPT fallback repeated queries, lost usage uncertainty or leaked transport resources");
        }));
    }
    private static void CheckNanoRequest(HttpRequestMessage request, JObject body) => Require(
        request.RequestUri!.AbsoluteUri == "https://nano-gpt.com/api/subscription/v1/chat/completions" &&
        request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "mock_nano_secret" &&
        !request.Headers.Contains("x-goog-api-key") && (bool?)body["stream"] == false && (int?)body["max_tokens"] == 4096 &&
        body["provider"] == null && body["billing_mode"] == null && body["billingMode"] == null &&
        body["caching"] == null && (string?)body["tools"]?[0]?["function"]?["name"] is ToolName or "connection_check",
        "NanoGPT used the wrong authentication, endpoint, tools or a paid routing override");
    private static AdvisorApiConfiguration NanoConfiguration() => new("nano", "Nano subscription", "nanogpt",
        new JObject { ["api_key"] = "mock_nano_secret", ["model"] = NanoGptService.DefaultModel }, 90);
    private static AdvisorSettingsDraft NanoDraft()
    { var draft = new AdvisorSettingsDraft(new JObject()); var configuration = NanoConfiguration(); draft.Put(new AdvisorApiConfigurationDraft(configuration)); draft.MainId = configuration.Id; return draft; }
    private static JObject NanoCall(string id, string query, string name = ToolName, JObject? arguments = null) => new()
    {
        ["id"] = id, ["type"] = "function", ["function"] = new JObject { ["name"] = name,
            ["arguments"] = (arguments ?? new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) }).ToString(Formatting.None) }
    };
    private static JObject NanoReply(string text, params JObject[] calls) => new()
    {
        ["choices"] = new JArray(new JObject { ["finish_reason"] = calls.Length == 0 ? "stop" : "tool_calls",
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = new JArray(calls) } }),
        ["usage"] = new JObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 10, ["total_tokens"] = 110 }
    };
}
