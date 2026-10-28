using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class ReservationCompositionTests
{
    private static JObject LoopPolicy(int target, string placement, int segment, int point)
    {
        var policy = Policy(target, placement);
        policy["loop_from"] = new JObject { ["segment"] = segment, ["point"] = point };
        return policy;
    }
    private static void RunLoops(Action<string, Action> check)
    {
        check("An appended loop excludes existing reservations and the one-time approach across interleaved target segments", () =>
        {
            var game = new Game(); var service = Service(game, "loop");
            var plan = Plan(new[] { LoopPolicy(42, "append", 2, 2), Policy(43, "append") },
                Move(42, 1, 2), Move(43, 20), Move(42, 3, 4, 5), Move(43, 21), Move(42, 6, 7));
            service.SubmitPlans("loop", Batch(plan)); var proposal = service.FinishProposal("loop", true)!;
            var preview = service.PreviewProposal(proposal, "en");
            var closure = preview.Drawings.Last(d => d.Kind == AdvisorMapDrawingKind.Line);
            Require(closure.Coordinates.Select(c => c.Latitude).SequenceEqual(new[] { 7d, 4d }) && game.Log.Count == 0,
                "Preview loop included the approach or another target, or review changed reservations");
            var display = AdvisorOperationTargets.Render(proposal.Plans, AdvisorText.Load("ja"), id => "Unit " + id);
            Require(display.Descendants().Count(e => (string?)e.Attribute("Text") == "Loop") == 1,
                "Loop approval did not distinguish the repeating target");
            service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Loops[42].Select(p => p.Latitude).SequenceEqual(new[] { 4d, 5d, 6d, 7d }) &&
                !game.Loops.ContainsKey(43) && game.Queues[42][0] == "old" && game.Log.Count(l => l == "loop:42") == 1 &&
                !game.Log.Any(l => l.StartsWith("clear:", StringComparison.Ordinal)) && game.Log.Last() == "loop:42",
                "Loop scope, append preservation, completion timing or replay protection failed");
        });
        check("Independent loop starts on two targets survive replacement and finish before immediate actions", () =>
        {
            var game = new Game(); var service = Service(game, "two");
            var action = new PhaseAction("fixture.immediate", OperationExecutionMode.Immediate, game.Log); service.Register(action);
            var plan = Plan(new[] { LoopPolicy(42, "replace", 1, 2), LoopPolicy(43, "append", 1, 1) }, Move(42, 1, 2, 3), Move(43, 4, 5));
            plan["immediate_operations"] = new JArray(PhaseOrder(action.Id, 42, 9));
            service.SubmitPlans("two", Batch(plan)); var proposal = service.FinishProposal("two", true)!; service.Approve(proposal);
            Require(game.Loops[42].Select(p => p.Latitude).SequenceEqual(new[] { 2d, 3d }) &&
                game.Loops[43].Select(p => p.Latitude).SequenceEqual(new[] { 4d, 5d }) && game.Queues[42].All(p => p != "old") &&
                game.Queues[43][0] == "old" && game.Log.Last() == "fixture.immediate:unit:42:9",
                "Loop ranges, per-target placement or reservation-before-immediate timing changed");
        });
        check("A nonrepeatable one-time task may precede a movement loop but cannot be inside it", () =>
        {
            var game = new Game(); var service = Service(game, "prefix");
            service.SubmitPlans("prefix", Batch(Plan(new[] { LoopPolicy(42, "append", 2, 1) }, Drop(42, 1), Move(42, 2, 3))));
            service.Approve(service.FinishProposal("prefix", true)!);
            Require(game.Log.SequenceEqual(new[] { "drop:42:1", "move:42:2", "move:42:3", "loop:42" }) &&
                game.Loops[42].Select(p => p.Latitude).SequenceEqual(new[] { 2d, 3d }), "The prefix was repeated or forbidden outside the loop");
            foreach (var orders in new[] { new[] { Move(42, 1, 2), Drop(42, 3) }, new[] { Move(42, 1, 2), Return(42) }, new[] { Drop(42, 1), Move(42, 2, 3) } })
            {
                var rejectedGame = new Game(); var rejected = Service(rejectedGame, "invalid");
                Expect("reservation_not_repeatable", () => rejected.SubmitPlans("invalid", Batch(Plan(new[] { LoopPolicy(42, "append", 1, 1) }, orders))));
                Require(rejectedGame.Log.Count == 0, "A nonrepeatable loop changed the game before rejection");
            }
        });
        check("Invalid loop boundaries and degenerate routes are rejected without replacing an earlier valid proposal", () =>
        {
            var policies = new List<JObject> { LoopPolicy(42, "append", 0, 1), LoopPolicy(42, "append", 3, 1),
                LoopPolicy(42, "append", 1, 0), LoopPolicy(42, "append", 1, 4), LoopPolicy(42, "append", 1, 3) };
            var missing = Policy(42, "append"); missing["loop_from"] = JValue.CreateNull(); policies.Add(missing);
            var extra = LoopPolicy(42, "append", 1, 1); extra["loop_from"]!["end"] = 2; policies.Add(extra);
            var fractional = LoopPolicy(42, "append", 1, 1); fractional["loop_from"]!["point"] = 1.5; policies.Add(fractional);
            foreach (var policy in policies)
            {
                var game = new Game(); var service = Service(game, "range");
                service.SubmitPlans("range", Batch(Plan(new[] { Policy(42, "append") }, Move(42, 6, 7))));
                var previous = service.Proposal("range")!.Orders.Select(o => o.Arguments.ToString()).ToArray();
                var request = Batch(Plan(new[] { policy }, Move(42, 1, 2, 3))); request["request_id"] = "invalid-loop";
                Expect("invalid_reservation_loop", () => service.SubmitPlans("range", request));
                Require(service.Proposal("range")!.Orders.Select(o => o.Arguments.ToString()).SequenceEqual(previous) && game.Log.Count == 0,
                    "A rejected loop revision replaced valid work or executed reservations");
            }
            var duplicateGame = new Game(); var duplicate = Service(duplicateGame, "degenerate");
            Expect("invalid_reservation_loop", () => duplicate.SubmitPlans("degenerate", Batch(Plan(new[] { LoopPolicy(42, "replace", 1, 1) }, Move(42, 1, 1)))));
            Require(duplicateGame.Log.Count == 0, "A degenerate loop cleared old reservations");
        });
        check("A loop setup failure remains an unknown changed outcome and never executes remaining targets or replays work", () =>
        {
            var game = new Game { RejectLoop = true }; var service = Service(game, "failed");
            service.SubmitPlans("failed", Batch(Plan(new[] { LoopPolicy(42, "append", 1, 1), Policy(43, "append") }, Move(42, 1, 2), Move(43, 3))));
            var proposal = service.FinishProposal("failed", true)!;
            try { service.Approve(proposal); throw new Exception("A failed loop was hidden"); }
            catch (InvalidOperationException error) when (error.InnerException is AdvisorOperationException) { }
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Unknown && game.Loops.Count == 0 &&
                game.Log.SequenceEqual(new[] { "move:42:1", "move:42:2" }) && game.Queues[43].Single() == "old",
                "A changed loop failure was reported as a clean rejection, continued or replayed");
        });
        check("An existing loop blocks append and replacing it with a one-way route removes repetition", () =>
        {
            var game = new Game(); game.Loops[42] = new[] { new AdvisorMapCoordinate(1, 127), new AdvisorMapCoordinate(2, 127) };
            var service = Service(game, "old-loop");
            Expect("reservation_loop_conflict", () => service.SubmitPlans("old-loop", Batch(Plan(new[] { Policy(42, "append") }, Move(42, 3)))));
            Require(game.Log.Count == 0 && game.Loops.ContainsKey(42), "Append modified an existing loop");
            service.SubmitPlans("old-loop", Batch(Plan(new[] { Policy(42, "replace") }, Move(42, 3))));
            service.Approve(service.FinishProposal("old-loop", true)!);
            Require(!game.Loops.ContainsKey(42) && game.Log.SequenceEqual(new[] { "clear:42", "move:42:3" }),
                "A one-way replacement retained or independently restarted the old loop");
        });
        check("Adoption revalidates loop control before replacing the queue", () =>
        {
            var game = new Game(); var service = Service(game, "stale");
            service.SubmitPlans("stale", Batch(Plan(new[] { LoopPolicy(42, "replace", 1, 1) }, Move(42, 1, 2))));
            var proposal = service.FinishProposal("stale", true)!; game.UnavailableTarget = 42; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && game.Log.Count == 0 && game.Loops.Count == 0 && game.Queues[42].Single() == "old",
                "A stale loop bypassed adoption-time control checks");
        });
    }
}
