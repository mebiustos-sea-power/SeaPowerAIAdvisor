using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;
using Client = SeaPowerAIAdvisor.ExtensionFixture;

internal static partial class ProviderTests
{
    private static JObject LegacyPlans()
    { var request = PlanRequest(BuoyRequest()); request["action"] = "submit"; return request; }
    private static void RunSplitTools(Action<string, Action> check)
    {
        check("Split tool calls batch independent snapshot and operation reads in one round and preserve each call's results", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(() => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("split", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge(); var recording = new AdvisorLocalRun();
            var progress = new System.Collections.Generic.List<AdvisorProgress>();
            var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations, preparedInput: recording.CaptureInput, progress: progress.Add);
            var provider = new FakeProvider("split", (session, send, _, _) =>
            {
                if (send == 1)
                {
                    var calls = new[] {
                        new AdvisorToolCall("definition", "describe_operation", new JObject { ["operation_id"] = operation.Id }),
                        new AdvisorToolCall("routes", "query_situation", ReadArguments(new JObject { ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_1", "own_unit_2") })),
                        new AdvisorToolCall("missing", "query_situation", ReadArguments(new JObject { ["query"] = "get_units", ["ids"] = new JArray("missing") })),
                        new AdvisorToolCall("contacts", "query_situation", ReadArguments(new JObject { ["query"] = "list_contacts", ["kind"] = "surface" })),
                        new AdvisorToolCall("candidates", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id }) };
                    foreach (var call in calls)
                        Require(RequestSchemaFixture.Accepts(session.Input.Tools.Single(t => (string?)t["name"] == call.Name)["parameters"]!, call.Arguments), "A valid split request was not advertised");
                    return Task.FromResult(Reply(calls));
                }
                var results = session.Results;
                Require(results.Select(r => r.Id).SequenceEqual(new[] { "definition", "routes", "missing", "contacts", "candidates" }) &&
                    results.All(r => (int?)r.Data["results"]?[0]?["index"] == 0) &&
                    (string?)results[0].Data["results"]?[0]?["data"]?["operation"]?["id"] == operation.Id &&
                    results[1].Data["results"]?[0]?["data"]?["routes"]?.Count() == 2 &&
                    (string?)results[2].Data["results"]?[0]?["error"]?["code"] == "unknown_id" &&
                    results[3].Data["results"]?[0]?["data"]?["items"]?.Count() == 1 &&
                    results[4].Data["results"]?[0]?["data"]?["candidates"]?.Count() == 2, "Split results lost identity, filtering or a partial error");
                return Task.FromResult(new AdvisorReply("Both routes considered", Array.Empty<AdvisorToolCall>(), 1));
            });
            var answer = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(provider).ConsultAsync(context, CancellationToken.None)));
            Require(answer.Completed && answer.ApiRequests == 2 && knowledge.RoundsUsed == 1 && operations.History.Count == 2 && operation.Writes == 0 &&
                operation.Thread == Environment.CurrentManagedThreadId && recording.Snapshot()["input"]?["tools"]?.Count() == 5,
                "Splitting added a round, changed state, escaped the game thread or lost diagnostic definitions");
            var waiting = progress.Last(p => p.Stage == AdvisorStage.Waiting);
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var display = AdvisorProgress.Format(waiting, text);
                Require(display.Contains(text["progress.topic.operation_definition"], StringComparison.Ordinal) &&
                    display.Contains(text["progress.topic.operation_candidates"], StringComparison.Ordinal) &&
                    display.Contains(text["progress.topic.movement"], StringComparison.Ordinal) &&
                    display.Contains(text["progress.topic.contacts"], StringComparison.Ordinal) &&
                    !display.Contains(text["progress.topic.units"], StringComparison.Ordinal),
                    "A mixed batch hid its independent successful lookups or reported a failed read as completed");
            }
        });
        check("Split schemas and handlers reject cross-purpose fields and never execute or broaden a forged read", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(() => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("forged", AdvisorPrompts.Current)!;
            var context = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations);
            var tools = context.PrepareStandardInput().Tools;
            var invalid = new[] {
                new AdvisorToolCall("mixed-list", "list_operations", new JObject { ["query"] = "catalog" }),
                new AdvisorToolCall("missing-id", "describe_operation", new JObject()),
                new AdvisorToolCall("bad-target", "describe_operation", new JObject { ["operation_id"] = operation.Id, ["target_ref"] = "unit:42" }),
                new AdvisorToolCall("forged-proposal", "query_situation", new JObject { ["requests"] = new JArray(new JObject
                    { ["source"] = "operation", ["action"] = "execute", ["operation_id"] = operation.Id, ["target_ref"] = "unit:42", ["request_id"] = "forged", ["arguments"] = new JObject() }) }),
                new AdvisorToolCall("forged-lookup", "list_operations", new JObject { ["action"] = "candidates", ["operation_id"] = operation.Id }) };
            foreach (var call in invalid)
                Require(!RequestSchemaFixture.Accepts(tools.Single(t => (string?)t["name"] == call.Name)["parameters"]!, call.Arguments), "A mixed-purpose request remained schema-valid");
            foreach (var call in invalid)
            {
                var result = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { call }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
                var response = result.Results.Single().Data;
                Require((string?)response["status"] == "error" || (string?)response["results"]?[0]?["status"] == "error", "An invalid split call was accepted");
            }
            Require(operation.Writes == 0 && registry.Proposal("forged")!.Orders.Count == 0 &&
                operations.History.All(r => (string?)r["response"]?["status"] == "error"), "A forged read proposed or executed an order");
            var malformed = new AdvisorToolCall("bad-source", "query_situation", new JObject { ["requests"] = new JArray(
                new JObject { ["source"] = new JObject(), ["query"] = "catalog" }, new JObject { ["query"] = "get_briefing" }) });
            var partial = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { malformed }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            Require((string?)partial.Results[0].Data["results"]?[0]?["status"] == "error" &&
                (string?)partial.Results[0].Data["results"]?[1]?["data"]?["text"] == "Frozen briefing", "Malformed source hid a valid read or caused an internal cast error");
        });
        check("The eight-read budget is shared by all split tools before any lookup dispatches", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(() => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("split-budget", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge(); var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            var calls = Enumerable.Range(0, 8).Select(i => new AdvisorToolCall("read-" + i, "query_situation", ReadArguments(QueryTests.Q("catalog"))))
                .Append(new AdvisorToolCall("lookup", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id })).ToArray();
            var result = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(calls, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            Require(result.Results.Count == 9 && result.Results.All(r => (string?)r.Data["error"]?["code"] == "invalid_arguments") &&
                knowledge.RoundsUsed == 0 && operations.History.Count == 0 && operation.Writes == 0,
                "Splitting reset the per-response read budget or partially dispatched an oversized batch");
        });
        check("Split dispatch preserves reference definitions after a rejected mixed response and cancellation stops subsequent reads", () =>
        {
            var operation = new LookupOperation { Detail = new string('x', 38_000) };
            var registry = new AdvisorOperations(() => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("split-reference", AdvisorPrompts.Current)!;
            var context = AdvisorContext.Create(ReadKnowledge(referenceText: new string('y', 18_000)), Array.Empty<ChatExchange>(), operations: operations);
            var capability = new AdvisorToolCall("capability", "query_situation", ReadArguments(new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("weapons") }));
            var lookup = new AdvisorToolCall("lookup", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id });
            var rejected = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { capability, lookup }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            var retry = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { capability }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            Require(rejected.Results.All(r => (string?)r.Data["error"]?["code"] == "response_too_large") &&
                retry.Results[0].Data["results"]?[0]?["data"]?["weapon_references"]?.Count() == 1, "A rejected response consumed references before delivery");
            using var cancellation = new CancellationTokenSource(); operation.OnCandidates = cancellation.Cancel;
            var knowledge = ReadKnowledge(); var cancelled = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            try { _ = OperationGatewayTests.Finish(gateway, cancelled.Tools.ExecuteAsync(new[] { lookup, capability }, cancellation.Token, (_, _) => { }, (_, _) => { })); }
            catch (OperationCanceledException) { Require(knowledge.RoundsUsed == 0 && operation.Writes == 0, "Cancellation continued snapshot work"); return; }
            throw new InvalidOperationException("Cancellation was hidden");
        });
        check("Old and split standard providers can hand off both ways with one evidence ledger and one approval receipt", () =>
        {
            foreach (var legacyFirst in new[] { true, false })
            {
                var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
                using var gateway = new AdvisorOperationGateway(registry);
                using var operations = gateway.CreateSession("format-switch", AdvisorPrompts.Current)!;
                var knowledge = ReadKnowledge();
                Task<AdvisorReply> Main(FakeSession session, int send, bool _, CancellationToken token)
                {
                    Require(session.Input.Tools.Count == (legacyFirst ? 2 : 5), "Main received another tool format");
                    if (send > 1) throw new AdvisorConnectionException("rate_limited", 429);
                    var read = legacyFirst ? new AdvisorToolCall("fact", AdvisorLegacyTools.ReadName, LegacyReadArguments(QueryTests.Q("get_briefing"))) : Call("fact", "get_briefing");
                    var order = legacyFirst ? new AdvisorToolCall("order", "game_operations", LegacyPlans()) : ProposalReply("order", BuoyRequest()).Calls.Single();
                    return Task.FromResult(Reply(read, order));
                }
                Task<AdvisorReply> Sub(FakeSession session, int send, bool _, CancellationToken token)
                {
                    Require(session.Input.Tools.Count == (legacyFirst ? 5 : 2) && session.Continuation?.Retrievals.Count == 1 &&
                        session.Continuation.Operations.Count == 1 && session.Continuation.RoundsUsed == 1, "Fallback changed tools or duplicated evidence");
                    return Task.FromResult(send == 1 ? (legacyFirst ? ProposalReply("replay", BuoyRequest()) : Reply(new AdvisorToolCall("replay", "game_operations", LegacyPlans())))
                        : new AdvisorReply("Retained proposal", Array.Empty<AdvisorToolCall>(), 1));
                }
                IAdvisorProvider main = legacyFirst ? new LegacyProvider("old", Main) : new FakeProvider("new", Main);
                IAdvisorProvider sub = legacyFirst ? new FakeProvider("new", Sub) : new LegacyProvider("old", Sub);
                var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(main, sub).ConsultAsync(
                    AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
                Require(result.Completed && result.ApiRequests == 4 && result.QueryRounds == 1 && result.TotalTokens == null && unit.Calls == 0,
                    "Format migration changed request accounting or bypassed approval");
                var proposal = registry.FinishProposal("format-switch", true)!;
                Require(proposal.Plans.Count == 2 && proposal.Orders.Count == 2, "Replay duplicated a plan set"); registry.Approve(proposal, new[] { "a" });
                Require(unit.Calls == 1, "The migrated proposal did not execute exactly once");
            }
        });
        check("A separately compiled tool-list extension receives split definitions and uses the same host approval service", () =>
        {
            var extension = new Client.ToolListExtension(); using var host = new PublicHost(extension);
            var unit = new OperationHelicopter(); var registry = SonobuoyRegistry(unit);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("public-split", AdvisorPrompts.Current)!;
            var sends = 0; System.Collections.Generic.IReadOnlyList<Api.ToolResult>? results = null;
            extension.Open = (_, _, input, _) =>
            {
                Require(input.ToolsJson.Count == 5 && input.ToolJson == null && input.OperationToolJson == null, "The opt-in public adapter received the old contract");
                return new Client.Session(_ => new Api.ProviderRequest(100, token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (++sends == 1) return Task.FromResult(new Api.ProviderReply("", new[] {
                        new Api.ToolCall("fact", "query_situation", ReadArguments(QueryTests.Q("get_briefing")).ToString()),
                        new Api.ToolCall("list", "list_operations", "{}") }));
                    if (sends == 2)
                    {
                        Require(results?.Count == 2 && JObject.Parse(results[0].DataJson)["results"]?[0]?["data"]?["text"] != null, "Public results were lost");
                        var order = ProposalReply("proposal", BuoyRequest()).Calls.Single();
                        return Task.FromResult(new Api.ProviderReply("", new[] { new Api.ToolCall(order.Id, order.Name, order.Arguments.ToString()) }));
                    }
                    Require((string?)JObject.Parse(results!.Single().DataJson)["status"] == "proposed" && unit.Calls == 0, "Public adapter bypassed approval");
                    return Task.FromResult(new Api.ProviderReply("Approval required"));
                }), value => results = value, () => { });
            };
            var context = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations);
            var answer = OperationGatewayTests.Finish(gateway, Task.Run(() => AdvisorConsultation.RunAsync(AdvisorServices.Prepare(ExtensionDraft(extension)),
                context, "unused-split-log", new AdvisorQuestion("Propose a buoy"), CancellationToken.None)));
            Require(answer.Completed && answer.ApiRequests == 3 && answer.QueryRounds == 1 && unit.Calls == 0, "Public tool-list execution failed");
            registry.Approve(registry.FinishProposal("public-split", true)!, new[] { "a" });
            Require(unit.Calls == 1, "The public proposal was not executable");
        });
    }
}
