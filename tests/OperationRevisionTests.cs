using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class OperationPlanTests
{
    private static JObject Revision(string id, params JObject[] plans)
    { var request = Batch(plans); request["request_id"] = id; return request; }
    private static void RunRevisions(Action<string, Action> check)
    {
        check("A new complete revision replaces alternatives and mixed orders without executing the previous set", () =>
        {
            var log = new List<string>(); var a = new Command("custom.move", log); var b = new Command("custom.observe", log);
            var service = Service(a, "revisions"); service.Register(b);
            var first = Revision("first", Plan("old", Order(a, "one", 1)));
            service.SubmitPlans("revisions", first);
            var proposal = service.Proposal("revisions")!; var oldIds = proposal.Orders.Select(o => o.Id).ToArray();
            var latest = Revision("second", Plan("preferred", Order(b, "two", 2), Order(a, "one", 3)), Plan("alternative", Order(a, "three", 4)));
            var expected = (JObject)latest.DeepClone(); var result = service.SubmitPlans("revisions", latest);
            latest["plans"]![0]!["title"] = "caller mutation";
            Require(ReferenceEquals(service.Proposal("revisions"), proposal) && proposal.SubmissionCount == 2 &&
                JToken.DeepEquals(proposal.Submission, expected) && !proposal.Orders.Any(o => oldIds.Contains(o.Id)) &&
                (string?)result["request_id"] == "second" && (bool?)result["replaces_previous_submission"] == true && log.Count == 0, "Revision retained old orders, caller data or executed before approval");
            var prior = service.PriorOrders("revisions");
            Require(prior.Count == 1 && JToken.DeepEquals(prior[0]["request"], WithAction(expected)) &&
                (string?)prior[0]["response"]?["request_id"] == "second", "Continuation history retained a superseded proposal");
            service.FinishProposal("revisions", true); service.Approve(proposal, new[] { "preferred" }); service.Approve(proposal, new[] { "alternative" });
            Require(log.SequenceEqual(new[] { "custom.observe:two:2", "custom.move:one:3" }) && proposal.RegisteredCount == 2,
                "Approval executed old, unselected, duplicate or reordered operations");
            Require((bool?)service.SubmitPlans("revisions", expected)["registered"] == true && log.Count == 2,
                "Retry repeated execution of the current revision");
        });
        check("Old identical retries report superseded and changing any accepted request ID conflicts without rolling back plans", () =>
        {
            var log = new List<string>(); var command = new Command("custom.retry", log); var service = Service(command, "retry");
            var first = Revision("first", Plan("one", Order(command, "one", 1)));
            var latest = Revision("latest", Plan("two", Order(command, "one", 2)));
            service.SubmitPlans("retry", first); service.SubmitPlans("retry", latest);
            foreach (var afterApproval in new[] { false, true })
            {
                if (afterApproval) { service.FinishProposal("retry", true); service.Approve(service.Proposal("retry")!); }
                var old = service.SubmitPlans("retry", (JObject)first.DeepClone());
                Require((string?)old["status"] == "superseded" && (bool?)old["registered"] == false &&
                    (string?)old["current_submission"]?["request_id"] == "latest" &&
                    (bool?)old["current_submission"]?["registered"] == afterApproval,
                    "Old retry was reported as current or as having executed");
                old["current_submission"]!["plans"]![0]!["plan_id"] = "caller mutation";
                foreach (var request in new[] { first, latest })
                {
                    var changed = (JObject)request.DeepClone(); changed["plans"]![0]!["summary"] = "changed";
                    Expect("request_id_conflict", () => service.SubmitPlans("retry", changed));
                }
                Require(JToken.DeepEquals(service.Proposal("retry")!.Submission, latest) &&
                    service.Proposal("retry")!.SubmissionCount == 2 && log.Count == (afterApproval ? 1 : 0),
                    "A replay changed plans, consumed a revision or repeated execution");
            }
        });
        check("Rejected revisions leave the accepted plans, policy and retry identity intact and can be corrected", () =>
        {
            var log = new List<string>(); var command = new Command("custom.atomic", log) { Approval = false }; var service = Service(command, "atomic-revision");
            var first = Revision("first", Plan("one", Order(command, "one", 1)));
            service.SubmitPlans("atomic-revision", first); var proposal = service.Proposal("atomic-revision")!;
            var ids = proposal.Orders.Select(o => o.Id).ToArray(); var firstReceipt = service.SubmitPlans("atomic-revision", first);
            Require((bool?)firstReceipt["replaces_previous_submission"] == false, "Initial submission was reported as replacing another set");
            var bad = Revision("corrected", Plan("a", Order(command, "one", 2)), Plan("b", Order(command, "two", -1)));
            Expect("invalid_command_argument", () => service.SubmitPlans("atomic-revision", bad));
            var malformed = (JObject)bad.DeepClone(); malformed["plans"]![1]!["plan_id"] = "a";
            Expect("duplicate_plan_id", () => service.SubmitPlans("atomic-revision", malformed));
            Require(JToken.DeepEquals(proposal.Submission, first) && proposal.Orders.Select(o => o.Id).SequenceEqual(ids) &&
                !proposal.RequiresApproval && proposal.SubmissionCount == 1 && JToken.DeepEquals(service.SubmitPlans("atomic-revision", first), firstReceipt) && log.Count == 0,
                "A rejected revision changed valid plans, flags, receipts or game state");
            bad["plans"]![1]!["operations"]![0]!["arguments"]!["index"] = 3;
            service.SubmitPlans("atomic-revision", bad);
            Require(proposal.SubmissionCount == 2 && proposal.RequiresApproval && proposal.Plans.Count == 2,
                "A corrected rejected request ID was poisoned or kept the old approval policy");
        });
        check("Finishing or cancelling a consultation locks revisions even if approval is pending or the owner is reopened", () =>
        {
            foreach (var state in new[] { "pending", "registered", "cancelled", "removed" })
            {
                var log = new List<string>(); var command = new Command("custom.lifecycle", log); var service = Service(command, "lifecycle");
                var first = Revision("first", Plan("one", Order(command, "one", 1)));
                var latest = Revision("latest", Plan("two", Order(command, "one", 2)));
                service.SubmitPlans("lifecycle", first); service.SubmitPlans("lifecycle", latest);
                var proposal = service.FinishProposal("lifecycle", state != "cancelled")!;
                if (state == "registered") service.Approve(proposal);
                if (state == "removed") service.DiscardProposalsExcept(Array.Empty<string>());
                var replacement = Revision("too-late", Plan("three", Order(command, "one", 3)));
                Expect("proposal_closed", () => service.SubmitPlans("lifecycle", replacement));
                if (state is "cancelled" or "removed")
                {
                    service.BeginProposal("lifecycle");
                    Expect("proposal_closed", () => service.SubmitPlans("lifecycle", replacement));
                }
                Require((string?)service.SubmitPlans("lifecycle", first)["status"] == "superseded" &&
                    JToken.DeepEquals(proposal.Submission, latest) && log.Count == (state == "registered" ? 1 : 0),
                    "A finished or cancelled consultation changed or revived superseded work");
            }
        });
        check("Automatic commands execute only the final accepted revision after successful consultation completion", () =>
        {
            var log = new List<string>(); var command = new Command("custom.auto-revision", log) { Approval = false }; var service = Service(command, "auto-revision");
            service.SubmitPlans("auto-revision", Revision("first", Plan("one", Order(command, "one", 1))));
            var latest = Revision("latest", Plan("one", Order(command, "two", 2)));
            service.SubmitPlans("auto-revision", latest);
            Require(log.Count == 0, "An automatic revision executed during consultation");
            service.FinishProposal("auto-revision", true); service.FinishProposal("auto-revision", true); service.SubmitPlans("auto-revision", latest);
            Require(log.SequenceEqual(new[] { "custom.auto-revision:two:2" }), "Automatic completion executed an old or duplicate revision");
        });
        check("Revision receipt limits reject atomically and accepted retries remain usable without consuming more receipts", () =>
        {
            var log = new List<string>(); var command = new Command("custom.limit", log); var service = Service(command, "limit-revisions");
            JObject? first = null, latest = null; var rejected = false;
            for (var revision = 0; revision < 50; revision++)
            {
                var request = Revision("revision-" + revision, Plan("one", Enumerable.Range(0, 40).Select(i => Order(command, "one", i)).ToArray()));
                try { service.SubmitPlans("limit-revisions", request); first ??= request; latest = request; }
                catch (AdvisorOperationException error) when (error.Code == "operation_receipt_limit") { rejected = true; break; }
            }
            Require(rejected && first != null && latest != null && log.Count == 0 &&
                JToken.DeepEquals(service.Proposal("limit-revisions")!.Submission, latest), "Receipt exhaustion changed or executed the last valid revision");
            Require((string?)service.SubmitPlans("limit-revisions", first!)["status"] == "superseded" &&
                (string?)service.SubmitPlans("limit-revisions", latest!)["status"] == "proposed", "Receipt exhaustion blocked identical accepted retries");
            service.FinishProposal("limit-revisions", true); service.Approve(service.Proposal("limit-revisions")!);
            Require(log.Count == 40, "Receipt exhaustion replayed previous revisions or prevented adoption of the valid one");
        });
    }
    private static JObject WithAction(JObject request)
    { var value = (JObject)request.DeepClone(); value["action"] = "submit"; return value; }
}

