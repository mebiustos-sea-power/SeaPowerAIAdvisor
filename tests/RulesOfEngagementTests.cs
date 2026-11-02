using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class CombatOperationTests
{
    private static void RunRulesOfEngagementTests(Action<string, Action> check)
    {
        check("Engagement policy discovery reports current modes without requiring weapons or contacts or changing orders", () =>
        {
            var game = new Game { Weapons = false, Contacts = false }; var service = Service(game);
            var rows = (JArray)service.Candidates(RulesOfEngagementExtension.OperationId)["candidates"]!;
            Require((string?)rows[0]!["target_ref"] == "unit:42" && (string?)rows[0]!["mode"] == "free" &&
                (string?)rows[1]!["mode"] == "tight" && game.Log.Count == 0, "Policy discovery omitted current modes or changed the game");
            game.Information = false;
            Expect("information_disabled", () => service.Candidates(RulesOfEngagementExtension.OperationId));
        });
        check("Each engagement mode is reviewed in both languages, changes only its named unit after approval and cannot replay", () =>
        {
            foreach (var mode in new[] { RulesOfEngagement.Free, RulesOfEngagement.Tight, RulesOfEngagement.Hold })
            {
                var game = new Game { Weapons = false, Contacts = false }; var service = Service(game);
                OperationSubmissionFixture.Submit(service, "test", Engagement(42, mode));
                var proposal = service.FinishProposal("test", true)!;
                foreach (var language in new[] { "en", "ja" })
                {
                    var review = OperationSubmissionFixture.ReviewText(service.DescribeProposal(proposal, language));
                    Require(review.Contains("Unit 42", StringComparison.Ordinal) && review.Contains("Weapons Free", StringComparison.Ordinal) &&
                        review.Contains("Weapons " + mode, StringComparison.Ordinal) && review.Contains('→') && game.Log.Count == 0,
                        "Review omitted the current/requested mode or changed policy before approval");
                    var text = new AdvisorOperationText(new RulesOfEngagementExtension().GetDefinition().Localizations).For(language);
                    Require(review.Contains("• " + text.Format("execution_note", "Unit 42"), StringComparison.Ordinal) == (mode == RulesOfEngagement.Hold) &&
                        review.Split('\n').Count(line => line == AdvisorText.Load(language)["operation.notes_heading"]) == (mode == RulesOfEngagement.Hold ? 1 : 0),
                        "Review omitted the Hold consequence, repeated its heading or added notes to a different requested mode");
                }
                service.Approve(proposal); service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Registered && game.Engagement["unit:42"] == mode &&
                    game.Engagement["unit:43"] == RulesOfEngagement.Tight && game.Log.SequenceEqual(new[] { "roe:unit:42:" + mode }),
                    "Policy change cancelled an attack, changed another unit or replayed");
            }
        });
        check("Engagement policy rejects missing, unknown and invented arguments without changing game state", () =>
        {
            var game = new Game();
            using var context = new AdvisorOperationContext(game, new Map(), new AdvisorOperationText(new RulesOfEngagementExtension().GetDefinition().Localizations));
            using var handler = new RulesOfEngagementOperation(context);
            var arguments = new[] { new JObject(), new JObject { ["mode"] = "unknown" }, new JObject { ["mode"] = "Hold" },
                new JObject { ["mode"] = JValue.CreateNull() }, new JObject { ["mode"] = 2 },
                new JObject { ["mode"] = "hold", ["formation"] = true } };
            foreach (var args in arguments)
            {
                try { handler.Validate(new[] { new OperationOrder("unit:42", args.ToString()) }); throw new InvalidOperationException("Invalid policy accepted"); }
                catch (OperationException error) when (error.Code == "invalid_rules_of_engagement_arguments") { }
            }
            try { handler.Validate(new[] { new OperationOrder("unit:99", "{\"mode\":\"hold\"}") }); throw new InvalidOperationException("Unknown actor accepted"); }
            catch (OperationException error) when (error.Code == "target_unavailable") { }
            Require(game.Log.Count == 0, "Invalid policy input changed game state");
        });
        check("Engagement approval revalidates all units and information permissions before any immediate action", () =>
        {
            foreach (var fault in new[] { "control", "permission" })
            {
                var game = new Game(); var service = Service(game);
                OperationSubmissionFixture.Submit(service, "test", CancelAttack(43), Engagement(42, RulesOfEngagement.Hold));
                var proposal = service.FinishProposal("test", true)!;
                game.Fault = fault; service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode != null && game.Log.Count == 0 &&
                    game.Engagement["unit:42"] == RulesOfEngagement.Free, "Stale policy proposal changed an earlier unit");
            }
        });
        check("Movement, attack cancellation and per-unit engagement policies compose in the shared phase and submission order", () =>
        {
            var game = new Game(); var service = Service(game, true);
            var move = new JObject { ["operation"] = MovementExtension.OperationId, ["target_ref"] = "unit:42",
                ["arguments"] = new JObject { ["waypoints"] = new JArray(new JObject { ["latitude"] = 26, ["longitude"] = 127 }) } };
            OperationSubmissionFixture.Submit(service, "test", CancelAttack(42), Engagement(42, RulesOfEngagement.Hold),
                Engagement(43, RulesOfEngagement.Free), move);
            var proposal = service.FinishProposal("test", true)!; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] {
                "begin:unit:42", "move:unit:42", "cancel-attack:unit:42", "roe:unit:42:Hold", "roe:unit:43:Free" }) &&
                game.Engagement["unit:42"] == RulesOfEngagement.Hold && game.Engagement["unit:43"] == RulesOfEngagement.Free,
                "Policy changes broke reservation-first ordering or mixed unit policies");
        });
        check("Engagement support retains host thread and battle lifetime guards", () =>
        {
            var game = new Game(); var context = new AdvisorOperationContext(game, new Map(), new AdvisorOperationText(new RulesOfEngagementExtension().GetDefinition().Localizations));
            var support = (IAdvisorRulesOfEngagementSupport)context.Game;
            Require(Task.Run(() => { try { support.GetRulesOfEngagementUnits(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(),
                "Policy support accessed on a worker thread");
            context.Dispose();
            try { support.SetRulesOfEngagement("unit:42", RulesOfEngagement.Hold); throw new InvalidOperationException("Retired policy support changed game state"); }
            catch (ObjectDisposedException) { }
            Require(game.Log.Count == 0, "Retired policy support reached the game");
        });
    }
    private static JObject Engagement(int unit, RulesOfEngagement mode) => new()
    { ["operation"] = RulesOfEngagementExtension.OperationId, ["target_ref"] = "unit:" + unit,
        ["arguments"] = new JObject { ["mode"] = mode.ToString().ToLowerInvariant() } };
}
