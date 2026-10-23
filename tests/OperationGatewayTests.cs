using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OperationGatewayTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Operation discovery pages and searches registered summaries without loading every argument definition", () =>
        {
            var service = new AdvisorOperations(() => true);
            var entries = Enumerable.Range(0, 47).Select(i => new Operation("custom.operation_" + i.ToString("D2"))).ToArray();
            foreach (var entry in entries.Reverse()) service.Register(entry);
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("discovery", AdvisorPrompts.Current)!;
            Require(!session.Tool.ToString().Contains("custom.operation_", StringComparison.Ordinal), "Initial tools contain registered operation definitions");
            var first = Send(gateway, session, new JObject { ["action"] = "list", ["limit"] = 20 });
            var next = Send(gateway, session, new JObject { ["action"] = "list", ["offset"] = 20, ["limit"] = 20 });
            Require((int?)Send(gateway, session, new JObject { ["action"] = "list", ["search"] = " " })["total_count"] == 47,
                "Empty search could not browse available operations");
            Require((int?)first["total_count"] == 47 && (bool?)first["has_more"] == true &&
                (string?)first["operations"]![0]!["id"] == "custom.operation_00" &&
                (string?)next["operations"]![0]!["id"] == "custom.operation_20" && entries.All(o => o.Definitions == 0),
                "Paging is unstable or discovery loaded all argument definitions");
            var found = Send(gateway, session, new JObject { ["action"] = "list", ["search"] = "OPERATION_37" });
            Require(found["operations"]!.Count() == 1 && (string?)found["operations"]![0]!["id"] == entries[37].Id, "Search did not discover an added operation");
            var detail = Send(gateway, session, new JObject { ["action"] = "describe", ["operation_id"] = entries[37].Id });
            Require((string?)detail["operation"]?["parameters"]?["type"] == "object" && entries[37].Definitions == 1 &&
                entries.Where((_, i) => i != 37).All(o => o.Definitions == 0), "Describe did not isolate the selected definition");
            var candidates = Send(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = entries[37].Id, ["target_ref"] = "custom:7" });
            Require((int?)candidates["total_count"] == 1 && (string?)candidates["candidates"]![0]!["target_ref"] == "custom:7" &&
                JToken.DeepEquals(candidates["operation"], detail["operation"]) &&
                entries.Where((_, i) => i != 37).All(o => o.Definitions == 0) && entries.All(o => o.Calls == 0),
                "Candidate lookup lost its matching definition, queried another operation or changed game state");
            var nextPage = Send(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = entries[37].Id, ["offset"] = 1, ["limit"] = 1 });
            var empty = Send(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = entries[37].Id, ["target_ref"] = "custom:missing" });
            Require((int?)nextPage["total_count"] == 2 && (int?)nextPage["offset"] == 1 && (bool?)nextPage["has_more"] == false &&
                (string?)nextPage["candidates"]?[0]?["target_ref"] == "custom:8" &&
                (int?)empty["total_count"] == 0 && empty["candidates"]!.Count() == 0 &&
                JToken.DeepEquals(nextPage["operation"], detail["operation"]) && JToken.DeepEquals(empty["operation"], detail["operation"]),
                "Paging or an empty candidate set removed the operation contract or changed candidate filtering");
        });
        check("AI operation requests run on the owning game thread and recheck WIP after enqueue", () =>
        {
            var enabled = true; var operation = new Operation("custom.test"); var service = Registry(() => enabled, operation);
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("permission", AdvisorPrompts.Current)!;
            var task = Task.Run(() => session.ExecuteAsync(Execute(operation.Id), CancellationToken.None));
            Require(operation.Calls == 0, "A provider worker changed the game directly");
            enabled = false;
            Require(Code(Finish(gateway, task)) == "unit_operations_disabled" && operation.Calls == 0, "Queued work bypassed disabled permission");
            Require(gateway.CreateSession("off", AdvisorPrompts.Current) == null, "Disabled WIP advertised an operation service");
            enabled = true;
            var answer = Finish(gateway, Task.Run(() => session.ExecuteAsync(Execute(operation.Id), CancellationToken.None)));
            Require((string?)answer["status"] == "proposed" && operation.Calls == 0, "AI bypassed staging");
            service.FinishProposal("permission", true);
            Require(operation.Calls == 1 && operation.Thread == Environment.CurrentManagedThreadId,
                "Operation did not execute once on the game thread");
        });
        check("Cancelled sessions and retired battle gateways never register queued orders", () =>
        {
            var operation = new Operation("custom.test"); var service = Registry(() => true, operation);
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("cancel", AdvisorPrompts.Current)!;
            using var cancellation = new CancellationTokenSource();
            var cancelled = session.ExecuteAsync(Execute(operation.Id), cancellation.Token); cancellation.Cancel();
            try { Finish(gateway, cancelled); throw new InvalidOperationException("Cancelled operation succeeded"); }
            catch (OperationCanceledException) { }
            var retired = session.ExecuteAsync(Execute(operation.Id), CancellationToken.None); gateway.Dispose();
            Require(Code(retired.GetAwaiter().GetResult()) == "operation_scope_changed" && operation.Calls == 0, "Old battle work registered an order");
            session.Dispose();
            try { session.ExecuteAsync(Execute(operation.Id), CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("Disposed session executed"); }
            catch (OperationCanceledException) { }
        });
        check("Operation schemas reject malformed discovery and request envelopes without hidden execution", () =>
        {
            var operation = new Operation("custom.test"); using var gateway = new AdvisorOperationGateway(Registry(() => true, operation));
            using var session = gateway.CreateSession("validation", AdvisorPrompts.Current)!;
            foreach (var bad in new[] {
                new JObject { ["action"] = "list", ["limit"] = 21 }, new JObject { ["action"] = "list", ["offset"] = -1 },
                new JObject { ["action"] = "candidates", ["operation_id"] = operation.Id, ["limit"] = "1" },
                new JObject { ["action"] = "describe", ["operation_id"] = "missing" }, new JObject { ["action"] = "unsupported" },
                new JObject { ["action"] = "execute", ["operation_id"] = operation.Id, ["scope_id"] = "forged" },
                new JObject { ["action"] = "execute", ["operation_id"] = operation.Id, ["arguments"] = new JArray() }
            }) Require(Code(Send(gateway, session, bad)) != null, "Malformed operation request succeeded");
            Require(operation.Calls == 0, "Invalid arguments changed game state");
        });
        check("Retrying the same conversation turn reuses native receipts and exposes prior registration results", () =>
        {
            var operation = new Operation("custom.test"); var service = Registry(() => true, operation);
            using var gateway = new AdvisorOperationGateway(service);
            var conversation = new AdvisorConversation(); var turn = conversation.Begin(new AdvisorQuestion("Schedule an order"));
            using (var first = gateway.CreateSession(turn.OperationId, AdvisorPrompts.Current)!)
                Require((string?)Send(gateway, first, Execute(operation.Id))["status"] == "proposed", "Initial proposal failed");
            service.FinishProposal(turn.OperationId, true);
            var owner = turn.OperationId; turn.Cancel(); conversation.Retry(turn);
            using var retry = gateway.CreateSession(turn.OperationId, AdvisorPrompts.Current)!;
            Require(turn.OperationId == owner && retry.History.Count == 1 && (string?)retry.History[0]?["response"]?["status"] == "registered",
                "Retry lost its mechanical identity or prior result");
            Require((string?)Send(gateway, retry, Execute(operation.Id))["status"] == "registered" && operation.Calls == 1,
                "Retry duplicated a previously registered order");
            var altered = Execute(operation.Id); altered["arguments"]!["extra"] = true;
            Require(Code(Send(gateway, retry, altered)) == "request_id_conflict" && operation.Calls == 1, "A reused ID changed its registered order");
        });
        check("Unexpected native errors propagate and cannot be hidden by replaying the same AI order ID", () =>
        {
            var operation = new Operation("custom.test") { Fail = true }; var service = Registry(() => true, operation);
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("failure", AdvisorPrompts.Current)!;
            var work = session.ExecuteAsync(Execute(operation.Id), CancellationToken.None);
            Require((string?)Finish(gateway, work)["status"] == "proposed" && operation.Calls == 0, "A staged fault ran early");
            try { service.FinishProposal("failure", true); throw new InvalidOperationException("Native failure was hidden"); }
            catch (System.IO.IOException) { }
            Require(Code(Send(gateway, session, Execute(operation.Id))) == "operation_outcome_unknown" && operation.Calls == 1,
                "Unknown side effects were replayed");
        });
        check("Independent operation discovery is bounded even when no standard engine manages its loop", () =>
        {
            var operation = new Operation("custom.test"); using var gateway = new AdvisorOperationGateway(Registry(() => true, operation));
            using var session = gateway.CreateSession("bounded", AdvisorPrompts.Current)!;
            for (var i = 0; i < 64; i++) Require(Send(gateway, session, new JObject { ["action"] = "list" })["operations"] != null, "Valid discovery was rejected too early");
            Require(Code(Send(gateway, session, new JObject { ["action"] = "list" })) == "operation_budget_exhausted" && operation.Calls == 0,
                "An independent workflow bypassed operation request limits");
        });
    }
    internal static T Finish<T>(AdvisorOperationGateway gateway, Task<T> task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(8)) { gateway.Pump(); Thread.Sleep(1); }
        Require(task.IsCompleted, "Game-thread operation did not finish in the test deadline");
        return task.GetAwaiter().GetResult();
    }
    private static JObject Send(AdvisorOperationGateway gateway, IAdvisorOperationSession session, JObject request) =>
        Finish(gateway, session.ExecuteAsync(request, CancellationToken.None));
    private static string? Code(JObject result) => (string?)result["error"]?["code"] ?? (string?)result["code"];
    internal static JObject Execute(string operation) => new() { ["action"] = "execute", ["operation_id"] = operation,
        ["request_id"] = "order-1", ["target_ref"] = "custom:7", ["arguments"] = new JObject() };
    private static AdvisorOperations Registry(Func<bool> enabled, Operation operation)
    { var registry = new AdvisorOperations(enabled); registry.Register(operation); return registry; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Operation : IAdvisorOperation
    {
        public bool RequiresApproval => false;
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => "Custom operation";
        public string Id { get; }
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Custom extension action";
        internal int Calls, Definitions, Thread;
        internal bool Fail;
        internal Operation(string id) => Id = id;
        public JObject Definition() { Definitions++; return new JObject { ["id"] = Id, ["description"] = Description, ["parameters"] = new JObject { ["type"] = "object" } }; }
        public JArray Candidates() => new(new JObject { ["target_ref"] = "custom:7" }, new JObject { ["target_ref"] = "custom:8" });
        public JObject Execute(string target, JObject arguments)
        { Calls++; Thread = Environment.CurrentManagedThreadId; if (Fail) throw new System.IO.IOException("Native failure"); return new JObject { ["target_ref"] = target }; }
    }
}
