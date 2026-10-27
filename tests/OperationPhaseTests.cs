using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class ReservationCompositionTests
{
    private static void RunPhases(Action<string, Action> check)
    {
        check("Plan envelopes require both phase arrays, reject the old format and bound their combined operation count", () =>
        {
            var valid = Batch(Plan(new[] { Policy(42, "append") }, Move(42, 1)));
            AdvisorOperationPlanBatch.Validate(valid);
            foreach (var field in new[] { "immediate_operations", "reservation_operations" })
            {
                var missing = (JObject)valid.DeepClone(); ((JObject)missing["plans"]![0]!).Remove(field);
                Expect("invalid_plan_submission", () => AdvisorOperationPlanBatch.Validate(missing));
            }
            var old = (JObject)valid.DeepClone(); var oldPlan = (JObject)old["plans"]![0]!;
            oldPlan["operations"] = oldPlan["reservation_operations"]!.DeepClone();
            Expect("invalid_plan_submission", () => AdvisorOperationPlanBatch.Validate(old));
            oldPlan.Remove("immediate_operations"); oldPlan.Remove("reservation_operations");
            Expect("invalid_plan_submission", () => AdvisorOperationPlanBatch.Validate(old));
            var empty = Batch(Plan(Array.Empty<JObject>()));
            Expect("invalid_plan_submission", () => AdvisorOperationPlanBatch.Validate(empty));
            var atLimit = Batch(Plan(new[] { Policy(42, "append") }, Enumerable.Range(0, 20).Select(i => Move(42, i)).ToArray()));
            atLimit["plans"]![0]!["immediate_operations"] = new JArray(Enumerable.Range(0, 20).Select(i => PhaseOrder("custom.instant", 42, i)));
            AdvisorOperationPlanBatch.Validate(atLimit);
            ((JArray)atLimit["plans"]![0]!["immediate_operations"]!).Add(PhaseOrder("custom.instant", 42, 20));
            Expect("invalid_plan_submission", () => AdvisorOperationPlanBatch.Validate(atLimit));
        });
        check("Command modes select their phase and mismatches cannot replace a valid submitted proposal", () =>
        {
            var game = new Game(); var service = Service(game, "phase");
            service.Register(new PhaseAction("custom.instant", OperationExecutionMode.Immediate, game.Log));
            var first = Batch(PhasePlan("one", new[] { PhaseOrder("custom.instant", 42, 1) }, new[] { Move(42, 2) }, Policy(42, "replace")));
            service.SubmitPlans("phase", first); var proposal = service.Proposal("phase")!;
            foreach (var wrong in new[] { "immediate_operations", "reservation_operations" })
            {
                var bad = (JObject)first.DeepClone(); bad["request_id"] = wrong;
                var plan = bad["plans"]![0]!;
                var from = wrong == "immediate_operations" ? "reservation_operations" : "immediate_operations";
                var item = plan[from]![0]!; item.Remove(); ((JArray)plan[wrong]!).Add(item);
                Expect("operation_phase_mismatch", () => service.SubmitPlans("phase", bad));
                Require(JToken.DeepEquals(proposal.Submission, first) && proposal.SubmissionCount == 1 && game.Log.Count == 0,
                    "A wrong-phase revision replaced valid work or changed game state");
            }
            foreach (var id in new[] { "custom.instant", MovementExtension.OperationId })
            {
                var expected = id == "custom.instant" ? "immediate" : "reservation";
                Require((string?)service.Discover(id, 0, 20)["operations"]![0]!["execution_mode"] == expected &&
                    (string?)service.Describe(id)["operation"]!["execution_mode"] == expected &&
                    (string?)service.CandidatePage(id, null, 0, 20)["operation"]!["execution_mode"] == expected,
                    "Discovery and candidate contracts disagree about operation timing");
            }
        });
        check("Same-unit and cross-unit reservations are registered before immediate actions and reservation interleaving remains intact", () =>
        {
            var game = new Game(); var service = Service(game, "mixed-phases");
            service.Register(new PhaseAction("custom.instant", OperationExecutionMode.Immediate, game.Log));
            var request = Batch(PhasePlan("one", new[] { PhaseOrder("custom.instant", 42, 1), PhaseOrder("custom.instant", 43, 2) },
                new[] { Move(42, 3), Drop(42, 4), Move(43, 5), Move(42, 6) }, Policy(42, "replace"), Policy(43, "append")));
            service.SubmitPlans("mixed-phases", request); var proposal = service.FinishProposal("mixed-phases", true)!;
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var description = service.DescribeProposal(proposal, language);
                Require(description.IndexOf(text["operation.reservation_heading"], StringComparison.Ordinal) <
                    description.IndexOf(text["operation.immediate_heading"], StringComparison.Ordinal), "Approval descriptions lost their phase order");
                _ = service.PreviewProposal(proposal, language);
            }
            Require(game.Log.Count == 0, "Review executed an immediate action or touched reservations");
            service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] {
                "clear:42", "move:42:3", "drop:42:4", "move:43:5", "move:42:6", "custom.instant:unit:42:1", "custom.instant:unit:43:2" }),
                "Phases were interleaved, reservation order changed or adoption replayed actions");
        });
        check("Selecting several plans registers every reservation first in submitted plan order regardless of selection click order", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(_ => true);
            service.Register(new PhaseAction("custom.instant", OperationExecutionMode.Immediate, log));
            service.Register(new PhaseAction("custom.reserve", OperationExecutionMode.Reservation, log));
            service.BeginProposal("selection");
            service.SubmitPlans("selection", Batch(
                PhasePlan("one", new[] { PhaseOrder("custom.instant", 42, 1), PhaseOrder("custom.instant", 43, 2) }, new[] { PhaseOrder("custom.reserve", 42, 3) }),
                PhasePlan("two", new[] { PhaseOrder("custom.instant", 42, 4) }, new[] { PhaseOrder("custom.reserve", 43, 5), PhaseOrder("custom.reserve", 42, 6) })));
            var proposal = service.FinishProposal("selection", true)!;
            service.Approve(proposal, new[] { "two", "one" }); service.Approve(proposal, new[] { "one", "two" });
            Require(proposal.State == AdvisorProposalState.Registered && log.SequenceEqual(new[] {
                "custom.reserve:unit:42:3", "custom.reserve:unit:43:5", "custom.reserve:unit:42:6",
                "custom.instant:unit:42:1", "custom.instant:unit:43:2", "custom.instant:unit:42:4" }),
                "An immediate action ran before a later plan's reservation, selection reordered work or execution repeated");
        });
        check("Preflight blocks both phases, reservation failure blocks immediate work and an immediate failure retains registered reservations without replay", () =>
        {
            foreach (var failure in new[] { "reservation_preflight", "immediate_preflight", "immediate", "reservation" })
            {
                var game = new Game(); var service = Service(game, failure);
                var instant = new PhaseAction("custom.instant", OperationExecutionMode.Immediate, game.Log);
                service.Register(instant);
                service.SubmitPlans(failure, Batch(PhasePlan("one", new[] { PhaseOrder(instant.Id, 42, 1) },
                    new[] { Move(43, 2) }, Policy(43, "append"))));
                var proposal = service.FinishProposal(failure, true)!;
                var preflight = failure.EndsWith("_preflight", StringComparison.Ordinal);
                if (failure == "reservation_preflight") game.UnavailableTarget = 43;
                if (failure == "immediate_preflight") instant.ValidationFail = true;
                if (failure == "immediate") instant.Fail = true;
                if (failure == "reservation") game.RejectRegistration = true;
                service.Approve(proposal);
                Require(game.Queues[43].SequenceEqual(failure == "immediate" ? new[] { "old", "move" } : new[] { "old" }) &&
                    game.Log.SequenceEqual(failure == "immediate" ? new[] { "move:43:2" } : Array.Empty<string>()),
                    "A failed phase changed downstream work or lost a registered reservation");
                Require(proposal.State == (preflight ? AdvisorProposalState.Pending :
                    failure == "immediate" ? AdvisorProposalState.Partial : AdvisorProposalState.Rejected),
                    "The failure hid completed reservations or treated preflight as execution");
                if (!preflight)
                {
                    instant.Fail = false; game.RejectRegistration = false; var count = game.Log.Count;
                    service.Approve(proposal); Require(game.Log.Count == count, "A failed adoption replayed completed work");
                }
            }
        });
        check("Noncomposing commands retain multi-order descriptions and connected previews within each phase", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(_ => true);
            service.Register(new PhaseAction("custom.instant", OperationExecutionMode.Immediate, log));
            service.Register(new PhaseAction("custom.reserve", OperationExecutionMode.Reservation, log));
            service.BeginProposal("review");
            service.SubmitPlans("review", Batch(PhasePlan("one",
                new[] { PhaseOrder("custom.instant", 42, 1), PhaseOrder("custom.instant", 42, 2) },
                new[] { PhaseOrder("custom.reserve", 42, 3), PhaseOrder("custom.reserve", 42, 4) })));
            var proposal = service.FinishProposal("review", true)!;
            var description = service.DescribeProposal(proposal, "en"); var preview = service.PreviewProposal(proposal, "en");
            Require(description.Contains("custom.instant:2", StringComparison.Ordinal) && description.Contains("custom.reserve:2", StringComparison.Ordinal) &&
                preview.Drawings.Count == 2 && preview.Drawings[0].Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 3d, 4d }) &&
                preview.Drawings[1].Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 1d, 2d }) && log.Count == 0,
                "Phase separation split a command's description or disconnected its multi-order preview");
        });
        check("An immediate command cannot declare participation in reservation composition", () =>
        {
            try { new AdvisorOperationCatalog().Register(new WrongPhaseExtension()); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Contradictory execution metadata was accepted");
        });
        check("A noncomposing reservation command stays in the reservation phase but cannot bypass shared-queue composition checks", () =>
        {
            var game = new Game(); var service = Service(game, "noncomposing");
            var action = new PhaseAction("custom.reserve", OperationExecutionMode.Reservation, game.Log); service.Register(action);
            var bad = Batch(PhasePlan("one", Array.Empty<JObject>(), new[] { PhaseOrder(action.Id, 42, 1), Move(42, 2) }, Policy(42, "append")));
            Expect("operation_not_composable", () => service.SubmitPlans("noncomposing", bad));
            var good = Batch(PhasePlan("one", Array.Empty<JObject>(), new[] { PhaseOrder(action.Id, 43, 1), Move(42, 2) }, Policy(42, "append")));
            service.SubmitPlans("noncomposing", good); service.Approve(service.FinishProposal("noncomposing", true)!);
            Require(game.Log.SequenceEqual(new[] { "custom.reserve:unit:43:1", "move:42:2" }), "Noncomposing reservation timing was inferred from the handler interface");
        });
    }
    private static JObject PhasePlan(string id, IEnumerable<JObject> immediate, IEnumerable<JObject> reserved, params JObject[] policies) => new() {
        ["plan_id"] = id, ["title"] = id, ["summary"] = "Timing test", ["reservation_updates"] = new JArray(policies),
        ["immediate_operations"] = new JArray(immediate), ["reservation_operations"] = new JArray(reserved) };
    private static JObject PhaseOrder(string id, int target, int index) => Order(id, target, new JObject { ["index"] = index });
    private sealed class WrongPhaseExtension : IAdvisorOperationExtension, IAdvisorReservationExtension
    {
        public int ApiVersion => AdvisorApi.ContractVersion;
        public bool EndsReservationSequence => false;
        public OperationDefinition GetDefinition()
        {
            var source = new SonobuoyDropExtension().GetDefinition();
            return new OperationDefinition(source.Id, OperationExecutionMode.Immediate, source.Description, source.ParametersJson, source.Localizations);
        }
        public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => throw new InvalidOperationException("Invalid metadata created a session");
    }
    private sealed class PhaseAction : IAdvisorOperation, IAdvisorOperationPlans, SeaPowerAIAdvisor.IAdvisorOperationPreview
    {
        private readonly List<string> log;
        public string Id { get; }
        public OperationExecutionMode ExecutionMode { get; }
        public string Description => Id;
        public AdvisorOperationText Documentation { get; } = new(new Dictionary<string, string> { ["en"] = "{\"name\":\"Timing test\",\"summary\":\"Test action\",\"requirements\":\"Test target\"}" });
        public bool RequiresApproval => true;
        public AdvisorOperationPlanPolicy PlanPolicy => new(1, 1, 8, AdvisorPlanSelection.Multiple);
        internal bool Fail, ValidationFail;
        internal PhaseAction(string id, OperationExecutionMode mode, List<string> log) { Id = id; ExecutionMode = mode; this.log = log; }
        public JObject Definition() => new() { ["type"] = "object" };
        public JArray Candidates() => new();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders)
        { if (ValidationFail) throw new AdvisorOperationException(409, "target_unavailable"); }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Id + ":" + orders.Count;
        public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language) => orders.Count < 2
            ? AdvisorMapPreview.Empty : new AdvisorMapPreview(new[] {
                AdvisorMapDrawing.Line(orders.Select(o => new AdvisorMapCoordinate((int)o.Arguments["index"]!, 127))) });
        public JObject Execute(string target, JObject args)
        {
            if (Fail) throw new AdvisorOperationException(409, "target_unavailable");
            log.Add(Id + ":" + target + ":" + (int)args["index"]!); return new JObject();
        }
    }
}
