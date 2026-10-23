using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;

internal static partial class ProviderTests
{
    private static void RunOperations(Action<string, Action> check)
    {
        check("Split and legacy AI workflows get the contract with live candidates and submit plans in four requests before approval", () =>
        {
            foreach (var delivery in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            foreach (var legacy in new[] { false, true })
            foreach (var planCount in new[] { 1, 2 })
            {
                var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("ai", AdvisorPrompts.Current)!;
                var knowledge = DeliveryKnowledge(); var progress = new List<AdvisorProgress>();
                AdvisorReply Read(string id, JObject args) => legacy
                    ? Reply(new AdvisorToolCall(id, "query_game", new JObject { ["requests"] = new JArray(new JObject(args) { ["source"] = "operation" }) }))
                    : OperationReply(id, args);
                Task<AdvisorReply> Send(FakeSession session, int send, bool allowed, CancellationToken token)
                {
                    Require(session.Input.Tools.Any(t => (string?)t["name"] == (legacy ? "game_operations" : "propose_operation_plans")) &&
                        !session.Input.Data.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal),
                        "Operation definition was eagerly embedded in initial context");
                    if (send == 1) return Task.FromResult(Read("list", new JObject { ["action"] = "list" }));
                    var result = session.Results.Single().Data;
                    if (result["results"] is JArray reads) result = (JObject)reads[0]!["data"]!;
                    if (send == 2)
                    {
                        var id = (string)result["operations"]![0]!["id"]!;
                        Require(id == BattleSonobuoyOperation.OperationId, "AI could not discover its operation");
                        return Task.FromResult(Read("candidates", new JObject { ["action"] = "candidates", ["operation_id"] = id }));
                    }
                    if (send == 3)
                    {
                        var definition = result["operation"]!; var candidate = result["candidates"]![0]!;
                        Require(definition["parameters"]!["required"]!.Values<string>().SequenceEqual(new[] { "latitude", "longitude", "ammunition" }) &&
                            definition["parameters"]!["properties"]!["depth"] == null &&
                            (bool?)definition["requires_approval"] == true && (int?)definition["plan_policy"]?["target"] == 2 &&
                            (int?)definition["plan_policy"]?["minimum"] == 1 &&
                            (int?)definition["plan_policy"]?["maximum"] == 2 && (string?)definition["plan_policy"]?["selection"] == "single" &&
                            (double?)candidate["position"]?["latitude_deg"] == 26.5 && (string?)candidate["depth"] == "alternating",
                            "One candidate lookup did not supply the argument contract, plan/approval policy or live choices");
                        var order = BuoyRequest(); order["operation_id"] = definition["id"]!.DeepClone();
                        order["target_ref"] = candidate["target_ref"]!.DeepClone();
                        order["arguments"]!["ammunition"] = candidate["ammunition"]![0]!["id"]!.DeepClone();
                        order["arguments"]!["latitude"] = candidate["position"]!["latitude_deg"]!.DeepClone();
                        order["arguments"]!["longitude"] = candidate["position"]!["longitude_deg"]!.DeepClone();
                        var request = PlanRequest(order);
                        if (planCount == 1) ((JArray)request["plans"]!).RemoveAt(1);
                        if (legacy) request["action"] = "submit";
                        return Task.FromResult(Reply(new AdvisorToolCall("plans", legacy ? "game_operations" : "propose_operation_plans", request)));
                    }
                    Require((string?)result["status"] == "proposed" && (bool?)result["registered"] == false &&
                        (bool?)result["requires_approval"] == true && result["plans"]!.Count() == planCount && unit.Calls == 0,
                        "AI submission below target was rejected, padded, bypassed approval or misreported registration");
                    return Task.FromResult(new AdvisorReply("One buoy order proposed.", Array.Empty<AdvisorToolCall>(), 1));
                }
                IAdvisorProvider provider = legacy ? new LegacyProvider("operation-model", Send) : new FakeProvider("operation-model", Send);
                var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations, progress: progress.Add);
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider, delivery: delivery).ConsultAsync(context, CancellationToken.None)));
                Require(unit.Calls == 0, "AI bypassed approval");
                var waiting = progress.Where(p => p.Stage == AdvisorStage.Waiting).ToArray();
                Require(waiting.Length == 4, "Operation progress lost the matching network request");
                foreach (var language in new[] { "en", "ja", "fr" })
                {
                    var text = AdvisorText.Load(language);
                    var displays = waiting.Select(p => AdvisorProgress.Format(p, text)).ToArray();
                    Require(displays[1].Contains(text["progress.topic.operations"], StringComparison.Ordinal) &&
                        displays[2].Contains(text["progress.topic.operation_candidates"], StringComparison.Ordinal) &&
                        displays[3].Contains(text["progress.proposed"], StringComparison.Ordinal) &&
                        !displays[1].Contains(text["progress.proposed"], StringComparison.Ordinal) &&
                        !displays[2].Contains(text["progress.proposed"], StringComparison.Ordinal),
                        "Completed operation lookups and accepted proposals still show the same activity");
                    Require(progress.Where(p => p.Stage == AdvisorStage.Retrieving).All(p =>
                        !AdvisorProgress.Format(p, text).Contains(text["progress.proposed"], StringComparison.Ordinal)),
                        "A submission was shown as received before the gateway accepted it");
                }
                var proposal = registry.FinishProposal("ai", true)!;
                Require(proposal.State == AdvisorProposalState.Pending && proposal.Plans.Count == planCount,
                    "An accepted smaller submission was lost, padded or executed without approval");
                registry.Approve(proposal, new[] { "a" });
                Require(result.Text == "One buoy order proposed." && result.ApiRequests == 4 && unit.Calls == 1 && unit.Depth == "alternating" &&
                    unit.Thread == Environment.CurrentManagedThreadId && knowledge.RoundsUsed == 0 && operations.History.Count == 3 &&
                    operations.History.All(row => (string?)row["request"]?["action"] != "describe"),
                    "Combined lookup needed an extra definition request, bypassed approval or changed native order behavior");
            }
        });
        check("Rejected operation lookups and proposals retain the last real progress without claiming acceptance", () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("progress-errors", AdvisorPrompts.Current)!;
                var progress = new List<AdvisorProgress>();
                AdvisorReply Read(string id, JObject args) => legacy
                    ? Reply(new AdvisorToolCall(id, "query_game", new JObject { ["requests"] = new JArray(new JObject(args) { ["source"] = "operation" }) }))
                    : OperationReply(id, args);
                AdvisorReply Submit(string id, JObject request)
                {
                    if (legacy) request["action"] = "submit";
                    return Reply(new AdvisorToolCall(id, legacy ? "game_operations" : "propose_operation_plans", request));
                }
                Task<AdvisorReply> Send(FakeSession session, int send, bool _, CancellationToken token)
                {
                    if (send is 1 or 3) return Task.FromResult(Read("missing-" + send, new JObject {
                        ["action"] = "candidates", ["operation_id"] = "PRIVATE_MISSING_OPERATION" }));
                    if (send == 2) return Task.FromResult(Read("list", new JObject { ["action"] = "list" }));
                    if (send == 4)
                    {
                        var request = BuoyRequest(); request["request_id"] = "rejected-order";
                        request["arguments"]!["ammunition"] = "PRIVATE_INVALID_AMMUNITION";
                        return Task.FromResult(legacy ? Reply(new AdvisorToolCall("rejected", "game_operations", request)) : OperationReply("rejected", request));
                    }
                    if (send == 5)
                    {
                        Require((string?)session.Results.Single().Data["status"] == (legacy ? "rejected" : "error"), "The failed proposal fixture was not rejected");
                        return Task.FromResult(Submit("valid", PlanRequest(BuoyRequest())));
                    }
                    if (send == 6) return Task.FromResult(Submit("invalid", new JObject { ["request_id"] = "invalid", ["plans"] = new JArray() }));
                    Require((string?)session.Results.Single().Data["status"] == "error", "An invalid later submission was accepted");
                    return Task.FromResult(new AdvisorReply("Ready for review", Array.Empty<AdvisorToolCall>(), 1));
                }
                IAdvisorProvider provider = legacy ? new LegacyProvider("progress", Send) : new FakeProvider("progress", Send);
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(
                    AdvisorContext.Create(new Knowledge(10), Array.Empty<ChatExchange>(), operations: operations, progress: progress.Add), CancellationToken.None)));
                var waiting = progress.Where(p => p.Stage == AdvisorStage.Waiting).ToArray();
                Require(result.Completed && result.ApiRequests == 7 && waiting.Length == 7 && waiting[1].Queries.Count == 0 &&
                    waiting[2].Queries.SequenceEqual(waiting[3].Queries) && waiting[3].Queries.SequenceEqual(waiting[4].Queries) &&
                    waiting[5].Queries.SequenceEqual(waiting[6].Queries) && unit.Calls == 0 && registry.Proposal("progress-errors")!.Plans.Count == 2,
                    "Rejections erased completed work, fabricated progress or changed the accepted proposal");
                foreach (var language in new[] { "ja", "en" })
                {
                    var text = AdvisorText.Load(language);
                    Require(waiting.Take(5).All(p => !AdvisorProgress.Format(p, text).Contains(text["progress.proposed"], StringComparison.Ordinal)) &&
                        waiting.Skip(5).All(p => AdvisorProgress.Format(p, text).Contains(text["progress.proposed"], StringComparison.Ordinal)) &&
                        progress.All(p => !AdvisorProgress.Format(p, text).Contains("PRIVATE_", StringComparison.Ordinal)),
                        "A failed tool call claimed proposal acceptance or exposed model arguments");
                }
            }
        });
        check("One tool call submits two full plans with eleven or forty ordered drops, and only the chosen plan registers", () =>
        {
            foreach (var count in new[] { 11, 40 })
            {
                var unit = new OperationHelicopter { Inventory = count }; var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("batch", AdvisorPrompts.Current)!;
                var request = PlanRequest(BuoyRequest());
                foreach (var plan in ((JArray)request["plans"]!).Cast<JObject>())
                {
                    plan["operations"] = new JArray(Enumerable.Range(0, count).Select(i =>
                    {
                        var order = BuoyRequest(); order.Remove("action"); order.Remove("request_id");
                        order["arguments"]!["latitude"] = 26.5 + i * .001; return order;
                    }));
                }
                var provider = new FakeProvider("batch-model", (session, send, _, _) =>
                {
                    if (send == 1)
                    {
                        Require(RequestSchemaFixture.Accepts(session.Input.Tools.Single(t => (string?)t["name"] == "propose_operation_plans")["parameters"]!, request),
                            "Complete alternative plans did not match the published contract");
                        return Task.FromResult(Reply(new AdvisorToolCall("plans", "propose_operation_plans", request)));
                    }
                    Require(session.Results.Count == 1 && (string?)session.Results[0].Data["status"] == "proposed" && unit.Calls == 0,
                        "A complete submission was partially accepted or ran before approval");
                    return Task.FromResult(new AdvisorReply("Plans ready", Array.Empty<AdvisorToolCall>(), 1));
                });
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(
                    AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
                var proposal = registry.FinishProposal("batch", true)!; registry.Approve(proposal, new[] { "b" }); registry.Approve(proposal, new[] { "a" });
                Require(proposal.Plans.Count == 2 && result.ApiRequests == 2 && operations.History.Count == 1 && unit.Calls == count && unit.Depth == "alternating" &&
                    unit.Latitudes.SequenceEqual(Enumerable.Range(0, count).Select(i => 26.5 + i * .001)), "Selection lost order, duplicated alternatives, or changed player settings");
            }
        });
        check("Forty-one calls are rejected before dispatch, preserving earlier proposals and a specific user notice", () =>
        {
            foreach (var includeRead in new[] { false, true })
            {
                var unit = new OperationHelicopter { Inventory = 50 };
                using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
                using var operations = gateway.CreateSession("overflow", AdvisorPrompts.Current)!;
                var knowledge = new Knowledge(3);
                var provider = new FakeProvider("overflow-model", (_, send, _, _) =>
                {
                    if (send == 1) return Task.FromResult(Reply(BuoyCall(0)));
                    var calls = Enumerable.Range(1, includeRead ? 40 : 41).Select(BuoyCall);
                    if (includeRead) calls = calls.Prepend(Call("read", "catalog"));
                    return Task.FromResult(Reply(calls.ToArray()));
                });
                try
                {
                    _ = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(
                        AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
                }
                catch (AdvisorConnectionException error)
                {
                    Require(error.Code == "tool_call_limit" && unit.Calls == 0 && operations.History.Count == 1 && knowledge.Requests.Count == 0 &&
                        provider.Sessions.Single().Sends == 2 && provider.Sessions.Single().Disposed,
                        "The oversized response partially executed, removed an earlier order, or lost its error identity");
                    foreach (var language in new[] { "ja", "en" })
                    {
                        var notice = AdvisorNotice.Connection(error);
                        var message = notice.Message(AdvisorText.Load(language));
                        Require(notice.TextKey == "connection.tool_call_limit" && message.Contains("40", StringComparison.Ordinal) &&
                            message.Contains(error.Code, StringComparison.Ordinal) && !message.Contains("{0}", StringComparison.Ordinal),
                            "The limit was hidden behind a generic error or could not be displayed");
                    }
                    continue;
                }
                throw new InvalidOperationException("An oversized response was accepted");
            }
        });
        check("The forty-call response limit does not expand the eight-query retrieval batch", () => Async(async () =>
        {
            foreach (var count in new[] { 8, 9 })
            {
                var knowledge = new Knowledge(2);
                var provider = new FakeProvider("read-model", (session, send, _, _) =>
                {
                    if (send == 1) return Task.FromResult(Reply(Enumerable.Range(0, count).Select(i => Call("read-" + i, "catalog")).ToArray()));
                    Require(session.Results.Count == count && session.Results.All(r =>
                        (string?)r.Data["status"] == (count == 8 ? "ok" : "error")), "Retrieval batch validation changed");
                    if (count == 9) Require(session.Results.All(r => (string?)r.Data["error"]?["code"] == "invalid_arguments"),
                        "Oversized information retrieval bypassed the knowledge gateway");
                    return Task.FromResult(new AdvisorReply("Read result handled", Array.Empty<AdvisorToolCall>(), 1));
                });
                var result = await new AdvisorEngine(provider).ConsultAsync(AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None);
                Require(result.ApiRequests == 2 && knowledge.Requests.Count == (count == 8 ? 1 : 0) && knowledge.RoundsUsed == (count == 8 ? 1 : 0),
                    "Retrieval rejection changed round accounting or terminated as a tool-call overflow");
            }
        }));
        check("A provider handoff receives operation receipts and replaying an order cannot register it twice", () =>
        {
            var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("handoff", AdvisorPrompts.Current)!;
            var main = new FakeProvider("first", (_, n, _, _) => n == 1 ? Task.FromResult(OperationReply("first-order", BuoyRequest())) : throw new AdvisorConnectionException("rate_limited", 429));
            var sub = new FakeProvider("second", (s, n, _, _) =>
            {
                Require(s.Continuation != null && (string?)s.Continuation.Operations[0]?["response"]?["status"] == "proposed", "Handoff lost the existing order receipt");
                return Task.FromResult(n == 1 ? OperationReply("replay", BuoyRequest()) : new AdvisorReply("Existing reservation retained.", Array.Empty<AdvisorToolCall>(), 1));
            });
            var context = AdvisorContext.Create(new Knowledge(4), Array.Empty<ChatExchange>(), operations: operations);
            var answer = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(main, sub).ConsultAsync(context, CancellationToken.None)));
            Require(unit.Calls == 0, "A fallback bypassed approval");
            var proposal = registry.FinishProposal("handoff", true)!; registry.Approve(proposal, new[] { "a" });
            Require(answer.Text == "Existing reservation retained." && unit.Calls == 1 && operations.History.Count == 2, "A connection handoff duplicated the order");
        });
        check("All built-in wires preserve the complete split tool list and operation schemas without eager command definitions", () => Async(async () =>
        {
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            using var operations = gateway.CreateSession("wire", AdvisorPrompts.Current)!;
            var input = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations).PrepareStandardInput();
            var settings = AdvisorSettings.Parse(new JObject { ["api_key"] = "mock-key", ["model"] = "gemini-model" }.ToString());
            void CheckWire(JArray wire, bool nested)
            {
                Require(wire.Count == input.Tools.Count && wire.Count == 5, "A tool was omitted or duplicated");
                for (var i = 0; i < wire.Count; i++)
                {
                    var definition = nested ? wire[i]!["function"]! : wire[i]!;
                    Require((string?)definition["name"] == (string?)input.Tools[i]["name"] &&
                        JToken.DeepEquals(definition["parameters"], input.Tools[i]["parameters"]), "Wire schema changed argument constraints");
                }
            }
            CheckWire((JArray)GeminiInteraction.Request(settings, new JArray(), allowTools: true, tools: input.Tools)["tools"]!, false);
            CheckWire((JArray)OllamaProvider.Request(LocalConfiguration("local"), new AdvisorConnectionOptions(new JObject()), new JArray(), input.Tools)["tools"]!, true);
            foreach (var nano in new[] { false, true })
            {
                using var transport = new AdvisorHttpTransport(new BackendHttpHandler((_, _) => throw new InvalidOperationException("Unexpected real send")));
                ChatCompletionProvider provider = nano ? new NanoGptProvider(transport, NanoConfiguration(), new AdvisorConnectionOptions(new JObject()), false)
                    : new OpenRouterProvider(transport, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), false);
                var body = provider.Request(new JArray(), input.Tools, new JValue("auto"));
                CheckWire((JArray)body["tools"]!, true);
                Require(!body.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal), "Commands were eagerly embedded");
            }
            var old = new Api.ConsultationInput("{}", "system", "{}");
            Require(old.OperationToolJson == null && old.ToolsJson.Count == 1 && new Api.RetrievalHistory("[]", 0, 1).OperationsJson == "[]", "Legacy constructors changed");
            await Task.CompletedTask;
        }));
        check("An independent external consultation discovers and proposes through the same host approval boundary", () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("independent", AdvisorPrompts.Current)!;
            external.Consult = async (_, _, context, token) =>
            {
                Require(context.Operations != null && context.Input.OperationToolJson != null, "Independent extension did not receive its optional operation service");
                var list = JObject.Parse(await context.Operations!.ExecuteAsync("{\"action\":\"list\"}", token));
                Require(list["operations"]!.Count() == 1, "Independent catalog lookup failed");
                var result = JObject.Parse(await context.Operations.ExecuteAsync(BuoyRequest().ToString(), token));
                return new Api.AdvisorAnswer((string?)result["status"] == "proposed" ? "Independent registration" : "Failed");
            };
            var setup = AdvisorServices.Prepare(ExtensionDraft(external));
            var context = AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>(), operations: operations);
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => AdvisorConsultation.RunAsync(setup, context,
                "unused-operation-log", new AdvisorQuestion("Schedule one buoy"), CancellationToken.None)));
            Require(unit.Calls == 0, "An independent backend bypassed approval");
            var proposal = registry.FinishProposal("independent", true)!; registry.Approve(proposal);
            Require(result.Text == "Independent registration" && unit.Calls == 1, "A custom workflow was forced through the standard engine or lost its order");
        });
        check("Local consultation diagnostics retain operation requests and receipts without changing them", () =>
        {
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            var run = new AdvisorLocalRun(); var turn = new AdvisorConversation().Begin(new AdvisorQuestion("One buoy")); run.Attach(turn);
            using var operations = run.Observe(gateway.CreateSession(turn.OperationId, AdvisorPrompts.Current)!);
            run.CaptureInput(AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>(), operations: operations).Input);
            var request = BuoyRequest(); _ = OperationGatewayTests.Finish(gateway, operations.ExecuteAsync(request, CancellationToken.None));
            request["request_id"] = "mutated"; turn.Complete("Registered"); run.Finish();
            var snapshot = run.Snapshot();
            Require(snapshot["input"]?["tools"]?.Count() == 2 && (string?)snapshot["operations"]?[0]?["request"]?["request_id"] == "buoy-1" &&
                (string?)snapshot["operations"]?[0]?["response"]?["status"] == "proposed", "Operation evidence was lost or aliased");
        });
    }
    private static AdvisorToolCall BuoyCall(int index)
    {
        var request = BuoyRequest(); request["request_id"] = "buoy-" + index;
        request["arguments"]!["latitude"] = 26.5 + index * 0.001;
        return new AdvisorToolCall("schedule-" + index, "propose_operation_plans", PlanRequest(request));
    }
    private static AdvisorReply OperationReply(string id, JObject args)
    {
        var name = (string?)args["action"] switch {
            "list" => "list_operations", "describe" => "describe_operation", "candidates" => "get_operation_candidates",
            "execute" => "propose_operation_plans", _ => throw new InvalidOperationException("Unknown fixture operation") };
        var arguments = (string?)args["action"] == "execute" ? PlanRequest(args) : (JObject)args.DeepClone(); arguments.Remove("action");
        return Reply(new AdvisorToolCall(id, name, arguments));
    }
    private static JObject PlanRequest(JObject request)
    {
        var order = (JObject)request.DeepClone(); order.Remove("action"); order.Remove("request_id");
        return new JObject { ["request_id"] = request["request_id"]!.DeepClone(), ["plans"] = new JArray(new[] { "a", "b" }.Select(id =>
            new JObject { ["plan_id"] = id, ["title"] = "Plan " + id, ["summary"] = "Comparison " + id, ["operations"] = new JArray(order.DeepClone()) })) };
    }
    private static JObject BuoyRequest() => new()
    {
        ["action"] = "execute", ["operation_id"] = BattleSonobuoyOperation.OperationId, ["request_id"] = "buoy-1", ["target_ref"] = "unit:42",
        ["arguments"] = new JObject { ["latitude"] = 26.5, ["longitude"] = 127.5, ["ammunition"] = "extra_mod_buoy", ["depth"] = "below_layer" }
    };
    private static AdvisorOperations SonobuoyRegistry(OperationHelicopter unit)
    { var registry = new AdvisorOperations(() => true); registry.Register(new BattleSonobuoyOperation(() => new IBattleSonobuoyUnit[] { unit })); return registry; }
    private sealed class OperationHelicopter : IBattleSonobuoyUnit
    {
        public int Id => 42;
        public string Name => "Test helicopter";
        public string? UnavailableReason => null;
        public string Depth => "alternating";
        public JObject Position => new() { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 };
        public BattleReservationRoute? ReservationRoute => new(new AdvisorMapCoordinate(26.5, 127.5), null);
        internal int Calls, Thread, Inventory = 4;
        internal readonly System.Collections.Generic.List<double> Latitudes = new();
        public System.Collections.Generic.IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("extra_mod_buoy", "Extra buoy", Inventory, Inventory - Calls, true) };
        public void Append(string ammunition, double latitude, double longitude) { Latitudes.Add(latitude); Calls++; Thread = Environment.CurrentManagedThreadId; }
    }
}
