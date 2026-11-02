using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class ReservationCompositionTests
{
    private static void RunArrivalRules(Action<string, Action> check)
    {
        check("Arrival ROE is reviewed before approval and remains attached to each requested waypoint when reservations are registered", () =>
        {
            foreach (var mode in new[] { RulesOfEngagement.Free, RulesOfEngagement.Tight, RulesOfEngagement.Hold })
            {
                var game = new Game(); var service = Service(game, "arrival");
                var move = Move(42, 1, 2); var other = Move(43, 8);
                move["arguments"]!["waypoints"]![0]!["rules_of_engagement_on_arrival"] = mode.ToString().ToLowerInvariant();
                service.SubmitPlans("arrival", Batch(Plan(new[] { Policy(42, "append"), Policy(43, "append") }, move, other)));
                var proposal = service.FinishProposal("arrival", true)!;
                foreach (var language in new[] { "en", "ja" })
                {
                    var review = OperationSubmissionFixture.ReviewText(service.DescribeProposal(proposal, language));
                    Require(review.Contains("Weapons " + mode, StringComparison.Ordinal) &&
                        review.Contains(language == "ja" ? "到着時" : "on arrival", StringComparison.Ordinal) &&
                        review.Contains(language == "ja" ? "編隊設定" : "formation-control", StringComparison.Ordinal),
                        "Review omitted arrival timing, the requested policy or formation scope");
                    var text = new AdvisorOperationText(new MovementExtension().GetDefinition().Localizations).For(language);
                    var heading = AdvisorText.Load(language)["operation.notes_heading"];
                    Require(review.Split('\n').Count(line => line == heading) == 1 &&
                        review.Contains("• " + text.Format("proposal_arrival_rules_scope", "Hobby 1"), StringComparison.Ordinal) &&
                        review.IndexOf(heading, StringComparison.Ordinal) > review.LastIndexOf("1. ", StringComparison.Ordinal),
                        "Review repeated arrival notes, lost their actor or placed them before all action details");
                }
                Require(game.Log.Count == 0 && game.Movements.Count == 0, "Submission or review issued an order before approval");
                service.Approve(proposal); service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Registered && game.Movements.Count == 3 &&
                    game.Movements[0].RulesOfEngagementOnArrival == mode &&
                    game.Movements[1].RulesOfEngagementOnArrival == null && game.Movements[2].RulesOfEngagementOnArrival == null &&
                    game.Log.SequenceEqual(new[] { "move:42:1", "move:42:2", "move:43:8" }) &&
                    game.Queues[42][0] == "old" && game.Queues[43][0] == "old",
                    "Registration lost an arrival policy, spread it to an omitted point or another unit, cleared an append queue or replayed");
            }
        });
        check("Invalid arrival ROE on a later waypoint rejects the whole plan before replacing any existing reservation", () =>
        {
            foreach (var value in new JToken[] { new JValue("unknown"), new JValue("Free"), new JValue(""),
                JValue.CreateNull(), new JValue(1), new JValue(true), new JArray("free"), new JObject() })
            {
                var game = new Game(); var service = Service(game, "invalid-arrival");
                var move = Move(42, 1, 2);
                move["arguments"]!["waypoints"]![0]!["rules_of_engagement_on_arrival"] = "free";
                move["arguments"]!["waypoints"]![1]!["rules_of_engagement_on_arrival"] = value.DeepClone();
                Expect("invalid_movement_arguments", () => service.SubmitPlans("invalid-arrival",
                    Batch(Plan(new[] { Policy(42, "replace") }, move))));
                Require(game.Log.Count == 0 && game.Movements.Count == 0 && game.Queues[42].Single() == "old",
                    "A malformed later waypoint changed the game or cleared existing reservations");
            }
        });
    }
}
