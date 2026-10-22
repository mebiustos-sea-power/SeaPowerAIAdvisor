using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OperationTargetTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Operation candidates resolve exact consultation aliases and retain the original request in diagnostics", () =>
        {
            using var trial = new Trial();
            var source = new BattleAdvisorSource(() => throw new InvalidOperationException("Unexpected capture"), () => trial.Gateway);
            var recording = new AdvisorLocalRun();
            using var session = recording.Observe(source.CreateOperations("candidates", AdvisorPrompts.Current, References())!);
            foreach (var alias in new[] { "own_unit_8", "unit:own_unit_8" })
            {
                var request = Candidates(alias); var original = request.DeepClone();
                var result = Send(trial, session, request);
                Require((int?)result["total_count"] == 1 && (string?)result["candidates"]?[0]?["target_ref"] == "unit:42",
                    "The alias did not select its mapped unit");
                Require((string?)result["target_ref_resolution"]?["requested"] == alias &&
                    (string?)result["target_ref_resolution"]?["resolved"] == "unit:42" && JToken.DeepEquals(request, original),
                    "Resolution changed the caller's request or omitted the correction");
                var entry = session.History.Last!;
                var recorded = recording.Snapshot()["operations"]!.Last!;
                Require(JToken.DeepEquals(entry["request"], original) && JToken.DeepEquals(recorded["request"], original) &&
                    JToken.DeepEquals(recorded["response"], result), "The original alias and resolved target were lost from logs");
            }
            Require(trial.Main.Orders.Count == 0 && trial.Other.Orders.Count == 0, "Candidate resolution executed an order");
        });
        check("Numeric and custom target references keep their meaning and unknown aliases never broaden a candidate filter", () =>
        {
            using var trial = new Trial();
            using var session = trial.Gateway.CreateSession("strict", AdvisorPrompts.Current, References())!;
            var native = Send(trial, session, Candidates("unit:8"));
            Require((int?)native["total_count"] == 1 && (string?)native["candidates"]?[0]?["target_ref"] == "unit:8" &&
                native["target_ref_resolution"] == null, "A real numeric unit ID was interpreted as a snapshot index");
            foreach (var alias in new[] { "own_unit_9", "unit:own_unit_9", "own_unit_99", "unit:own_unit_08" })
            {
                Require(Code(Send(trial, session, Candidates(alias))) == "unknown_unit_reference", "An unknown or excluded alias returned candidates");
                Require(Code(Send(trial, session, Execute(alias))) == "unknown_unit_reference", "An unknown or excluded alias created a proposal");
            }
            foreach (var target in new[] { "custom:8", "Hobby 1", "unit:observed_contact_8" })
                Require((int?)Send(trial, session, Candidates(target))["total_count"] == 0, "An unrelated target was guessed from its spelling");
            var proposal = trial.Service.FinishProposal("strict", true)!;
            Require(proposal.Orders.Count == 0 && trial.Main.Orders.Count == 0, "Invalid aliases left executable work");
        });
        check("Alias and canonical retries share one approved order and changed content still conflicts", () =>
        {
            using var trial = new Trial();
            using var session = trial.Gateway.CreateSession("replay", AdvisorPrompts.Current, References())!;
            foreach (var target in new[] { "own_unit_8", "unit:own_unit_8", "unit:42" })
            {
                var receipt = Send(trial, session, Execute(target));
                Require((string?)receipt["status"] == "proposed" && (string?)receipt["request_id"] == "replay.order-1" &&
                    (bool?)receipt["registered"] == false, "Equivalent targets did not share the original request identity");
            }
            var changed = Execute("unit:42"); changed["arguments"]!["latitude"] = 27;
            Require(Code(Send(trial, session, changed)) == "request_id_conflict", "A changed order silently replaced its receipt");
            var proposal = trial.Service.FinishProposal("replay", true)!;
            Require(proposal.State == AdvisorProposalState.Pending && proposal.Orders.Count == 1 &&
                proposal.Orders[0].Target == "unit:42" && trial.Main.Orders.Count == 0, "Resolution bypassed approval or duplicated a proposal");
            trial.Service.Approve(proposal); trial.Service.Approve(proposal);
            Require((string?)Send(trial, session, Execute("unit:own_unit_8"))["status"] == "registered" &&
                trial.Main.Orders.Count == 1 && trial.Main.Orders[0] == ("custom_buoy", 26.5, 127.5),
                "An alias retry registered twice or changed the operation arguments");
            using var retry = trial.Gateway.CreateSession("replay", AdvisorPrompts.Current,
                new AdvisorUnitReferences(new[] { ("own_unit_8", 8) }))!;
            Require(Code(Send(trial, retry, Execute("own_unit_8"))) == "request_id_conflict" && trial.Other.Orders.Count == 0,
                "A retry rebound a previously registered request to another unit");
        });
        check("Unit reference mappings are isolated to each consultation and absent mappings cannot reuse earlier aliases", () =>
        {
            using var trial = new Trial();
            using var first = trial.Gateway.CreateSession("first", AdvisorPrompts.Current, References())!;
            using var next = trial.Gateway.CreateSession("next", AdvisorPrompts.Current,
                new AdvisorUnitReferences(new[] { ("own_unit_8", 8) }))!;
            using var empty = trial.Gateway.CreateSession("empty", AdvisorPrompts.Current)!;
            Require((string?)Send(trial, first, Candidates("own_unit_8"))["candidates"]?[0]?["target_ref"] == "unit:42" &&
                (string?)Send(trial, next, Candidates("own_unit_8"))["candidates"]?[0]?["target_ref"] == "unit:8",
                "One consultation reused another consultation's alias map");
            Require(Code(Send(trial, empty, Candidates("own_unit_8"))) == "unknown_unit_reference" &&
                (int?)Send(trial, empty, Candidates("unit:42"))["total_count"] == 1, "An absent map guessed an ID or broke canonical callers");
        });
        check("Unit normalization leaves ammunition identifiers and conflicting request IDs unchanged", () =>
        {
            using var trial = new Trial();
            using var session = trial.Gateway.CreateSession("ammunition", AdvisorPrompts.Current, References())!;
            var request = Execute("unit:own_unit_8"); request["arguments"]!["ammunition"] = "weapon_reference_18";
            var original = request.DeepClone(); var rejected = Send(trial, session, request);
            Require((string?)rejected["status"] == "rejected" && Code(rejected) == "sonobuoy_not_carried" &&
                (string?)rejected["target_ref_resolution"]?["resolved"] == "unit:42" && JToken.DeepEquals(original, request),
                "Unit resolution rewrote ammunition or hid the command's validation error");
            Require(Code(Send(trial, session, Execute("unit:42"))) == "request_id_conflict" && trial.Main.Orders.Count == 0,
                "A rejected order ID was automatically replaced after changing ammunition");
        });
        check("Normalized proposals still revalidate live conditions at approval and respect disabled or retired gateways", () =>
        {
            using var trial = new Trial();
            using var session = trial.Gateway.CreateSession("live", AdvisorPrompts.Current, References())!;
            Require((string?)Send(trial, session, Execute("unit:own_unit_8"))["status"] == "proposed", "A valid alias did not reach staging");
            var proposal = trial.Service.FinishProposal("live", true)!;
            trial.Main.UnavailableReason = "current_order_conflict";
            trial.Service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Rejected && trial.Main.Orders.Count == 0,
                "The normalized target bypassed approval-time validation");
            trial.Enabled = false;
            Require(Code(Send(trial, session, Execute("own_unit_8", "disabled"))) == "unit_operations_disabled", "Normalization bypassed WIP");
            trial.Enabled = true;
            using var cancelled = new CancellationTokenSource();
            var pending = session.ExecuteAsync(Execute("own_unit_8", "cancelled"), cancelled.Token); cancelled.Cancel();
            try { OperationGatewayTests.Finish(trial.Gateway, pending); throw new InvalidOperationException("Cancelled alias succeeded"); }
            catch (OperationCanceledException) { }
            trial.Gateway.Dispose();
            Require(Code(session.ExecuteAsync(Execute("own_unit_8", "old-battle"), CancellationToken.None).GetAwaiter().GetResult()) == "operation_scope_changed" &&
                trial.Main.Orders.Count == 0, "A retired battle applied an old alias");
        });
    }
    private static AdvisorUnitReferences References() => new AdvisorUnitReferences(new[] { ("own_unit_8", 42), ("own_unit_9", 8) }).Only(new[] { "own_unit_8" });
    private static JObject Candidates(string target) => new() { ["action"] = "candidates", ["operation_id"] = BattleSonobuoyOperation.OperationId, ["target_ref"] = target };
    private static JObject Execute(string target, string id = "order-1") => new()
    {
        ["action"] = "execute", ["operation_id"] = BattleSonobuoyOperation.OperationId, ["request_id"] = id, ["target_ref"] = target,
        ["arguments"] = new JObject { ["latitude"] = 26.5, ["longitude"] = 127.5, ["ammunition"] = "custom_buoy" }
    };
    private static JObject Send(Trial trial, IAdvisorOperationSession session, JObject request) =>
        OperationGatewayTests.Finish(trial.Gateway, session.ExecuteAsync(request, CancellationToken.None));
    private static string? Code(JObject result) => (string?)result["error"]?["code"] ?? (string?)result["code"];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Trial : IDisposable
    {
        internal readonly Helicopter Main = new(42), Other = new(8);
        internal bool Enabled = true;
        internal readonly AdvisorOperations Service;
        internal readonly AdvisorOperationGateway Gateway;
        internal Trial()
        {
            Service = new AdvisorOperations(() => Enabled);
            Service.Register(new BattleSonobuoyOperation(() => new IBattleSonobuoyUnit[] { Main, Other }));
            Gateway = new AdvisorOperationGateway(Service);
        }
        public void Dispose() => Gateway.Dispose();
    }
    private sealed class Helicopter : IBattleSonobuoyUnit
    {
        public int Id { get; }
        public string Name => "Helicopter " + Id;
        public string? UnavailableReason { get; set; }
        public string Depth => "alternating";
        public JObject Position => new() { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 };
        internal readonly List<(string Ammunition, double Latitude, double Longitude)> Orders = new();
        internal Helicopter(int id) => Id = id;
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("custom_buoy", "Custom buoy", 12, 12, true) };
        public void Append(string ammunition, double latitude, double longitude) => Orders.Add((ammunition, latitude, longitude));
    }
}
