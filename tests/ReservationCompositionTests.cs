using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static partial class ReservationCompositionTests
{
    internal static void Run(Action<string, Action> check)
    {
        RunRevisions(check);
        check("A mixed multi-unit plan replaces each queue once and connects movement/drop/return segments in execution order", () =>
        {
            var game = new Game(); var service = Service(game, "mixed");
            var batch = Batch(Plan(new[] { Policy(42, "replace"), Policy(43, "append") },
                Move(42, 1), Drop(42, 2), Move(43, 8), Move(42, 3), Drop(42, 4), Return(42)));
            service.SubmitPlans("mixed", batch); var proposal = service.FinishProposal("mixed", true)!;
            var text = service.DescribeProposal(proposal, "ja"); var preview = service.PreviewProposal(proposal, "en");
            Require(game.Log.Count == 0 && text.IndexOf("移動予約", StringComparison.Ordinal) < text.IndexOf("ソノブイ", StringComparison.Ordinal), "Review changed the game or reordered command explanations");
            var frame = AdvisorOperationTargets.Render(new[] { proposal.Plans[0] }, AdvisorText.Load("en"), id => "Unit " + id);
            var rows = frame.Descendants().Where(e => e.Name.LocalName == "WrapPanel").ToArray();
            Require(rows.Length == 2 && rows.All(row => row.Elements().Count(e => e.Name.LocalName == "Border") == 1),
                "Interleaved commands repeated a target's queue policy or merged different units");
            var lines = preview.Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Line).ToArray();
            Require(lines.Select(l => l.Coordinates[0].Latitude).SequenceEqual(new[] { 0d, 1d, 10d, 2d, 3d }) &&
                lines.Select(l => l.Coordinates.Last().Latitude).SequenceEqual(new[] { 1d, 2d, 8d, 3d, 4d }), "Preview segments ignored a preceding command or another target's queue");
            service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] {
                "clear:42", "move:42:1", "drop:42:2", "move:43:8", "move:42:3", "drop:42:4", "return:42" }) &&
                game.Queues[43][0] == "old" && game.Depths[42] == "alternating", "Registration reordered commands, cleared twice, replayed or changed sonobuoy depth");
        });
        check("Separated sonobuoy segments share one inventory budget and adoption rechecks it before clearing any target", () =>
        {
            var game = new Game { Inventory = 3 }; var service = Service(game, "stock");
            var plan = Plan(new[] { Policy(42, "replace") }, Drop(42, 1, 2), Move(42, 3), Drop(42, 4, 5));
            Expect("no_available_sonobuoy", () => service.SubmitPlans("stock", Batch(plan)));
            Require(game.Log.Count == 0 && service.Proposal("stock")!.Orders.Count == 0, "A rejected aggregate shortage changed reservations or retained partial work");
            game.Inventory = 4; service.SubmitPlans("stock", Batch(plan)); var proposal = service.FinishProposal("stock", true)!;
            game.Inventory = 3; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "no_available_sonobuoy" && game.Log.Count == 0, "A stale replacement cleared the old queue");
            game.Inventory = 4; service.Approve(proposal);
            Require(game.Log.Count(l => l == "clear:42") == 1 && game.Log.Count(l => l.StartsWith("drop:", StringComparison.Ordinal)) == 4, "A corrected plan lost segments or cleared earlier newly added drops");
        });
        check("An append plan preserves old reservations while replacement reclaims their sonobuoys only once", () =>
        {
            var game = new Game { Inventory = 4, ReservedOld = 2 }; var service = Service(game, "budget");
            var orders = new[] { Drop(42, 1, 2), Move(42, 3), Drop(42, 4) };
            Expect("no_available_sonobuoy", () => service.SubmitPlans("budget", Batch(Plan(new[] { Policy(42, "append") }, orders))));
            Require(game.Log.Count == 0 && game.Queues[42].Single() == "old", "Append cancelled old reservations to fit its resource demand");
            service.SubmitPlans("budget", Batch(Plan(new[] { Policy(42, "replace") }, orders)));
            service.Approve(service.FinishProposal("budget", true)!);
            Require(game.Log.Count(l => l == "clear:42") == 1 && game.Queues[42].Count == 4, "Replacement cleared a preceding segment");
        });
        check("Queue policy mistakes and operations after a terminal return are rejected without creating usable partial proposals", () =>
        {
            var game = new Game(); var service = Service(game, "invalid");
            var wire = AdvisorOperationPlanBatch.Schema(new JObject { ["type"] = "string" }, "identity");
            var planShape = wire["properties"]!["plans"]!["items"]!;
            Require(((JArray)planShape["required"]!).Values<string>().Contains("reservation_updates"),
                "Published tools made the required target policy optional again");
            Expect("missing_reservation_updates", () => service.SubmitPlans("invalid", Batch(Plan(Array.Empty<JObject>(), Move(42, 1)))));
            Expect("duplicate_reservation_policy", () => service.SubmitPlans("invalid", Batch(Plan(new[] { Policy(42, "append"), Policy(42, "replace") }, Move(42, 1)))));
            Expect("reservation_after_terminal_operation", () => service.SubmitPlans("invalid", Batch(Plan(new[] { Policy(42, "replace") }, Move(42, 1), Return(42), Drop(42, 2)))));
            var nested = Move(42, 1); nested["arguments"]!["placement"] = "replace";
            Expect("invalid_movement_arguments", () => service.SubmitPlans("invalid", Batch(Plan(new[] { Policy(42, "append") }, nested))));
            Require(game.Log.Count == 0 && service.PriorOrders("invalid").Count == 0, "Invalid composition retained executable receipts or changed game state");
        });
        check("Unsupported waypoint settings are rejected and omitted settings remain absent from later registered legs", () =>
        {
            var game = new Game(); var service = Service(game, "settings");
            var bad = Move(42, 1); bad["arguments"]!["waypoints"]![0]!["speed"] = "ahead_flank";
            Expect("movement_setting_not_supported", () => service.SubmitPlans("settings", Batch(Plan(new[] { Policy(42, "append") }, bad))));
            var good = Move(42, 1, 2); good["arguments"]!["waypoints"]![0]!["altitude"] = "preset_1";
            service.SubmitPlans("settings", Batch(Plan(new[] { Policy(42, "append") }, good))); service.Approve(service.FinishProposal("settings", true)!);
            Require(game.Movements.Count == 2 && game.Movements[0].Altitude == "preset_1" && game.Movements[1].Altitude == null &&
                game.Movements[1].Speed == null && game.Movements[1].Depth == null && game.Log.All(l => !l.StartsWith("clear", StringComparison.Ordinal)),
                "A later waypoint stamped captured defaults, changed another setting or cleared an append plan");
        });
        RunPresets(check);
        check("A follower can enter independent movement, drop and queue a return without changing control during review", () =>
        {
            var game = new Game { Following = true }; var service = Service(game, "follower");
            service.SubmitPlans("follower", Batch(Plan(new[] { Policy(42, "append") }, Move(42, 1), Drop(42, 2), Return(42))));
            var proposal = service.FinishProposal("follower", true)!;
            Require(game.Following && service.DescribeProposal(proposal, "en").Contains("independently", StringComparison.Ordinal) && game.Log.Count == 0, "Review switched formation control");
            service.Approve(proposal);
            Require(!game.Following && game.Log.SequenceEqual(new[] { "clear:42", "independent:42", "move:42:1", "drop:42:2", "return:42" }) && game.Returns == 1,
                "A follower was not prepared once or formation return executed before preceding reservations");
        });
        check("A rejection after replacement is an unknown changed outcome and cannot replay or proceed to the next unit", () =>
        {
            var game = new Game { RejectRegistration = true }; var service = Service(game, "failure");
            service.SubmitPlans("failure", Batch(Plan(new[] { Policy(42, "replace"), Policy(43, "append") }, Move(42, 1), Move(43, 2))));
            var proposal = service.FinishProposal("failure", true)!;
            try { service.Approve(proposal); throw new Exception("Changed queue reported as an unchanged rejection"); }
            catch (InvalidOperationException error) when (error.InnerException is AdvisorOperationException) { }
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Unknown && game.Log.SequenceEqual(new[] { "clear:42" }) && game.Queues[43].Single() == "old",
                "Changed work was hidden, replayed or followed by another unit");
        });
        check("Developer composed execution keeps scope, validates selection and never repeats a registered plan", () =>
        {
            var game = new Game(); var service = Service(game, "unused");
            var first = Plan(new[] { Policy(42, "replace") }, Move(42, 1), Drop(42, 2));
            var second = Plan(new[] { Policy(42, "replace") }, Move(42, 3)); second["plan_id"] = "two";
            var request = Batch(first, second);
            request["scope_id"] = "previous-battle";
            Expect("operation_scope_changed", () => service.SubmitAndApprove(request));
            request["scope_id"] = service.ScopeId; request["selected_plan_ids"] = new JArray("missing");
            Expect("invalid_plan_selection", () => service.SubmitAndApprove(request));
            Require(game.Log.Count == 0, "A scope or selection error changed reservations");
            request["selected_plan_ids"] = new JArray("one");
            var result = service.SubmitAndApprove(request); var replay = service.SubmitAndApprove((JObject)request.DeepClone());
            Require((bool?)result["registered"] == true && (bool?)replay["registered"] == true &&
                game.Log.SequenceEqual(new[] { "clear:42", "move:42:1", "drop:42:2" }), "Developer retries repeated or bypassed the composed plan");
            request["selected_plan_ids"] = new JArray("two");
            Expect("request_id_conflict", () => service.SubmitAndApprove(request));
            request["selected_plan_ids"] = new JArray("one");
            service.DiscardProposalsExcept(System.Array.Empty<string>());
            Require((bool?)service.SubmitAndApprove(request)["registered"] == true && game.Log.Count == 3,
                "Removing a history owner forgot a developer result or replayed its mutations");
            request["plans"]![0]!["operations"]![0]!["arguments"]!["waypoints"]![0]!["latitude"] = 5;
            Expect("request_id_conflict", () => service.SubmitAndApprove(request));
        });
        check("Ownership changes on another target prevent every queue mutation at adoption", () =>
        {
            var game = new Game(); var service = Service(game, "owner");
            service.SubmitPlans("owner", Batch(Plan(new[] { Policy(42, "replace"), Policy(43, "append") }, Move(42, 1), Drop(43, 2))));
            var proposal = service.FinishProposal("owner", true)!; game.UnavailableTarget = 43;
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "target_not_controllable" && game.Log.Count == 0,
                "An earlier unit was changed before validating the later target");
        });
        check("Expected SDK queue inspection and preparation errors keep their rejection code and never change reservations", () =>
        {
            foreach (var code in new[] { "target_unavailable", "information_disabled" })
            {
                var game = new Game { InspectFailure = new OperationException(409, code) }; var service = Service(game, "boundary");
                using var gateway = new AdvisorOperationGateway(service);
                using var session = gateway.CreateSession("boundary", AdvisorPrompts.Current)!;
                var request = Batch(Plan(new[] { Policy(42, "replace") }, Move(42, 1), Drop(42, 2)));
                request["action"] = "submit";
                var rejected = OperationGatewayTests.Finish(gateway, session.ExecuteAsync(request, System.Threading.CancellationToken.None));
                Require((string?)rejected["error"]?["code"] == code && service.Proposal("boundary")!.Orders.Count == 0 &&
                    game.Log.Count == 0, "SDK queue inspection faulted the consultation, lost its code or retained partial work");
                game.InspectFailure = null;
                var accepted = OperationGatewayTests.Finish(gateway, session.ExecuteAsync(request, System.Threading.CancellationToken.None));
                Require((string?)accepted["status"] == "proposed", "A corrected live target could not be submitted");
                var proposal = service.FinishProposal("boundary", true)!;
                game.InspectFailure = new OperationException(409, code); service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == code && game.Log.Count == 0,
                    "Approval-time inspection bypassed ordinary live revalidation");
                game.InspectFailure = null; game.BeginFailure = new OperationException(409, code); service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Rejected && proposal.ErrorCode == code && game.Log.Count == 0 &&
                    game.Queues[42].Single() == "old", "A queue-preparation rejection became an unknown fault or cleared reservations");
            }
        });
        check("Unexpected SDK queue inspection errors remain visible instead of becoming expected rejections", () =>
        {
            var game = new Game { InspectFailure = new System.IO.IOException("native inspection failure") };
            var service = Service(game, "fault");
            try { service.SubmitPlans("fault", Batch(Plan(new[] { Policy(42, "append") }, Move(42, 1)))); throw new Exception("Inspection fault hidden"); }
            catch (System.IO.IOException) { }
            Require(game.Log.Count == 0 && service.Proposal("fault")!.Orders.Count == 0, "A failed inspection left executable work");
        });
    }
    private static AdvisorOperations Service(Game game, string owner)
    {
        var catalog = new AdvisorOperationCatalog(); catalog.Register(new SonobuoyDropExtension()); catalog.Register(new MovementExtension()); catalog.Register(new FormationReturnExtension());
        var service = new AdvisorOperations(_ => true); foreach (var op in catalog.CreateOperations(game, new Map())) service.Register(op);
        service.BeginProposal(owner); return service;
    }
    private static JObject Batch(params JObject[] plans) => new() { ["request_id"] = "composed", ["plans"] = new JArray(plans) };
    private static JObject Plan(IEnumerable<JObject> updates, params JObject[] orders) => new() {
        ["plan_id"] = "one", ["title"] = "Combined plan", ["summary"] = "Move, drop and return", ["reservation_updates"] = new JArray(updates), ["operations"] = new JArray(orders) };
    private static JObject Policy(int target, string placement) => new() { ["target_ref"] = "unit:" + target, ["placement"] = placement };
    private static JObject Order(string id, int target, JObject args) => new() { ["operation_id"] = id, ["target_ref"] = "unit:" + target, ["arguments"] = args };
    private static JObject Move(int target, params double[] latitudes) => Order(MovementExtension.OperationId, target, new JObject { ["waypoints"] = new JArray(latitudes.Select(lat => new JObject { ["latitude"] = lat, ["longitude"] = 127 })) });
    private static JObject Drop(int target, params double[] latitudes) => Order(SonobuoyDropExtension.OperationId, target, new JObject { ["drops"] = new JArray(latitudes.Select(lat => new JObject { ["latitude"] = lat, ["longitude"] = 127, ["ammunition"] = "buoy" })) });
    private static JObject Return(int target) => Order(FormationReturnExtension.OperationId, target, new JObject());
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action) { try { action(); } catch (AdvisorOperationException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private sealed class Map : IAdvisorMap
    {
        public void Center(AdvisorMapCoordinate p) => throw new Exception("Review must not move the map");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> d) => throw new Exception("Host owns drawing");
    }
    private sealed class Game : IAdvisorGameSupport, IAdvisorReservationSupport, IAdvisorReservationSequenceSupport, IAdvisorMovementSupport
    {
        internal int Inventory = 12, ReservedOld, Returns, UnavailableTarget;
        internal bool Following, RejectRegistration;
        internal Exception? InspectFailure, BeginFailure;
        internal readonly List<string> Log = new();
        internal readonly Dictionary<int, List<string>> Queues = new() { [42] = new() { "old" }, [43] = new() { "old" } };
        internal readonly Dictionary<int, string> Depths = new() { [42] = "alternating", [43] = "alternating" };
        internal readonly List<MovementWaypoint> Movements = new();
        internal IReadOnlyList<MovementUnitInfo>? MovementUnits;
        private static int Id(string target) => int.Parse(target.Substring(5), CultureInfo.InvariantCulture);
        public bool IsInformationEnabled(string category) => true;
        public IReadOnlyList<GameUnitInfo> GetUnits() => new[] { 42, 43 }.Select(id => new GameUnitInfo(id, "Hobby " + (id - 41), new AdvisorMapCoordinate(0, 127), true, true,
            UnavailableTarget == id ? "target_not_controllable" : Following && id == 42 ? "following_formation" : null)).ToArray();
        public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target)
        { var id = Id(target); var used = Queues[id].Count(q => q == "drop"); return new[] { new AmmunitionInfo("buoy", "AN/SSQ", "Bomb", "Sonobuoy", Inventory, Inventory - ReservedOld - used, true, Inventory) }; }
        public ReservationRoute? GetReservationRoute(string target) => new(new AdvisorMapCoordinate(0, 127), new AdvisorMapCoordinate(10, 127));
        public string GetSonobuoyDepth(string target) => Depths[Id(target)];
        public void ClearReservations(string target) { var id = Id(target); Queues[id].Clear(); ReservedOld = 0; Log.Add("clear:" + id); }
        public ReservationQueueState InspectReservations(string target, ReservationPlacement placement)
        {
            if (InspectFailure != null) throw InspectFailure;
            return new ReservationQueueState(new AdvisorMapCoordinate(0, 127), new AdvisorMapCoordinate(10, 127),
                Following && Id(target) == 42, UnavailableTarget == Id(target) ? "target_not_controllable" : null);
        }
        public bool BeginReservations(string target, ReservationPlacement placement)
        {
            if (BeginFailure != null) throw BeginFailure;
            var independent = Following && Id(target) == 42;
            if (placement == ReservationPlacement.Replace || independent) ClearReservations(target);
            if (independent) { Following = false; Log.Add("independent:42"); }
            return placement == ReservationPlacement.Replace || independent;
        }
        public IReadOnlyList<MovementUnitInfo> GetMovementUnits() => MovementUnits ?? GetUnits().Select(unit => new MovementUnitInfo(
            new GameUnitInfo(unit.Id, unit.Name, unit.Position, true, true, UnavailableTarget == unit.Id ? "target_not_controllable" : null),
            "helicopter", "unit:44", "California", false, Following && unit.Id == 42, Array.Empty<MovementPreset>(), new[] {
                new MovementPreset("preset_0", "150 ft", 150, "ft"), new MovementPreset("preset_1", "1000 ft", 1000, "ft") }, Array.Empty<MovementPreset>())).ToArray();
        public void AppendMovement(string target, MovementWaypoint point)
        { Check(); var id = Id(target); Movements.Add(point); Queues[id].Add("move"); Log.Add("move:" + id + ":" + point.Position.Latitude.ToString(CultureInfo.InvariantCulture)); }
        public void AppendSonobuoyDrop(string target, string ammo, AdvisorMapCoordinate position)
        { Check(); if (GetAmmunition(target)[0].Available < 1) throw new OperationException(409, "no_available_sonobuoy"); var id = Id(target); Queues[id].Add("drop"); Log.Add("drop:" + id + ":" + position.Latitude.ToString(CultureInfo.InvariantCulture)); }
        public void AppendFormationReturn(string target) { Check(); var id = Id(target); Queues[id].Add("return"); Returns++; Log.Add("return:" + id); }
        private void Check() { if (RejectRegistration) throw new OperationException(409, "reservation_rejected"); }
    }
}