internal static partial class ReservationCompositionTests
{
    private static void RunRevisions(Action<string, Action> check)
    {
        check("A one-waypoint plan can be replaced by seven points and only the final reservation policy and route are adopted", () =>
        {
            var game = new Game(); var service = Service(game, "route-revision");
            var first = Batch(Plan(new[] { Policy(42, "replace") }, Move(42, 45.579233))); first["request_id"] = "hiei-searchlegs-a";
            first["plans"]![0]!["operations"]![0]!["arguments"]!["waypoints"]![0]!["longitude"] = 45.4;
            service.SubmitPlans("route-revision", first);
            var latitudes = new[] { 45.63468, 45.69683, 45.75433, 45.81993, 45.87981, 45.94688, 46.03265307345113 };
            var latest = Batch(Plan(new[] { Policy(42, "append") }, Move(42, latitudes))); latest["request_id"] = "hiei-searchlegs-a2";
            foreach (var point in (JArray)latest["plans"]![0]!["operations"]![0]!["arguments"]!["waypoints"]!) point["longitude"] = 145.95787210501481;
            service.SubmitPlans("route-revision", latest);
            Require(game.Log.Count == 0 && game.Queues[42].SequenceEqual(new[] { "old" }), "Submitting a revision cleared or changed native reservations");
            Require((string?)service.SubmitPlans("route-revision", first)["status"] == "superseded", "Replaying the one-point plan rolled back the route");
            service.Approve(service.FinishProposal("route-revision", true)!);
            Require(game.Movements.Count == 7 && game.Movements.Select(w => w.Position.Latitude).SequenceEqual(latitudes) &&
                game.Movements.All(w => w.Position.Longitude == 145.95787210501481) && game.Queues[42].Count == 8 &&
                !game.Log.Any(entry => entry.StartsWith("clear:", StringComparison.Ordinal)), "Adoption used old coordinates, waypoints or replacement policy");
        });
    }
}
