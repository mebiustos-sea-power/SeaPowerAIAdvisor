using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunLocalConnections(Action<string, Action> check)
    {
        check("Connection lists share name and ID ordering without changing saved order or connection assignments", () =>
        {
            var saved = new AdvisorSettingsDraft(new JObject());
            foreach (var entry in new[] { ("z-main", "Zulu"), ("b-duplicate", "alpha"), ("a-duplicate", "Alpha"), ("m-sub", "Middle"), ("hidden", "Beta") })
                saved.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(entry.Item1, entry.Item2, "mock-key-" + entry.Item1, "gemini-model")));
            saved.MainId = "z-main"; saved.SubId = "m-sub";
            var document = saved.Data(); var registration = saved.Configurations.Select(c => c.Id).ToArray();
            Require(saved.ConfigurationsByName.Select(c => c.Id).SequenceEqual(new[] { "a-duplicate", "b-duplicate", "hidden", "m-sub", "z-main" }),
                "Name ordering depends on registration order or treats equivalent names inconsistently");
            var policy = new AdvisorLocalConnections(new[] { "z-main", "m-sub", "b-duplicate", "a-duplicate" }, "m-sub");
            var listed = policy.Describe(saved);
            Require(((JArray)listed["connections"]!).Select(c => (string)c["id"]!).SequenceEqual(new[] { "a-duplicate", "b-duplicate", "m-sub", "z-main" }) &&
                (string?)listed["default_connection_id"] == "m-sub" && policy.AllowedIds.SequenceEqual(new[] { "z-main", "m-sub", "b-duplicate", "a-duplicate" }),
                "Developer discovery changed order, leaked a disallowed entry or reassigned its default");
            Require(saved.Data().SequenceEqual(document) && saved.Configurations.Select(c => c.Id).SequenceEqual(registration) &&
                saved.MainId == "z-main" && saved.SubId == "m-sub", "Listing reordered stored credentials or changed main/sub assignments");
            var chosen = new AdvisorSettingsDraft(JObject.Parse(System.Text.Encoding.UTF8.GetString(document)));
            policy.Select(chosen, "b-duplicate", true);
            Require(chosen.MainId == "b-duplicate" && chosen.SubId == "m-sub" && chosen.Main.Name == "alpha",
                "Sorting made selection use a position or a duplicate name instead of its stable ID");
            saved.Find("z-main")!.Name = "AAA";
            Require(saved.ConfigurationsByName.First().Id == "z-main" &&
                (string?)policy.Describe(saved)["connections"]![0]!["id"] == "z-main" &&
                saved.MainId == "z-main" && saved.SubId == "m-sub" && policy.DefaultId == "m-sub",
                "A renamed connection retained stale ordering or changed an assigned identity");
        });
        check("A developer-selected connection sends only its key and can fall back only to an allowed saved sub", () => Async(async () =>
        {
            var source = new AdvisorSettingsDraft(new JObject());
            foreach (var id in new[] { "normal", "chosen", "sub" })
                source.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, id, "mock-key-" + id, "gemini-" + id)));
            source.MainId = "normal"; source.SubId = "sub";
            var document = source.Data();
            foreach (var allowSub in new[] { false, true })
            foreach (var allowFallback in new[] { false, true })
            {
                var policy = new AdvisorLocalConnections(allowSub ? new[] { "chosen", "sub" } : new[] { "chosen" }, "chosen", true);
                var draft = new AdvisorSettingsDraft(JObject.Parse(System.Text.Encoding.UTF8.GetString(document)));
                policy.Select(draft, "chosen", allowFallback, "gemini-overridden");
                var keys = new List<string>();
                var setup = AdvisorServices.Prepare(draft, allowFallback: allowFallback, transportFactory: trace => new AiHttpTransport(new BackendHttpHandler(async (request, token) =>
                {
                    var key = request.Headers.GetValues("x-goog-api-key").Single(); keys.Add(key);
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require((string?)body["model"] == (key == "mock-key-chosen" ? "gemini-overridden" : "gemini-sub"),
                        "The selected model was ignored or replaced the saved fallback model");
                    return key == "mock-key-chosen"
                        ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"quota\"}") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InteractionFixture.Reply(new JObject { ["text"] = "allowed sub" }).ToString()) };
                }), trace));
                if (allowSub && allowFallback)
                    Require((await RunPrepared(setup)).Text == "allowed sub" && keys.SequenceEqual(new[] { "mock-key-chosen", "mock-key-sub" }), "An approved sub was not used after the chosen connection failed");
                else
                {
                    try { await RunPrepared(setup); throw new InvalidOperationException("An unapproved sub concealed the failure"); }
                    catch (AiConnectionException error)
                    { Require(error.HttpStatus == 429 && keys.SequenceEqual(new[] { "mock-key-chosen" }), "A normal main or forbidden sub was contacted"); }
                }
                Require(source.MainId == "normal" && source.SubId == "sub" && source.Data().SequenceEqual(document),
                    "Developer selection changed the normal stored main/sub assignments");
            }
        }));
        check("Developer delivery overrides reach the NanoGPT wire while saved credentials, modes and subsequent defaults remain unchanged", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var saved = NanoDraft(); var path = System.IO.Path.Combine(directory, "settings.json");
                AdvisorSettings.WriteBytes(path, saved.Data()); var original = System.IO.File.ReadAllBytes(path);
                var policy = new AdvisorLocalConnections(new[] { "nano" }, "nano");
                foreach (var choice in new AdvisorSituationDelivery?[] { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.OnRequest, null })
                {
                    var draft = AdvisorSettings.Edit(path); policy.Select(draft, "nano", false);
                    var expected = choice ?? AdvisorSituationDelivery.OnDemand;
                    var handler = new BackendHttpHandler(async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                        Require(request.RequestUri!.AbsolutePath == "/api/subscription/v1/chat/completions" &&
                            request.Headers.GetValues("Authorization").Single() == "Bearer mock_nano_secret" &&
                            (string?)body["model"] == AiProviderDefinition.NanoGptDefaultModel, "Delivery changed credentials, endpoint or model");
                        var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                        Require((string?)data["interaction_mode"] == expected.Code() &&
                            (data["situation_data"] != null) == (expected == AdvisorSituationDelivery.Upfront) &&
                            (body["tools"] == null) == (expected == AdvisorSituationDelivery.Upfront),
                            "The consultation used the saved delivery instead of its override");
                        return JsonResponse(NanoReply("Per-request delivery"));
                    });
                    var setup = AdvisorServices.Prepare(draft, allowFallback: false, situationDelivery: choice, transportFactory: trace => new AiHttpTransport(handler, trace));
                    var result = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>()),
                        directory, new AdvisorQuestion("Question"), default);
                    Require(result.Completed && result.ApiRequests == 1 && setup.SituationDelivery == expected &&
                        System.IO.File.ReadAllBytes(path).SequenceEqual(original) && AdvisorSettings.Edit(path).Main.SituationDelivery == AdvisorSituationDelivery.OnDemand,
                        "The override persisted, altered its reported mode or required extra requests");
                }
            });
        }));
        check("Developer prompt overrides reach the wire without affecting saved settings, later consultations or fallback instructions", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var saved = NanoDraft(); saved.Main.Prompt = "SAVED_MAIN";
                var sub = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration("prompt-sub", "Sub", "nanogpt", saved.Main.Settings, prompt: "SAVED_SUB"));
                saved.Put(sub); saved.SubId = sub.Id;
                var path = System.IO.Path.Combine(directory, "settings.json"); AdvisorSettings.WriteBytes(path, saved.Data());
                var original = System.IO.File.ReadAllBytes(path); var policy = new AdvisorLocalConnections(new[] { "nano", sub.Id }, "nano");
                foreach (var prompt in new string?[] { "TEMPORARY\n日本語", "", null })
                foreach (var useSub in new[] { false, true })
                {
                    var draft = AdvisorSettings.Edit(path); policy.Select(draft, "nano", useSub, prompt: prompt);
                    var expected = prompt ?? "SAVED_MAIN"; var sends = 0;
                    var setup = AdvisorServices.Prepare(draft, allowFallback: useSub, transportFactory: trace => new AiHttpTransport(new BackendHttpHandler(async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                        var data = JObject.Parse((string)body["messages"]![1]!["content"]!); var first = ++sends == 1;
                        var activePrompt = first ? expected : "SAVED_SUB";
                        Require(activePrompt.Length == 0 ? data["connection_prompt"] == null : (string?)data["connection_prompt"] == activePrompt,
                            "A developer override was lost, replaced by later edits or leaked into the sub connection");
                        if (first && useSub) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                        return JsonResponse(NanoReply("Prompt override"));
                    }), trace));
                    draft.Main.Prompt = "EDITED_AFTER_PREPARATION";
                    var result = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>()), directory, new AdvisorQuestion("Question"), default);
                    Require(result.Completed && result.ApiRequests == (useSub ? 2 : 1) && System.IO.File.ReadAllBytes(path).SequenceEqual(original) &&
                        AdvisorSettings.Edit(path).Main.Prompt == "SAVED_MAIN", "The temporary prompt was saved or changed consultation accounting");
                }
            });
        }));
        check("Developer model override reuses NanoGPT subscription credentials and records the actual model without saving it", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var saved = NanoDraft(); saved.LogAiJson = true;
                var path = System.IO.Path.Combine(directory, "settings.json");
                AdvisorSettings.WriteBytes(path, saved.Data()); var original = System.IO.File.ReadAllBytes(path);
                var draft = AdvisorSettings.Edit(path);
                new AdvisorLocalConnections(new[] { "nano" }, "nano", true).Select(draft, "nano", false, "vendor/test-model");
                var progress = new List<AdvisorProgress>();
                var context = AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>(), progress: progress.Add);
                var setup = AdvisorServices.Prepare(draft, allowFallback: false, transportFactory: trace =>
                    new AiHttpTransport(new BackendHttpHandler(async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); CheckNanoRequest(request, body);
                        Require((string?)body["model"] == "vendor/test-model", "The wire request used the registered model instead of the override");
                        return JsonResponse(NanoReply("Override answer"));
                    }), trace));
                var result = await AdvisorConsultation.RunAsync(setup, context, directory, new AdvisorQuestion("Question"), default);
                var log = System.IO.File.ReadAllLines(System.IO.Path.Combine(directory, "ai-communication.jsonl")).Select(JObject.Parse).ToArray();
                Require(result.Model == "vendor/test-model" && progress.Count > 0 && progress.All(p => p.Model == "vendor/test-model") &&
                    log.Where(e => (string?)e["event"] is "consultation_started" or "request" or "consultation_finished")
                        .All(e => (string?)e["model"] == "vendor/test-model"), "Progress, answer or trace misidentified the model");
                Require(System.IO.File.ReadAllBytes(path).SequenceEqual(original) && AdvisorSettings.Edit(path).Main.Model == AiProviderDefinition.NanoGptDefaultModel &&
                    !System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "ai-communication.jsonl")).Contains("mock_nano_secret", StringComparison.Ordinal),
                    "The temporary model was saved or the existing credentials leaked into the trace");
            });
        }));
    }
}
