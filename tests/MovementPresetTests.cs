using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class ReservationCompositionTests
{
    private static MovementUnitInfo PresetUnit(int id, string kind, MovementPreset[] speeds, MovementPreset[] altitudes, MovementPreset[] depths) =>
        new(new GameUnitInfo(id, "Unit " + id, new AdvisorMapCoordinate(0, 127), kind == "aircraft", true, null),
            kind, null, null, false, false, speeds, altitudes, depths);
    private static void RunPresets(Action<string, Action> check)
    {
        check("Movement candidates preserve per-unit preset values and units across different aircraft", () =>
        {
            var game = new Game { MovementUnits = new[] {
                PresetUnit(42, "aircraft", new[] { new MovementPreset("cruise", "Cruise", 220, "kt") },
                    new[] { new MovementPreset("preset_0", "1000 ft", 1000, "ft") }, Array.Empty<MovementPreset>()),
                PresetUnit(43, "aircraft", new[] { new MovementPreset("cruise", "Cruise", 450, "kt") },
                    new[] { new MovementPreset("preset_0", "20000 ft", 20000, "ft") }, Array.Empty<MovementPreset>()) } };
            var candidates = Service(game, "values").Candidates(MovementExtension.OperationId)["candidates"]!;
            for (var i = 0; i < 2; i++)
            {
                var unit = game.MovementUnits[i]; var row = candidates[i]!;
                Require((string?)row["target_ref"] == unit.Unit.Target &&
                    (string?)row["speed_options"]![0]!["id"] == unit.Speeds[0].Id &&
                    (string?)row["speed_options"]![0]!["name"] == unit.Speeds[0].Name &&
                    (double?)row["speed_options"]![0]!["value"] == unit.Speeds[0].Value &&
                    (string?)row["speed_options"]![0]!["unit"] == "kt" &&
                    (double?)row["altitude_options"]![0]!["value"] == unit.Altitudes[0].Value &&
                    (string?)row["altitude_options"]![0]!["unit"] == "ft" && row["depth_options"]!.Count() == 0,
                    "Candidate transport lost preset identity, units or a unit-specific target value");
            }
            var service = Service(game, "review-values"); var first = Move(42, 1); var second = Move(43, 2);
            first["arguments"]!["waypoints"]![0]!["altitude"] = "preset_0";
            second["arguments"]!["waypoints"]![0]!["altitude"] = "preset_0";
            service.SubmitPlans("review-values", Batch(Plan(new[] { Policy(42, "append"), Policy(43, "append") }, first, second)));
            var review = service.DescribeProposal(service.FinishProposal("review-values", true)!, "en");
            Require(review.Contains("1000 ft", StringComparison.Ordinal) && review.Contains("20000 ft", StringComparison.Ordinal) &&
                !review.Contains("preset_0", StringComparison.Ordinal) && game.Log.Count == 0,
                "Review exposed an internal altitude ID, reused another unit's altitude or issued an order before approval");
        });
        check("Movement rejects numerical or another target's presets and rechecks supported IDs before changing queues", () =>
        {
            var game = new Game(); var own = game.GetMovementUnits()[0];
            game.MovementUnits = new[] { own, PresetUnit(43, "aircraft", Array.Empty<MovementPreset>(),
                new[] { new MovementPreset("preset_9", "30000 ft", 30000, "ft") }, Array.Empty<MovementPreset>()) };
            var service = Service(game, "presets");
            var bad = Move(42, 1); bad["arguments"]!["waypoints"]![0]!["altitude"] = "preset_9";
            Expect("movement_setting_not_supported", () => service.SubmitPlans("presets", Batch(Plan(new[] { Policy(42, "replace") }, bad))));
            bad["arguments"]!["waypoints"]![0]!["altitude"] = 750;
            Expect("invalid_movement_arguments", () => service.SubmitPlans("presets", Batch(Plan(new[] { Policy(42, "replace") }, bad))));
            bad["arguments"]!["waypoints"]![0]!["altitude"] = "preset_1";
            service.SubmitPlans("presets", Batch(Plan(new[] { Policy(42, "replace") }, bad)));
            var proposal = service.FinishProposal("presets", true)!;
            game.MovementUnits = new[] { PresetUnit(42, "aircraft", Array.Empty<MovementPreset>(),
                new[] { new MovementPreset("preset_0", "150 ft", 150, "ft") }, Array.Empty<MovementPreset>()) };
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "movement_setting_not_supported" &&
                game.Log.Count == 0 && game.Queues[42].Single() == "old", "A stale preset cleared or changed the native reservation queue");
        });
        check("Layer-relative depth remains a preset when its current reference value changes before adoption", () =>
        {
            var game = new Game();
            MovementUnitInfo Unit(double feet) => PresetUnit(42, "submarine", new[] { new MovementPreset("ahead_standard", "Ahead standard", 12, "kt") },
                Array.Empty<MovementPreset>(), new[] { new MovementPreset("below_layer", "Below thermocline", feet, "ft") });
            game.MovementUnits = new[] { Unit(500) }; var service = Service(game, "layer");
            var candidate = service.Candidates(MovementExtension.OperationId)["candidates"]![0]!;
            Require((string?)candidate["depth_options"]![0]!["id"] == "below_layer" &&
                (double?)candidate["depth_options"]![0]!["value"] == 500 && (string?)candidate["depth_options"]![0]!["unit"] == "ft",
                "A relative-depth candidate lost its current reference or units");
            var move = Move(42, 1); var point = move["arguments"]!["waypoints"]![0]!;
            point["depth"] = "below_layer"; point["speed"] = "ahead_standard";
            service.SubmitPlans("layer", Batch(Plan(new[] { Policy(42, "append") }, move)));
            var proposal = service.FinishProposal("layer", true)!;
            game.MovementUnits = new[] { Unit(800) }; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Movements.Single().Depth == "below_layer" &&
                game.Movements[0].Speed == "ahead_standard" && game.Movements[0].Altitude == null && game.Queues[42][0] == "old",
                "Adoption froze the old numeric reference, rejected a still-supported relative preset or cleared an append queue");
        });
    }
}
