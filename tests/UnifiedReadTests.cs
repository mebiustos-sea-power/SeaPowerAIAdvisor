using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static JObject ReadArguments(params JObject[] queries) => new()
    { ["requests"] = new JArray(queries.Select(q => q.DeepClone())) };
    private static JObject SharedReadArguments(params JObject[] queries) => new()
    { ["requests"] = new JArray(queries.Select(q => new JObject(q) { ["source"] = "situation" })) };
    private static JObject Lookup(string action) => new()
    { ["source"] = "operation", ["action"] = action, ["operation_id"] = "example.inspect" };

    private static void RunUnifiedReads(Action<string, Action> check)
    {
        check("One shared read batch combines two routes, operation metadata and two candidates while preserving item errors and contact kind", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("mixed", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge();
            var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            var requests = new JArray(Lookup("describe"),
                new JObject { ["source"] = "situation", ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_1", "own_unit_2") },
                new JObject { ["source"] = "situation", ["query"] = "get_units", ["ids"] = new JArray("missing") },
                new JObject { ["source"] = "situation", ["query"] = "list_contacts", ["kind"] = "surface" }, Lookup("candidates"));
            var response = OperationGatewayTests.Finish(gateway, context.Reads.ExecuteAsync(new JObject { ["requests"] = requests }, CancellationToken.None));
            var rows = (JArray)response["results"]!;
            Require(rows.Count == 5 && rows.Select((row, i) => (int?)row["index"] == i).All(v => v) &&
                (string?)rows[0]?["data"]?["operation"]?["id"] == operation.Id &&
                rows[1]?["data"]?["units"]?.Count() == 2 && rows[1]?["data"]?["routes"]?.Count() == 2 &&
                (string?)rows[2]?["error"]?["code"] == "unknown_id" && rows[2]?["data"] == null &&
                rows[3]?["data"]?["items"]?.Count() == 1 && rows[4]?["data"]?["candidates"]?.Count() == 2,
                "A result was lost, mixed, or a single invalid item rejected the whole batch");
            Require(knowledge.RoundsUsed == 1 && operations.History.Count == 2 && operation.Writes == 0 &&
                operation.Thread == Environment.CurrentManagedThreadId, "The shared read changed the game or ran a live lookup on a worker");
        });
        check("Shared reads reject mixed fields and missing identifiers while valid mixed reads succeed", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("schema-regression", AdvisorPrompts.Current)!;
            var requests = new[] {
                (new JObject { ["source"] = "operation", ["action"] = "list" }, true),
                (Lookup("describe"), true), (Lookup("candidates"), true),
                (new JObject { ["source"] = "situation", ["query"] = "get_units", ["ids"] = new JArray("own_unit_1") }, true),
                (new JObject { ["source"] = "situation", ["query"] = "get_movement_plans", ["ids"] = new JArray("own_unit_1") }, true),
                (new JObject { ["source"] = "operation", ["action"] = "list", ["query"] = "catalog" }, false),
                (new JObject { ["source"] = "operation", ["query"] = "catalog" }, false),
                (new JObject { ["source"] = "operation", ["action"] = "describe" }, false),
                (new JObject { ["source"] = "operation", ["action"] = "candidates" }, false),
                (new JObject(Lookup("describe")) { ["target_ref"] = "unit:42" }, false),
                (new JObject { ["source"] = "situation", ["query"] = "catalog", ["action"] = "list" }, false),
                (new JObject { ["source"] = "situation", ["query"] = "get_units" }, false),
                (new JObject { ["source"] = "situation", ["query"] = "get_movement_plans" }, false),
                (new JObject { ["source"] = "situation", ["query"] = "get_unit_capabilities", ["target_ref"] = "unit:42" }, false),
                (new JObject { ["source"] = "situation", ["query"] = "list_units", ["sections"] = new JArray("fuel") }, false)
            };
            foreach (var (request, valid) in requests)
            {
                var reads = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations).Reads;
                var args = new JObject { ["requests"] = new JArray(request) };
                var result = OperationGatewayTests.Finish(gateway, reads.ExecuteAsync(args, CancellationToken.None));
                Require(((string?)result["results"]?[0]?["status"] == "ok") == valid,
                    "Dispatch accepted invalid fields or rejected a valid request: " + request);
            }
            var context = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations);
            var mixed = new JObject { ["requests"] = new JArray(requests.Take(5).Select(r => r.Item1)) };
            var combined = OperationGatewayTests.Finish(gateway, context.Reads.ExecuteAsync(mixed, CancellationToken.None));
            Require(combined["results"]!.Count() == 5 && combined["results"]!.All(r => (string?)r["status"] == "ok") && operation.Writes == 0,
                "Valid mixed reads lost results or performed an operation");
        });
        check("Read-only dispatch rejects execute, malformed sources and disabled operations without touching native state", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("read-only", AdvisorPrompts.Current)!;
            var context = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>(), operations: operations);
            var execute = Lookup("execute"); execute["target_ref"] = "unit:42"; execute["arguments"] = new JObject(); execute["request_id"] = "blocked";
            var batch = new JObject { ["requests"] = new JArray(execute, new JObject { ["source"] = new JObject() }, 123,
                new JObject { ["source"] = "situation", ["query"] = "get_briefing" }) };
            var result = OperationGatewayTests.Finish(gateway, context.Reads.ExecuteAsync(batch, CancellationToken.None));
            var denied = AdvisorContext.Create(ReadKnowledge(), Array.Empty<ChatExchange>()).Reads.ExecuteAsync(
                new JObject { ["requests"] = new JArray(Lookup("list")) }, CancellationToken.None).GetAwaiter().GetResult();
            Require((string?)result["results"]?[0]?["status"] == "error" && (string?)result["results"]?[1]?["status"] == "error" &&
                (string?)result["results"]?[3]?["data"]?["text"] == "Frozen briefing" &&
                (string?)denied["results"]?[0]?["error"]?["code"] == "unit_operations_disabled" &&
                operation.Writes == 0 && operations.History.Count == 0 && registry.Proposal("read-only")!.Orders.Count == 0,
                "A read executed or proposed an order, hid a malformed source, or bypassed WIP");
        });
        check("A mixed response exceeding its budget leaves first-delivery references available for a smaller retry", () =>
        {
            var operation = new LookupOperation { Detail = new string('x', 38_000) };
            var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("budget", AdvisorPrompts.Current)!;
            var reads = AdvisorContext.Create(ReadKnowledge(referenceText: new string('y', 18_000)), Array.Empty<ChatExchange>(), operations: operations).Reads;
            var capability = new JObject { ["source"] = "situation", ["query"] = "get_unit_capabilities",
                ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("weapons") };
            var rejected = OperationGatewayTests.Finish(gateway, reads.ExecuteAsync(new JObject { ["requests"] = new JArray(Lookup("candidates"), capability) }, CancellationToken.None));
            var first = reads.ExecuteAsync(new JObject { ["requests"] = new JArray(capability.DeepClone()) }, CancellationToken.None).GetAwaiter().GetResult();
            var repeated = reads.ExecuteAsync(new JObject { ["requests"] = new JArray(capability.DeepClone()) }, CancellationToken.None).GetAwaiter().GetResult();
            Require((string?)rejected["error"]?["code"] == "response_too_large" &&
                first["results"]?[0]?["data"]?["weapon_references"]?.Count() == 1 &&
                repeated["results"]?[0]?["data"]?["weapon_references"]?.Count() == 0 && operation.Writes == 0,
                "A rejected mixed response consumed a reference definition or retried native operations");
        });
        check("Metadata-only reads share the total response budget and oversized batches dispatch no items", () =>
        {
            var operation = new LookupOperation { Detail = new string('x', 30_000) };
            var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("metadata-budget", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge(); var reads = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations).Reads;
            var over = OperationGatewayTests.Finish(gateway, reads.ExecuteAsync(new JObject { ["requests"] = new JArray(
                Enumerable.Range(0, 9).Select(_ => Lookup("candidates"))) }, CancellationToken.None));
            Require((string?)over["error"]?["code"] == "invalid_arguments" && operations.History.Count == 0 && knowledge.RoundsUsed == 0,
                "A rejected read batch partially dispatched");
            JObject result = new(); var successful = 0;
            for (var i = 0; i < 10; i++)
            {
                result = OperationGatewayTests.Finish(gateway, reads.ExecuteAsync(new JObject { ["requests"] = new JArray(Lookup("candidates")) }, CancellationToken.None));
                if ((string?)result["status"] == "error") break;
                successful++;
            }
            Require(successful > 0 && successful < 10 && (string?)result["error"]?["code"] == "payload_limit" &&
                knowledge.RoundsUsed == 0 && operation.Writes == 0, "Metadata-only reads bypassed the common budget or consumed snapshot rounds");
        });
        check("Cancellation during a live lookup stops the mixed read before subsequent snapshot work", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var operation = new LookupOperation { OnCandidates = cancellation.Cancel };
            var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("cancel", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge(); var reads = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations).Reads;
            try
            {
                _ = OperationGatewayTests.Finish(gateway, reads.ExecuteAsync(new JObject { ["requests"] = new JArray(
                    Lookup("candidates"), new JObject { ["source"] = "situation", ["query"] = "get_briefing" }) }, cancellation.Token));
            }
            catch (OperationCanceledException)
            { Require(knowledge.RoundsUsed == 0 && operation.Writes == 0, "Cancelled work continued"); return; }
            throw new InvalidOperationException("Cancellation was hidden as a successful item");
        });
        check("A fallback receives each mixed-read fact once through the existing knowledge and operation ledgers", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("read-handoff", AdvisorPrompts.Current)!;
            var knowledge = ReadKnowledge();
            var main = new FakeProvider("main", (_, send, _, _) => send == 1 ? Task.FromResult(Reply(
                Call("briefing", "get_briefing"),
                new AdvisorToolCall("definition", "describe_operation", new JObject { ["operation_id"] = operation.Id }),
                new AdvisorToolCall("candidates", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id })))
                : throw new AdvisorConnectionException("rate_limited", 429));
            var sub = new FakeProvider("sub", (session, _, _, _) =>
            {
                var history = session.Continuation ?? throw new InvalidOperationException("Missing handoff");
                Require(history.Retrievals.Count == 1 && history.Retrievals[0]?["response"]?["results"]?.Count() == 1 &&
                    history.Operations.Count == 2 && history.RoundsUsed == 1 &&
                    (string?)history.Retrievals[0]?["response"]?["results"]?[0]?["data"]?["text"] == "Frozen briefing",
                    "Mixed-read facts were duplicated or lost at a provider boundary");
                return Task.FromResult(new AdvisorReply("Retained facts", Array.Empty<AdvisorToolCall>(), 1));
            });
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(main, sub).ConsultAsync(
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
            Require(result.Completed && result.ApiRequests == 3 && result.QueryRounds == 1 && operation.Writes == 0,
                "Fallback repeated a read or executed an operation");
        });
        check("Unexpected snapshot errors escape the read facade instead of becoming normal item errors", () =>
        {
            var reads = AdvisorContext.Create(new BrokenReadKnowledge(), Array.Empty<ChatExchange>()).Reads;
            try { _ = reads.ExecuteAsync(SharedReadArguments(QueryTests.Q("catalog")), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) when (error.Message == "broken source") { return; }
            throw new InvalidOperationException("An internal source error was hidden");
        });
    }
    private static BattleQuerySession ReadKnowledge(string referenceText = "Reference")
    {
        var snapshot = new JObject
        {
            ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = "Frozen briefing",
            ["friendly_units"] = new JArray(Enumerable.Range(1, 2).Select(i => new JObject { ["id"] = "own_unit_" + i, ["display_name"] = "Own " + i, ["kind"] = "surface" })),
            ["movement_plans"] = new JArray(Enumerable.Range(1, 2).Select(i => new JObject { ["id"] = "own_unit_" + i, ["route_unit_id"] = "own_unit_" + i })),
            ["movement_routes"] = new JArray(Enumerable.Range(1, 2).Select(i => new JObject { ["id"] = "own_unit_" + i, ["waypoints"] = new JArray() })),
            ["unit_capabilities"] = new JArray(new JObject { ["id"] = "own_unit_1", ["display_name"] = "Own 1", ["weapons"] = new JObject
                { ["ammunition"] = new JArray(new JObject { ["reference_id"] = "weapon_reference_1", ["display_name"] = "Test weapon", ["remaining_count"] = 4 }) } }),
            ["weapon_references"] = new JArray(new JObject { ["id"] = "weapon_reference_1", ["reference_key"] = "test_weapon", ["details"] = referenceText }),
            ["observed_contacts"] = new JArray(new JObject { ["id"] = "observed_contact_1", ["kind"] = "surface", ["posture"] = "enemy" },
                new JObject { ["id"] = "observed_contact_2", ["kind"] = "air", ["posture"] = "enemy" })
        };
        foreach (var name in new[] { "public_objectives", "mission_map_areas", "friendly_bases", "hosted_air_units", "aviation_hosts", "contact_threat_references", "published_reports" })
            snapshot[name] = new JArray();
        return new BattleQuerySession(new BattleSnapshot(snapshot), "ja", "Plan", maxRounds: 10);
    }
    private sealed class LookupOperation : IAdvisorOperation
    {
        internal int Writes, Thread;
        internal string Detail = "Current details";
        internal Action? OnCandidates;
        public string Id => "example.inspect";
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "An externally defined example operation";
        public bool RequiresApproval => true;
        public JObject Definition() => new() { ["id"] = Id, ["parameters"] = new JObject { ["type"] = "object" } };
        public JArray Candidates()
        {
            Thread = Environment.CurrentManagedThreadId; OnCandidates?.Invoke();
            return new JArray(new JObject { ["target_ref"] = "unit:42", ["details"] = Detail }, new JObject { ["target_ref"] = "unit:43" });
        }
        public void Validate(System.Collections.Generic.IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(System.Collections.Generic.IReadOnlyList<AdvisorOperationOrder> orders, string language) => "Example";
        public JObject Execute(string target, JObject arguments) { Writes++; return new JObject(); }
    }
    private sealed class BrokenReadKnowledge : IAdvisorQuerySession
    {
        private readonly Knowledge inner = new(2);
        public string Id => inner.Id;
        public int RoundsUsed => inner.RoundsUsed;
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        public JObject InitialInput() => inner.InitialInput();
        public JObject Execute(JObject request) => throw new InvalidOperationException("broken source");
    }
}
