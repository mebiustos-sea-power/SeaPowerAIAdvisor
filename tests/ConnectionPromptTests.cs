using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunConnectionPrompts(Action<string, Action> check)
    {
        check("Connection instructions reach every built-in wire and diagnostic input with delivery overrides and a frozen configuration", () => Async(async () =>
        {
            foreach (var provider in new[] { "gemini", "ollama", "openrouter", "nanogpt" })
            foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            foreach (var prompt in new[] { "", "CONNECTION_ONLY\n日本語 instructions" })
            {
                var service = AdvisorServiceCatalog.BuiltIn.Require(provider); var values = service.Defaults();
                if (values["api_key"] != null) values["api_key"] = "mock_prompt_secret";
                var configuration = new AdvisorApiConfiguration("prompt-test", provider, provider, values, prompt: prompt);
                var draft = new AdvisorSettingsDraft(new JObject()); draft.Put(new AdvisorApiConfigurationDraft(configuration)); draft.MainId = configuration.Id;
                var recording = new AdvisorLocalRun();
                var context = AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>(), commonPrompt: "COMMON_ONLY", preparedInput: recording.CaptureInput);
                var before = context.Input.Data.DeepClone();
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    var data = JObject.Parse((string?)(provider == "gemini" ? body["input"]![0]!["content"]![0]!["text"]! : body["messages"]![1]!["content"]!) ?? throw new InvalidOperationException("Missing user input"));
                    var system = (string?)(provider == "gemini" ? body["system_instruction"]! : body["messages"]![0]!["content"]!) ?? throw new InvalidOperationException("Missing system input");
                    Require((prompt.Length == 0 ? data["connection_prompt"] == null : (string?)data["connection_prompt"] == prompt) &&
                        (string?)data["common_prompt"] == "COMMON_ONLY" && !system.Contains("CONNECTION_ONLY", StringComparison.Ordinal) &&
                        (prompt.Length == 0 ? recording.Snapshot()["input"]?["data"]?["connection_prompt"] == null :
                            (string?)recording.Snapshot()["input"]?["data"]?["connection_prompt"] == prompt),
                        "A provider lost, promoted or mixed instructions, or diagnostics captured the wrong prompt: " + provider);
                    if (provider == "gemini") return JsonResponse(new JObject { ["id"] = "prompt-result", ["status"] = "completed",
                        ["steps"] = new JArray(new JObject { ["type"] = "model_output", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Answer" }) }) });
                    if (provider == "ollama") return JsonResponse(new JObject { ["done"] = true,
                        ["message"] = new JObject { ["role"] = "assistant", ["content"] = "Answer" } });
                    return JsonResponse(NanoReply("Answer"));
                });
                var setup = AdvisorServices.Prepare(draft, situationDelivery: mode, transportFactory: trace => new AiHttpTransport(handler, trace));
                draft.Main.Prompt = "LATER_EDIT";
                var answer = await AdvisorConsultation.RunAsync(setup, context, "unused", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Completed && answer.ApiRequests == 1 && JToken.DeepEquals(before, context.Input.Data),
                    "Connection instructions changed shared input, followed a later edit or added API requests");
            }
        }));
        check("Standard fallback replaces the active connection prompt while retaining retrieved evidence and common preferences", () => Async(async () =>
        {
            foreach (var subPrompt in new[] { "SUB_ONLY", "" })
            {
                var settings = NanoDraft(); settings.Main.Prompt = "MAIN_ONLY";
                var sub = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration("sub-prompt", "Sub", "nanogpt", settings.Main.Settings, prompt: subPrompt));
                settings.Put(sub); settings.SubId = sub.Id;
                var sends = 0; var recording = new AdvisorLocalRun();
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    if (++sends <= 2)
                    {
                        Require((string?)data["connection_prompt"] == "MAIN_ONLY", "Main instructions disappeared during tool exchanges");
                        if (sends == 1) return JsonResponse(NanoReply("", NanoCall("read", "get_briefing")));
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                    }
                    Require((subPrompt.Length == 0 ? data["connection_prompt"] == null : (string?)data["connection_prompt"] == subPrompt) &&
                        !body.ToString().Contains("MAIN_ONLY", StringComparison.Ordinal) && (string?)data["common_prompt"] == "COMMON_ONLY" &&
                        data["continuation"]?["completed_retrievals"]?.Count() == 1 &&
                        (subPrompt.Length == 0 ? recording.Snapshot()["input"]?["data"]?["connection_prompt"] == null :
                            (string?)recording.Snapshot()["input"]?["data"]?["connection_prompt"] == subPrompt),
                        "Fallback retained main instructions, lost common preferences/evidence or reported stale diagnostic input");
                    return JsonResponse(NanoReply("Recovered"));
                });
                var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(settings, transportFactory: trace => new AiHttpTransport(handler, trace)),
                    AdvisorContext.Create(new Knowledge(4), Array.Empty<ChatExchange>(), commonPrompt: "COMMON_ONLY", preparedInput: recording.CaptureInput),
                    "unused", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Completed && answer.ApiRequests == 3 && answer.QueryRounds == 1, "Prompt switching changed continuation accounting");
            }
        }));
        check("Independent external workflows receive active connection instructions on either side of a standard handoff", () => Async(async () =>
        {
            foreach (var independentMain in new[] { true, false })
            {
                var external = IndependentClient(); using var host = new PublicHost(external);
                var settings = ExtensionDraft(external); settings.Main.Prompt = "EXTERNAL_ONLY";
                var standard = new AdvisorApiConfigurationDraft(NanoConfiguration()) { Prompt = "STANDARD_ONLY" };
                settings.Put(standard);
                if (independentMain) settings.SubId = standard.Id;
                else { settings.SubId = settings.MainId; settings.MainId = standard.Id; }
                var recording = new AdvisorLocalRun();
                external.Consult = async (configuration, services, context, token) =>
                {
                    var data = JObject.Parse(context.Input.DataJson);
                    Require((string?)data["connection_prompt"] == "EXTERNAL_ONLY" && (string?)data["common_prompt"] == "COMMON_ONLY" &&
                        !data.ToString().Contains("STANDARD_ONLY", StringComparison.Ordinal), "External workflow received missing or another connection's instructions");
                    if (independentMain)
                    {
                        _ = context.Knowledge.Execute(PublicQuery("get_briefing"));
                        await services.PostJsonAsync(new Uri("https://fixture.invalid/unavailable"), "{}", token);
                        throw new InvalidOperationException("Unreachable");
                    }
                    return new SeaPowerAIAdvisor.Api.AdvisorAnswer("External answer");
                };
                var sends = 0;
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    if (++sends == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require((string?)data["connection_prompt"] == "STANDARD_ONLY" && !body.ToString().Contains("EXTERNAL_ONLY", StringComparison.Ordinal),
                        "Standard handoff inherited external connection instructions");
                    return JsonResponse(NanoReply("Standard answer"));
                });
                var context = AdvisorContext.Create(new Knowledge(4), Array.Empty<ChatExchange>(), commonPrompt: "COMMON_ONLY", preparedInput: recording.CaptureInput);
                var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(settings, transportFactory: trace => new AiHttpTransport(handler, trace)),
                    context, "unused", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Completed && (string?)recording.Snapshot()["input"]?["data"]?["connection_prompt"] ==
                    (independentMain ? "STANDARD_ONLY" : "EXTERNAL_ONLY") && context.Input.Data["connection_prompt"] == null,
                    "Mixed workflows recorded stale instructions or mutated the base consultation input");
            }
        }));
    }
}
