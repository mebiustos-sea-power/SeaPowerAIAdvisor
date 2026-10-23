using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SeaPowerAIAdvisor.InformationKind;

internal static partial class ProviderTests
{
    private static void RunSituationDelivery(Action<string, Action> check)
    {
        check("Connection delivery defaults migrate without writing and survive independent saves, duplication and reload", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                var store = new AdvisorApiSettingsStore(path, () => "API");
                var draft = NanoDraft();
                Require(draft.Main.SituationDelivery == AdvisorSituationDelivery.OnDemand, "Existing connection changed delivery");
                byte[] saved = Array.Empty<byte>();
                foreach (var mode in new[] { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
                {
                    draft.Main.SituationDelivery = mode;
                    AdvisorSettings.WriteBytes(path, draft.Data());
                    saved = File.ReadAllBytes(path);
                    var copy = store.Read().Duplicate(draft.MainId!); store.SaveEntry(copy, true);
                    Require(store.Read().Find(copy.Id)!.SituationDelivery == mode && store.Read().Main.SituationDelivery == mode,
                        "Save/duplicate lost the connection policy");
                    var pending = store.Read().Main; pending.SituationDelivery = AdvisorSituationDelivery.OnDemand;
                    Require(store.Read().Main.SituationDelivery == mode, "Unsaved mode leaked");
                    store.SaveLogging(true);
                    Require(store.Read().Main.SituationDelivery == mode, "Another setting erased delivery");
                }
                var legacy = JObject.Parse(System.Text.Encoding.UTF8.GetString(saved));
                ((JObject)legacy["api_configurations"]![0]!).Remove("situation_delivery");
                Require(AdvisorApiLibrary.Read(legacy).Configurations[0].SituationDelivery == AdvisorSituationDelivery.OnDemand,
                    "Absent mode did not retain legacy on-demand behavior");
                foreach (var value in new JToken[] { JValue.CreateNull(), new JValue("typo"), new JValue(true), new JValue(0) })
                {
                    legacy["api_configurations"]![0]!["situation_delivery"] = value;
                    ExpectServiceError("settings_invalid", () => AdvisorApiLibrary.Read(legacy));
                }
                await Task.CompletedTask;
            });
        }));
        check("Upfront battle data includes all pages and ID chunks with shared references once and no query-round consumption", () =>
        {
            var knowledge = DeliveryKnowledge(); var before = knowledge.InitialInput();
            var run = new AdvisorLocalRun();
            var context = AdvisorContext.Create(run.Observe(knowledge), Array.Empty<ChatExchange>(), commonPrompt: "Style", preparedInput: run.CaptureInput);
            var input = context.PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            var rows = (JArray)input.Data["situation_data"]!;
            Require(rows.Where(r => (string?)r["query"] == "get_units").Sum(r => r["data"]!.Count()) == 25 &&
                rows.Single(r => (string?)r["query"] == "list_contacts")["data"]!["items"]!.Count() == 25,
                "Upfront data was truncated at a page or ID boundary");
            Require(rows.OfType<JObject>().SelectMany(r => (r["data"] as JObject)?["weapon_references"] as JArray ?? new JArray()).Count() == 1 &&
                rows.OfType<JObject>().SelectMany(r => (r["data"] as JObject)?["sensor_references"] as JArray ?? new JArray()).Count() == 1,
                "Shared performance references were duplicated");
            Require(knowledge.RoundsUsed == 0 && JToken.DeepEquals(before, knowledge.InitialInput()) && input.Tools.Count == 0 &&
                input.Data["knowledge_catalog"] == null && input.Data["query_limits"] == null &&
                (string?)input.Data["common_prompt"] == "Style" &&
                run.Snapshot()["input"]?["data"]?["situation_data"] != null &&
                run.Snapshot()["input"]?["tools"]?.Count() == 0,
                "Preparing full input changed the snapshot, consumed reads, lost preferences, or recorded the wrong tool");
            var next = context.PrepareStandardInput(AdvisorSituationDelivery.OnDemand);
            Require(next.Tools.Count > 0 && next.Data["situation_data"] == null, "Connection switch reused the other delivery schema");
        });
        check("Upfront battle projections honor status, reference, neutral, destroyed and category exclusions", () =>
        {
            var policy = AdvisorInformation.All.With(BattleFuel, false).With(BattleDamage, false)
                .With(BattleWeapons, false).With(BattleSensors, false).With(BattleSensorPerformance, false)
                .With(BattleThreats, false).With(BattleNeutralContacts, false).With(BattleDestroyedContacts, false)
                .With(BattleMovementPlans, false).With(BattleBriefing, false);
            var knowledge = DeliveryKnowledge(policy: policy);
            var input = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()).PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            var data = (JArray)input.Data["situation_data"]!;
            Require(data.All(r => (string?)r["query"] is not ("get_unit_capabilities" or "get_contact_threats" or "get_movement_plans" or "get_briefing")),
                "An excluded category was included");
            var contacts = (JArray)data.Single(r => (string?)r["query"] == "list_contacts")["data"]!["items"]!;
            Require(contacts.Count == 23 && contacts.All(c => (string?)c["posture"] != "neutral" && (bool?)c["destroyed"] != true),
                "Neutral/destroyed contacts leaked");
            foreach (var section in AdvisorInformation.BattleCapabilitySections)
            {
                var single = AdvisorInformation.All.With(section switch {
                    "fuel" => BattleFuel, "damage" => BattleDamage, "weapons" => BattleWeapons,
                    "sensors" => BattleSensors, _ => BattleSensorPerformance }, false);
                var rows = DeliveryKnowledge(policy: single).ExportSituation().Where(r => (string?)r["query"] == "get_unit_capabilities");
                Require(rows.All(r => r["data"]!["units"]!.All(u => u[section] == null)), "An excluded status leaked: " + section);
            }
        });
        check("An upfront read facade rejects duplicate situation retrieval but retains operation lookups without execution", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(() => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("upfront", AdvisorPrompts.Current)!;
            var knowledge = DeliveryKnowledge(); var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            var input = context.PrepareStandardInput(AdvisorSituationDelivery.Upfront);
            var batch = new JObject { ["requests"] = new JArray(new JObject { ["source"] = "situation", ["query"] = "get_units", ["ids"] = new JArray("own_unit_1") }, Lookup("describe"), Lookup("candidates")) };
            Require(!input.Tools.Any(t => RequestSchemaFixture.Accepts(t["parameters"]!, batch)), "The upfront schema advertises situation reads");
            var response = OperationGatewayTests.Finish(gateway, context.Reads.ExecuteAsync(batch, CancellationToken.None));
            Require((string?)response["results"]?[0]?["error"]?["code"] == "situation_already_supplied" &&
                response["results"]?[0]?["data"] == null && (string?)response["results"]?[1]?["status"] == "ok" &&
                (string?)response["results"]?[2]?["status"] == "ok" && knowledge.RoundsUsed == 0 && operation.Writes == 0 && input.Tools.Any(t => (string?)t["name"] == "propose_operation_plans"),
                "A duplicate read returned data, blocked operation lookup or executed an order");
        });
        check("An oversized upfront snapshot fails before sending and remains available on demand", () => Async(async () =>
        {
            var knowledge = DeliveryKnowledge(referenceText: new string('x', AdvisorQueryLimits.MaxTotalBytes));
            var provider = new FakeProvider("mock", (_, _, _, _) => throw new Exception("An oversized snapshot was sent"));
            try { _ = await new AdvisorEngine(provider, delivery: AdvisorSituationDelivery.Upfront).ConsultAsync(
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None); }
            catch (AdvisorConnectionException error) when (error.Code == "situation_payload_limit")
            {
                Require(provider.Sessions.Count == 0 && knowledge.RoundsUsed == 0 && knowledge.InitialInput()["knowledge_catalog"] != null,
                    "Oversize handling partially sent or mutated the source"); return;
            }
            throw new InvalidOperationException("Full data was silently truncated");
        }));
        check("Fallback changes delivery using the same frozen situation without resetting request or read accounting", () => Async(async () =>
        {
            foreach (var upfrontFirst in new[] { true, false })
            {
                var knowledge = DeliveryKnowledge();
                var mainMode = upfrontFirst ? AdvisorSituationDelivery.Upfront : AdvisorSituationDelivery.OnDemand;
                var subMode = upfrontFirst ? AdvisorSituationDelivery.OnDemand : AdvisorSituationDelivery.Upfront;
                var main = new FakeProvider("main", (s, n, _, _) =>
                {
                    Require((s.Input.Data["situation_data"] != null) == upfrontFirst, "Main ignored its delivery setting");
                    if (n == 1 && !upfrontFirst) return Task.FromResult(Reply(Call("briefing", "get_briefing")));
                    throw new AdvisorConnectionException("rate_limited", 429);
                });
                var sub = new FakeProvider("sub", (s, _, _, _) =>
                {
                    Require((s.Input.Data["situation_data"] != null) == !upfrontFirst &&
                        (string?)s.Input.Data["consultation_id"] == knowledge.Id && s.Continuation != null &&
                        s.Continuation.RoundsUsed == (upfrontFirst ? 0 : 1), "Fallback lost mode, snapshot or budget");
                    return Task.FromResult(new AdvisorReply("Retained situation", Array.Empty<AdvisorToolCall>(), 1));
                });
                var answer = await new AdvisorEngine(main, sub, delivery: mainMode, fallbackDelivery: subMode).ConsultAsync(
                    AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None);
                Require(answer.Completed && answer.ApiRequests == (upfrontFirst ? 2 : 3) && answer.QueryRounds == (upfrontFirst ? 0 : 1) && answer.TotalTokens == null,
                    "Delivery switching reset counters or invented known usage");
            }
        }));
        check("An independent workflow keeps its own input strategy and can hand off to an upfront standard connection", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            var settings = ExtensionDraft(external);
            settings.Main.SituationDelivery = AdvisorSituationDelivery.Upfront;
            ExpectServiceError("situation_delivery_unavailable", () => AdvisorServices.Prepare(settings));
            settings.Main.SituationDelivery = AdvisorSituationDelivery.OnDemand;
            var sub = new AdvisorApiConfigurationDraft(NanoConfiguration()) { SituationDelivery = AdvisorSituationDelivery.Upfront };
            settings.Put(sub); settings.SubId = sub.Id;
            external.Consult = async (configuration, services, context, token) =>
            {
                Require(context.Input.ToolJson != null && JObject.Parse(context.Input.DataJson)["situation_data"] == null,
                    "A standard strategy was forced on an independent workflow");
                _ = context.Knowledge.Execute(PublicQuery("get_briefing"));
                await services.PostJsonAsync(new Uri("https://fixture.invalid/unavailable"), "{}", token);
                throw new InvalidOperationException("Unreachable");
            };
            var sends = 0;
            var handler = new BackendHttpHandler(async (request, token) =>
            {
                if (++sends == 1) return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                    { Content = new System.Net.Http.StringContent("busy") };
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                Require(data["situation_data"] != null && body["tools"] == null &&
                    data["continuation"]?["completed_retrievals"]?.Count() == 1,
                    "The independent-to-standard boundary lost snapshot support or executed evidence");
                return JsonResponse(NanoReply("Full fallback answer"));
            });
            var knowledge = DeliveryKnowledge();
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(settings, transportFactory: trace => new AdvisorHttpTransport(handler, trace)),
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), "unused", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(answer.Completed && answer.ApiRequests == 2 && answer.QueryRounds == 1 && answer.TotalTokens == null,
                "Mixed workflow switching reset counters or asserted known usage");
        }));
        check("Built-in provider wires retain delivery choices, omit automatic data when requested and preserve models and endpoints", () => Async(async () =>
        {
            foreach (var provider in new[] { "nanogpt", "openrouter", "gemini", "ollama" })
            foreach (var mode in new[] { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var service = AdvisorServiceCatalog.BuiltIn.Require(provider);
                var values = service.Defaults(); if (values["api_key"] != null) values["api_key"] = "mock_delivery_key";
                var configuration = new AdvisorApiConfiguration(provider, provider, provider, values, situationDelivery: mode);
                var settings = new AdvisorSettingsDraft(new JObject()); settings.Put(new AdvisorApiConfigurationDraft(configuration)); settings.MainId = configuration.Id;
                var sends = 0;
                var handler = new BackendHttpHandler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends++;
                    Require((body["tools"] == null) == (mode == AdvisorSituationDelivery.Upfront) && (string?)body["model"] == configuration.Model,
                        "An unused tool was sent or delivery changed the configured model: " + provider);
                    if (provider == "gemini")
                    {
                        Require((body["generation_config"]?["tool_choice"] == null) == (mode == AdvisorSituationDelivery.Upfront), "Gemini sent a choice without tools");
                        return JsonResponse(new JObject { ["id"] = "upfront-1", ["status"] = "completed", ["steps"] = new JArray(new JObject { ["type"] = "model_output", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Full answer" }) }) });
                    }
                    if (provider == "ollama") return JsonResponse(new JObject { ["done"] = true, ["message"] = new JObject { ["role"] = "assistant", ["content"] = "Full answer" } });
                    Require((body["tool_choice"] == null) == (mode == AdvisorSituationDelivery.Upfront), "Chat provider sent a choice with no tools");
                    var data = JObject.Parse((string)body["messages"]![1]!["content"]!);
                    Require((data["situation_data"] != null) == (mode == AdvisorSituationDelivery.Upfront) && data["knowledge_catalog"] == null &&
                        (data["situation"] == null) == (mode == AdvisorSituationDelivery.OnRequest), "Saved mode did not reach the actual chat request");
                    return JsonResponse(NanoReply("Full answer"));
                });
                var setup = AdvisorServices.Prepare(settings, transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                var knowledge = DeliveryKnowledge();
                var answer = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()),
                    "unused", new AdvisorQuestion("Question"), CancellationToken.None);
                Require(answer.Completed && answer.Text == "Full answer" && answer.ApiRequests == 1 && knowledge.RoundsUsed == 0 && sends == 1,
                    "Upfront generated extra reads or lost its answer: " + provider);
            }
        }));
    }
    private static BattleQuerySession DeliveryKnowledge(AdvisorInformation? policy = null, string referenceText = "Public performance")
    {
        var snapshot = new JObject
        {
            ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = "Frozen briefing",
            ["friendly_units"] = new JArray(Enumerable.Range(1, 25).Select(i => new JObject { ["id"] = "own_unit_" + i, ["display_name"] = "Own " + i, ["kind"] = "surface" })),
            ["unit_capabilities"] = new JArray(Enumerable.Range(1, 25).Select(i => new JObject {
                ["id"] = "own_unit_" + i, ["display_name"] = "Own " + i, ["fuel"] = new JObject { ["remaining"] = 80 },
                ["damage"] = new JObject { ["damage_fraction"] = 0 }, ["sensors"] = new JArray(new JObject { ["active"] = true }),
                ["sensor_performance"] = new JArray(new JObject { ["reference_id"] = "sensor_1" }),
                ["weapons"] = new JObject { ["ammunition"] = new JArray(new JObject { ["reference_id"] = "weapon_1", ["display_name"] = "Test weapon", ["remaining_count"] = 4 }) } })),
            ["weapon_references"] = new JArray(new JObject { ["id"] = "weapon_1", ["reference_key"] = "test_weapon", ["details"] = referenceText }),
            ["sensor_references"] = new JArray(new JObject { ["id"] = "sensor_1", ["range"] = 100 }),
            ["observed_contacts"] = new JArray(Enumerable.Range(1, 25).Select(i => new JObject { ["id"] = "observed_contact_" + i,
                ["kind"] = "surface", ["posture"] = i == 24 ? "neutral" : "enemy", ["destroyed"] = i == 25 }))
        };
        foreach (var name in new[] { "public_objectives", "friendly_bases", "hosted_air_units", "aviation_hosts", "contact_threat_references", "published_reports" }) snapshot[name] = new JArray();
        return new BattleQuerySession(new BattleSnapshot(snapshot), "ja", "Question", maxRounds: 10, information: policy ?? AdvisorInformation.All);
    }
}
