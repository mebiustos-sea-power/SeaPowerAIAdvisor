using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class CombatOperationTests
{
    private static void RunRangeTests(Action<string, Action> check)
    {
        check("Range envelopes include both boundaries and reject invalid native values instead of reporting no option", () =>
        {
            foreach (var (distance, inRange) in new[] { (0.9, false), (1.0, true), (10.0, true), (20.0, true), (20.1, false) })
                Require(new AttackRangeInfo("unit:7022", "missile", distance, 1, 20, "sensor").InRange == inRange,
                    "Minimum or maximum range was ignored");
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1.0 })
            {
                try { _ = new AttackRangeInfo("unit:7022", "missile", 10, 1, invalid, "sensor"); throw new InvalidOperationException("Invalid native range accepted"); }
                catch (ArgumentOutOfRangeException) { }
            }
        });
        check("Contact-scoped attack candidates return several contacts and the operation definition in one read", () =>
        {
            var game = new Game(); var service = Service(game);
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("range-details", AdvisorPrompts.Current, new AdvisorOperationReferences(new AdvisorUnitReferences(new[] {
                ("own_unit_1", 42), ("observed_contact_1", 7022), ("observed_contact_2", 7023) })))!;
            var request = new JObject { ["action"] = "candidates", ["operation_id"] = AttackExtension.OperationId,
                ["target_ref"] = "own_unit_1", ["contact_refs"] = new JArray("observed_contact_1", "unit:observed_contact_2") };
            var original = request.DeepClone();
            var result = RangeRead(gateway, session, request);
            var rows = (JArray)result["candidates"]!;
            var ranges = (JArray)rows.Single()["ammunition"]![0]!["in_range_options"]!;
            Require(game.RangeCalls == 1 && game.RangeShooter == "unit:42" &&
                game.RangeContacts.SequenceEqual(new[] { "unit:7022", "unit:7023" }) &&
                ranges.Select(r => (string)r["target_ref"]!).SequenceEqual(game.RangeContacts) &&
                ranges.All(r => (double?)r["maximum_range_nm"] == 20 && (string?)r["limiting_factor"] == "sensor") &&
                (string?)result["operation"]?["id"] == AttackExtension.OperationId &&
                result["contact_ref_resolutions"]!.Count() == 2 && JToken.DeepEquals(request, original) && game.Log.Count == 0,
                "Batched detail lookup added a second read, confused actor/contact filters, lost its definition or executed work");
        });
        check("Invalid or unsupported contact filters cannot broaden candidate lookup or bypass information selection", () =>
        {
            var game = new Game(); using var gateway = new AdvisorOperationGateway(Service(game));
            using var session = gateway.CreateSession("range-validation", AdvisorPrompts.Current)!;
            foreach (var contacts in new[] { new JArray(), new JArray("unit:7022", "unit:7022"), new JArray(5), new JArray("observed_contact_99"), new JArray(Enumerable.Range(0, 11).Select(i => "unit:" + (7000 + i))) })
            {
                var result = RangeRead(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = AttackExtension.OperationId,
                    ["contact_refs"] = contacts });
                Require((string?)result["status"] == "error" && game.RangeCalls == 0, "Invalid contacts reached or broadened the native range query");
            }
            var unsupported = RangeRead(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = CancelAttackExtension.OperationId,
                ["contact_refs"] = new JArray("unit:7022") });
            Require((string?)unsupported["error"]?["code"] == "candidate_contacts_unsupported", "An unsupported filter was silently ignored");
            game.Weapons = false;
            var denied = RangeRead(gateway, session, new JObject { ["action"] = "candidates", ["operation_id"] = AttackExtension.OperationId,
                ["contact_refs"] = new JArray("unit:7022") });
            Require((string?)denied["error"]?["code"] == "information_disabled" && game.RangeCalls == 0 && game.Log.Count == 0,
                "Range details bypassed disclosure or issued an attack");
        });
        check("No current in-range options does not prevent approval of an attack requiring automatic approach", () =>
        {
            var game = new Game { NoInRangeOptions = true }; var service = Service(game);
            Require((int?)service.CandidatePage(AttackExtension.OperationId, "unit:42", 0, 10, new[] { "unit:7022" })["total_count"] == 0,
                "The fixture unexpectedly has an in-range option");
            OperationSubmissionFixture.Submit(service, "test", Attack(42, "append", ("unit:7022", "missile", 2)));
            var proposal = service.FinishProposal("test", true)!; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.Single() == "attack:unit:42:Append:7022/missile/2",
                "Range information became a new execution restriction");
        });
    }
    private static JObject RangeRead(AdvisorOperationGateway gateway, IAdvisorOperationSession session, JObject request)
    {
        var work = session.ExecuteAsync(request, CancellationToken.None);
        gateway.Pump();
        return work.GetAwaiter().GetResult();
    }
}
