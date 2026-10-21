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
        check("The standard AI engine discovers operation metadata and schedules exactly one buoy through the game-thread service", () =>
        {
            var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("ai", AdvisorPrompts.Current)!;
            var knowledge = new Knowledge(5);
            var provider = new FakeProvider("operation-model", (s, n, allowed, token) =>
            {
                Require(s.Input.OperationTool != null && !s.Input.Data.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal),
                    "Operation definition was eagerly embedded in initial context");
                if (n == 1) return Task.FromResult(OperationReply("list", new JObject { ["action"] = "list" }));
                var result = s.Results.Single().Data;
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
                Require((string?)result["status"] == "registered" && (string?)result["data"]?["depth"] == "alternating", "AI was not given the registration result");
                return Task.FromResult(new AdvisorReply("One buoy order registered.", Array.Empty<AdvisorToolCall>(), 1));
            });
            var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(context, CancellationToken.None)));
            Require(result.Text == "One buoy order registered." && result.ApiRequests == 5 && unit.Calls == 1 && unit.Depth == "alternating" &&
                unit.Thread == Environment.CurrentManagedThreadId && knowledge.Requests.Count == 0 && operations.History.Count == 4,
                "AI execution bypassed the operation service, duplicated registration, changed depth or used the read-only query gateway");
        });
        check("A provider handoff receives operation receipts and replaying an order cannot register it twice", () =>
        {
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            using var operations = gateway.CreateSession("handoff", AdvisorPrompts.Current)!;
            var main = new FakeProvider("first", (_, n, _, _) => n == 1 ? Task.FromResult(OperationReply("first-order", BuoyRequest())) : throw new AdvisorConnectionException("rate_limited", 429));
            var sub = new FakeProvider("second", (s, n, _, _) =>
            {
                Require(s.Continuation != null && (string?)s.Continuation.Operations[0]?["response"]?["status"] == "registered", "Handoff lost the existing order receipt");
                return Task.FromResult(n == 1 ? OperationReply("replay", BuoyRequest()) : new AdvisorReply("Existing reservation retained.", Array.Empty<AdvisorToolCall>(), 1));
            });
            var context = AdvisorContext.Create(new Knowledge(4), Array.Empty<ChatExchange>(), operations: operations);
            var answer = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(main, sub).ConsultAsync(context, CancellationToken.None)));
            Require(answer.Text == "Existing reservation retained." && unit.Calls == 1 && operations.History.Count == 2, "A connection handoff duplicated the order");
        });
        check("Gemini, Ollama, OpenRouter and NanoGPT carry separate read and operation tools without eager operation definitions", () => Async(async () =>
        {
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            using var operations = gateway.CreateSession("wire", AdvisorPrompts.Current)!;
            var input = AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>(), operations: operations).Input;
            var settings = AdvisorSettings.Parse(new JObject { ["api_key"] = "mock-key", ["model"] = "gemini-model" }.ToString());
            var gemini = GeminiInteraction.Request(settings, new JArray(), tool: input.Tool, allowTools: true, operationTool: input.OperationTool);
            Require(gemini["tools"]!.Count() == 2 && (string?)gemini["tools"]![1]!["name"] == "game_operations", "Gemini lost the second tool");
            var local = OllamaProvider.Request(LocalConfiguration("local"), new AdvisorConnectionOptions(new JObject()), new JArray(), input.Tool, input.OperationTool);
            Require(local["tools"]!.Count() == 2 && (string?)local["tools"]![1]!["function"]?["name"] == "game_operations", "Ollama lost the second tool");
            foreach (var nano in new[] { false, true })
            {
                using var transport = new AdvisorHttpTransport(new BackendHttpHandler((_, _) => throw new InvalidOperationException("Unexpected real send")));
                ChatCompletionProvider provider = nano ? new NanoGptProvider(transport, NanoConfiguration(), new AdvisorConnectionOptions(new JObject()), false)
                    : new OpenRouterProvider(transport, RouterConfiguration("router"), new AdvisorConnectionOptions(new JObject()), false);
                var body = provider.Request(new JArray(), input.Tool, new JValue("auto"), input.OperationTool);
                Require(body["tools"]!.Count() == 2 && (string?)body["tools"]![1]!["function"]?["name"] == "game_operations" &&
                    !body.ToString().Contains(BattleSonobuoyOperation.OperationId, StringComparison.Ordinal), "Chat completions embedded definitions or lost the operation tool");
            }
            var old = new Api.ConsultationInput("{}", "system", "{}");
            Require(old.OperationToolJson == null && new Api.RetrievalHistory("[]", 0, 1).OperationsJson == "[]", "Legacy public constructors changed behavior");
            await Task.CompletedTask;
        }));
        check("An independent external consultation can discover and execute through the optional host operation service", () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            var unit = new OperationHelicopter(); using var gateway = new AdvisorOperationGateway(SonobuoyRegistry(unit));
            using var operations = gateway.CreateSession("independent", AdvisorPrompts.Current)!;
            external.Consult = async (_, _, context, token) =>
            {
                Require(context.Operations != null && context.Input.OperationToolJson != null, "Independent extension did not receive its optional operation service");
                var list = JObject.Parse(await context.Operations!.ExecuteAsync("{\"action\":\"list\"}", token));
                Require(list["operations"]!.Count() == 1, "Independent catalog lookup failed");
                var result = JObject.Parse(await context.Operations.ExecuteAsync(BuoyRequest().ToString(), token));
                return new Api.AdvisorAnswer((string?)result["status"] == "registered" ? "Independent registration" : "Failed");
            };
            var setup = AdvisorServices.Prepare(ExtensionDraft(external));
            var context = AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>(), operations: operations);
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => AdvisorConsultation.RunAsync(setup, context,
                "unused-operation-log", new AdvisorQuestion("Schedule one buoy"), CancellationToken.None)));
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
                (string?)snapshot["operations"]?[0]?["response"]?["status"] == "registered", "Operation evidence was lost or aliased");
        });
    }
    private static AdvisorReply OperationReply(string id, JObject args) => Reply(new AdvisorToolCall(id, "game_operations", args));
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
        internal int Calls, Thread;
        public System.Collections.Generic.IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("extra_mod_buoy", "Extra buoy", 4, 4 - Calls, true) };
        public void Append(string ammunition, double latitude, double longitude) { Calls++; Thread = Environment.CurrentManagedThreadId; }
    }
}
