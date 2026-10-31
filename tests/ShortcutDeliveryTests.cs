using System;
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
    private static void RunShortcutDelivery(Action<string, Action> check)
    {
        check("All built-in shortcuts send situation upfront independent of saved delivery while typed questions inherit without writing settings", () => Async(async () =>
        {
            foreach (var savedMode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.OnRequest, AdvisorSituationDelivery.Upfront })
            foreach (var shortcut in AdvisorShortcut.All.Cast<AdvisorShortcut?>().Append(null))
            {
                var settings = NanoDraft(); settings.Main.SituationDelivery = savedMode; var original = settings.Data();
                var request = shortcut == null ? new AdvisorQuestion("Manual question") : shortcut.Prepare(shortcut.Id);
                var expected = shortcut == null ? savedMode : AdvisorSituationDelivery.Upfront;
                var sends = 0;
                var handler = new BackendHttpHandler(async (http, token) =>
                {
                    var body = JObject.Parse(await http.Content!.ReadAsStringAsync(token)); sends++;
                    var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require((string?)data["interaction_mode"] == expected.Code() &&
                        (data["situation_data"] != null) == (expected == AdvisorSituationDelivery.Upfront) &&
                        (body["tools"] == null) == (expected == AdvisorSituationDelivery.Upfront),
                        "Built-in or typed delivery did not reach the wire");
                    return JsonResponse(NanoReply("Advice"));
                });
                var setup = AdvisorServices.Prepare(settings, allowFallback: false, situationDelivery: request.SituationDelivery,
                    transportFactory: trace => new AiHttpTransport(handler, trace));
                var answer = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>()), "unused", request, CancellationToken.None);
                Require(answer.Completed && sends == 1 && settings.Data().SequenceEqual(original),
                    "A shortcut changed storage or added a read");
            }
        }));
        check("Shortcut delivery reaches both standard connections while inheritance keeps their individual modes and storage unchanged", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var saved = NanoDraft(); saved.Main.SituationDelivery = AdvisorSituationDelivery.OnDemand;
                var sub = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration("sub", "Sub", "nanogpt", saved.Main.Settings,
                    situationDelivery: AdvisorSituationDelivery.OnRequest));
                sub.Settings["api_key"] = "mock_sub_secret"; saved.Put(sub); saved.SubId = sub.Id;
                var path = Path.Combine(directory, "api-settings.json"); AdvisorSettings.WriteBytes(path, saved.Data());
                var before = File.ReadAllBytes(path);
                foreach (var choice in new AdvisorSituationDelivery?[] { null, AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
                {
                    var definition = new AdvisorShortcut("custom", "key_risks", situationDelivery: choice);
                    var request = definition.Prepare("Risks");
                    var sends = 0;
                    var registry = AdvisorServices.CreateRegistry(path, transportFactory: trace => new AiHttpTransport(new BackendHttpHandler(async (http, token) =>
                    {
                        var body = JObject.Parse(await http.Content!.ReadAsStringAsync(token)); var fallback = ++sends > 1;
                        var expected = choice ?? (fallback ? AdvisorSituationDelivery.OnRequest : AdvisorSituationDelivery.OnDemand);
                        var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                        Require((string?)data["interaction_mode"] == expected.Code() &&
                            (data["situation_data"] != null) == (expected == AdvisorSituationDelivery.Upfront), "Main/sub delivery resolution did not reach the wire");
                        Require(http.Headers.GetValues("Authorization").Single() == (fallback ? "Bearer mock_sub_secret" : "Bearer mock_nano_secret"),
                            "Delivery resolution changed connection credentials");
                        return fallback ? JsonResponse(NanoReply("Sub advice")) :
                            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") };
                    }), trace));
                    var prepared = registry.Prepare(situationDelivery: request.SituationDelivery);
                    var result = await AdvisorConsultation.RunAsync(prepared, AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>()),
                        directory, request, CancellationToken.None);
                    Require(result.Completed && result.ApiRequests == 2 && sends == 2 && prepared.SituationDelivery == (choice ?? AdvisorSituationDelivery.OnDemand) &&
                        File.ReadAllBytes(path).SequenceEqual(before), "Delivery override persisted, changed counters or lost fallback");
                }
            });
        }));
        check("An upfront shortcut answers directly while on-request supplies the selected snapshot once after a tool request", () => Async(async () =>
        {
            foreach (var mode in new[] { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var settings = NanoDraft(); var original = settings.Data(); var sends = 0;
                var request = AdvisorShortcut.KeyRisks.Prepare("Risks").WithSituationDelivery(mode);
                var recording = new AdvisorLocalRun(); var knowledge = DeliveryKnowledge(AdvisorInformation.Default.With(InformationKind.BattleWeapons, false));
                var handler = new BackendHttpHandler(async (http, token) =>
                {
                    var body = JObject.Parse(await http.Content!.ReadAsStringAsync(token)); sends++;
                    var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require((string?)data["interaction_mode"] == mode.Code(), "The shortcut used the connection default");
                    Require(!(body["tools"]?.ToString().Contains("list_operations", StringComparison.Ordinal) ?? false), "Advisory shortcut regained operation tools");
                    if (mode == AdvisorSituationDelivery.OnRequest && sends == 1)
                    {
                        Require(data["situation_data"] == null, "On-request sent situation before a request");
                        return JsonResponse(NanoReply("", NanoCall("full", "", "get_situation", new JObject())));
                    }
                    if (mode == AdvisorSituationDelivery.Upfront)
                        Require(data["situation_data"] != null && body["tools"] == null, "Upfront left a redundant situation tool");
                    return JsonResponse(NanoReply("Risk advice"));
                });
                var setup = AdvisorServices.Prepare(settings, allowFallback: false, situationDelivery: request.SituationDelivery,
                    transportFactory: trace => new AiHttpTransport(handler, trace));
                var answer = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(recording.Observe(knowledge), Array.Empty<ChatExchange>()), "unused", request, CancellationToken.None);
                Require(answer.Completed && sends == (mode == AdvisorSituationDelivery.Upfront ? 1 : 2) &&
                    recording.Snapshot()["retrievals"]!.Count() == (mode == AdvisorSituationDelivery.Upfront ? 0 : 1) && settings.Data().SequenceEqual(original),
                    "A shortcut added extra reads, lost full-data recording or rewrote its connection");
            }
        }));
        check("Accepted delivery overrides survive retries and permission changes without leaking into another request", () =>
        {
            var request = AdvisorShortcut.KeyRisks.Prepare("Risks").WithSituationDelivery(AdvisorSituationDelivery.OnRequest).WithOperations(true);
            var chat = new AdvisorConversation(); var turn = chat.Begin(request); turn.Fail(AdvisorNotice.Connection(new AiConnectionException("request_timeout")));
            chat.Retry(turn);
            Require(ReferenceEquals(turn.Request, request) && turn.Request.SituationDelivery == AdvisorSituationDelivery.OnRequest && turn.Request.AllowOperations &&
                AdvisorShortcut.KeyRisks.Prepare("Risks").SituationDelivery == AdvisorSituationDelivery.Upfront &&
                new AdvisorQuestion("Manual").SituationDelivery == null, "Retry or permission changes lost or leaked a delivery choice");
        });
        check("Unsupported independent delivery overrides fail before execution and forbidden fallback does not validate an unused independent sub", () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            var settings = ExtensionDraft(external);
            ExpectServiceError("situation_delivery_unavailable", () => AdvisorServices.Prepare(settings, situationDelivery: AdvisorSituationDelivery.Upfront));
            Require(AdvisorServices.Prepare(settings).SituationDelivery == null, "An inherited independent workflow was forced into a standard mode");
            var standard = NanoDraft(); var sub = settings.Main; standard.Put(sub); standard.SubId = sub.Id;
            ExpectServiceError("situation_delivery_unavailable", () => AdvisorServices.Prepare(standard, situationDelivery: AdvisorSituationDelivery.OnRequest));
            Require(AdvisorServices.Prepare(standard, allowFallback: false, situationDelivery: AdvisorSituationDelivery.Upfront).SituationDelivery == AdvisorSituationDelivery.Upfront,
                "An unused independent sub blocked a no-fallback consultation");
            var registry = new AdvisorBackendRegistry(); var opened = false;
            registry.Register("legacy", () => { opened = true; return Setup(new CustomBackend((_, _) => Task.FromResult(Answer("Unused")))); }); registry.Select("legacy");
            ExpectServiceError("situation_delivery_unavailable", () => registry.Prepare(situationDelivery: AdvisorSituationDelivery.Upfront));
            Require(!opened, "A legacy independent factory ran before rejecting its unsupported input mode");
        });
    }
}
