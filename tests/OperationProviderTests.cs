using System;
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
        check("The standard AI engine discovers operation metadata and proposes exactly one buoy until host approval", () =>
        {
            foreach (var delivery in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront })
            {
                var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("ai", AdvisorPrompts.Current)!;
                var knowledge = DeliveryKnowledge();
                var provider = new FakeProvider("operation-model", (s, n, allowed, token) =>
                {
                    Require(s.Input.OperationTool != null && !s.Input.Data.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal),
                        "Operation definition was eagerly embedded in initial context");
                    if (n == 1) return Task.FromResult(OperationReply("list", new JObject { ["action"] = "list" }));
                    var result = s.Results.Single().Data;
                    if (result["results"] is JArray reads) result = (JObject)reads[0]!["data"]!;
                    if (n == 2)
                    {
                        Require((string?)result["operations"]?[0]?["id"] == BattleSonobuoyOperation.OperationId, "AI could not discover its operation");
                        return Task.FromResult(OperationReply("detail", new JObject { ["action"] = "describe", ["operation_id"] = BattleSonobuoyOperation.OperationId }));
                    }
                    if (n == 3)
                    {
                        Require(result["operation"]?["parameters"]?["properties"]?["depth"] == null, "AI was offered depth control");
                        return Task.FromResult(OperationReply("candidates", new JObject { ["action"] = "candidates", ["operation_id"] = BattleSonobuoyOperation.OperationId }));
                    }
                    if (n == 4)
                    {
                        Require((double?)result["candidates"]?[0]?["position"]?["latitude_deg"] == 26.5 &&
                            (string?)result["candidates"]?[0]?["depth"] == "alternating", "Candidate position or user setting was unavailable");
                        return Task.FromResult(OperationReply("schedule", BuoyRequest()));
                    }
                    Require((string?)result["status"] == "proposed" && (bool?)result["registered"] == false && unit.Calls == 0,
                        "AI was told an unapproved operation was registered");
                    return Task.FromResult(new AdvisorReply("One buoy order proposed.", Array.Empty<AdvisorToolCall>(), 1));
                });
                var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider, delivery: delivery).ConsultAsync(context, CancellationToken.None)));
                Require(unit.Calls == 0, "AI bypassed approval");
                var proposal = registry.FinishProposal("ai", true)!; registry.Approve(proposal);
                Require(result.Text == "One buoy order proposed." && result.ApiRequests == 5 && unit.Calls == 1 && unit.Depth == "alternating" &&
                    unit.Thread == Environment.CurrentManagedThreadId && knowledge.RoundsUsed == 0 && operations.History.Count == 4,
                    "AI execution bypassed the operation service, duplicated registration, changed depth or used the read-only query gateway");
            }
        });
        check("A single AI response can propose eleven or forty orders and approval registers their sequence", () =>
        {
            foreach (var count in new[] { 11, 40 })
            {
                var unit = new OperationHelicopter { Inventory = count };
                var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("batch", AdvisorPrompts.Current)!;
                var calls = Enumerable.Range(0, count).Select(BuoyCall).ToArray();
                var provider = new FakeProvider("batch-model", (session, send, _, _) =>
                {
                    if (send == 1) return Task.FromResult(Reply(calls));
                    Require(session.Results.Count == count && session.Results.All(r => (string?)r.Data["status"] == "proposed"),
                        "Some valid requests were rejected or lost their results");
                    for (var i = 0; i < count; i++)
                        Require(session.Results[i].Id == calls[i].Id && unit.Calls == 0,
                            "Request order changed or native registration ran before approval");
                    return Task.FromResult(new AdvisorReply("Registered", Array.Empty<AdvisorToolCall>(), 1));
                });
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(
                    AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
                Require(unit.Calls == 0, "A batch bypassed approval");
                var proposal = registry.FinishProposal("batch", true)!; registry.Approve(proposal);
                Require(unit.Latitudes.SequenceEqual(Enumerable.Range(0, count).Select(i => 26.5 + i * 0.001)), "Approval changed native order");
                Require(result.ApiRequests == 2 && unit.Calls == count && operations.History.Count == count && unit.Depth == "alternating",
                    "The response was truncated, registration was duplicated, or the player's depth setting changed");
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
            var proposal = registry.FinishProposal("handoff", true)!; registry.Approve(proposal);
            Require(answer.Text == "Existing reservation retained." && unit.Calls == 1 && operations.History.Count == 2, "A connection handoff duplicated the order");
        });
        check("Gemini, Ollama, OpenRouter and NanoGPT carry separate read and operation tools without eager operation definitions", () => Async(async () =>
        {
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            using var operations = gateway.CreateSession("wire", AdvisorPrompts.Current)!;
            var input = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations).Reads.Input;
            var settings = AdvisorSettings.Parse(new JObject { ["api_key"] = "mock-key", ["model"] = "gemini-model" }.ToString());
            var gemini = GeminiInteraction.Request(settings, new JArray(), tool: input.Tool, allowTools: true, operationTool: input.OperationTool);
            Require(gemini["tools"]!.Count() == 2 && (string?)gemini["tools"]![1]!["name"] == "game_operations", "Gemini lost the second tool");
            Require(JToken.DeepEquals(gemini["tools"]![0]!["parameters"], input.Tool!["parameters"]), "Gemini flattened the read variants");
            var local = OllamaProvider.Request(LocalConfiguration("local"), new AdvisorConnectionOptions(new JObject()), new JArray(), input.Tool, input.OperationTool);
            Require(local["tools"]!.Count() == 2 && (string?)local["tools"]![1]!["function"]?["name"] == "game_operations", "Ollama lost the second tool");
            Require(JToken.DeepEquals(local["tools"]![0]!["function"]!["parameters"], input.Tool!["parameters"]), "Ollama flattened the read variants");
            foreach (var nano in new[] { false, true })
            {
                using var transport = new AdvisorHttpTransport(new BackendHttpHandler((_, _) => throw new InvalidOperationException("Unexpected real send")));
                ChatCompletionProvider provider = nano ? new NanoGptProvider(transport, NanoConfiguration(), new AdvisorConnectionOptions(new JObject()), false)
                    : new OpenRouterProvider(transport, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), false);
                var body = provider.Request(new JArray(), input.Tool, new JValue("auto"), input.OperationTool);
                Require(JToken.DeepEquals(body["tools"]![0]!["function"]!["parameters"], input.Tool!["parameters"]),
                    "Chat completions changed required fields or flattened read variants");
                Require(body["tools"]!.Count() == 2 && (string?)body["tools"]![1]!["function"]?["name"] == "game_operations" &&
                    !body.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal), "Chat completions embedded definitions or lost the operation tool");
            }
            var old = new Api.ConsultationInput("{}", "system", "{}");
            Require(old.OperationToolJson == null && new Api.RetrievalHistory("[]", 0, 1).OperationsJson == "[]", "Legacy public constructors changed behavior");
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
            Require(snapshot["input"]?["operation_tool"] != null && (string?)snapshot["operations"]?[0]?["request"]?["request_id"] == "buoy-1" &&
                (string?)snapshot["operations"]?[0]?["response"]?["status"] == "proposed", "Operation evidence was lost or aliased");
        });
    }
    private static AdvisorToolCall BuoyCall(int index)
    {
        var request = BuoyRequest(); request["request_id"] = "buoy-" + index;
        request["arguments"]!["latitude"] = 26.5 + index * 0.001;
        return new AdvisorToolCall("schedule-" + index, "game_operations", request);
    }
    private static AdvisorReply OperationReply(string id, JObject args)
    {
        if ((string?)args["action"] == "execute") return Reply(new AdvisorToolCall(id, "game_operations", args));
        var lookup = (JObject)args.DeepClone(); lookup["source"] = "operation";
        return Reply(new AdvisorToolCall(id, ToolName, new JObject { ["requests"] = new JArray(lookup) }));
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
        internal int Calls, Thread, Inventory = 4;
        internal readonly System.Collections.Generic.List<double> Latitudes = new();
        public System.Collections.Generic.IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("extra_mod_buoy", "Extra buoy", Inventory, Inventory - Calls, true) };
        public void Append(string ammunition, double latitude, double longitude) { Latitudes.Add(latitude); Calls++; Thread = Environment.CurrentManagedThreadId; }
    }
}
