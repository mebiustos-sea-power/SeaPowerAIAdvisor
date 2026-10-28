using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class SensorOperationTests
{
    internal static void Run(Action<string, Action> check)
    {
        SensorUiControlTests.Run(check);
        SensorConditionTests.Run(check);
        check("Sensor candidates expose the target's live control options without executing, while EMCON does not require enumeration by the AI", () =>
        {
            var game = new Game(); var service = Service(game);
            var sensors = service.Candidates(SensorExtension.OperationId)["candidates"]!;
            var emcon = service.Candidates(EmconExtension.OperationId)["candidates"]!;
            Require((string?)sensors[0]!["controls"]![0]!["id"] == "radar.power" &&
                (string?)sensors[0]!["controls"]![1]!["setting"] == "active" &&
                (string?)sensors[0]!["controls"]![2]!["display_name"] == (string?)sensors[0]!["controls"]![0]!["display_name"] &&
                (string?)sensors[0]!["controls"]![2]!["id"] != (string?)sensors[0]!["controls"]![0]!["id"] &&
                (string?)sensors[0]!["controls"]![2]!["function"] == "targeting" && emcon[0]!["controls"] == null && game.Log.Count == 0, "Discovery changed state or lost the distinct control choices");
            game.Removed = true;
            Require(service.Candidates(SensorExtension.OperationId)["candidates"]![0]!["controls"]!.Count() == 0,
                "Candidates reused a stale sensor inventory");
            game.Information = false;
            Expect("information_disabled", () => service.Candidates(SensorExtension.OperationId));
            Expect("information_disabled", () => service.Candidates(EmconExtension.OperationId));
        });
        check("Approved sensor and EMCON requests retain their target and order, leave other units alone and cannot execute twice", () =>
        {
            var game = new Game(); var service = Service(game);
            OperationSubmissionFixture.Submit(service, "test", Order(SensorExtension.OperationId, 42,
                Changes(("radar.power", "on"), ("sonar.active", "off"))), Order(EmconExtension.OperationId, 43, new JObject()));
            var proposal = service.FinishProposal("test", true)!;
            foreach (var language in new[] { "en", "ja" })
            {
                var description = service.DescribeProposal(proposal, language);
                Require(description.Contains("Unit 42", StringComparison.Ordinal) && description.Contains("Radar", StringComparison.Ordinal) &&
                    description.Contains("Unit 43", StringComparison.Ordinal) && game.Log.Count == 0, "Review omitted the actual targets or executed work");
            }
            service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[]
                { "unit:42:radar.power:on", "unit:42:sonar.active:off", "emcon:unit:43" }), "Execution was reordered, duplicated or applied to another unit");
        });
        check("Sensor choices and target availability are checked again at approval before any operation runs", () =>
        {
            foreach (var unavailable in new[] { "removed", "option", "unit", "sensor" })
            {
                var game = new Game(); var service = Service(game);
                OperationSubmissionFixture.Submit(service, "test", Order(EmconExtension.OperationId, 43, new JObject()),
                    Order(SensorExtension.OperationId, 42, Changes(("radar.power", "on"))));
                var proposal = service.FinishProposal("test", true)!;
                game.Removed = unavailable == "removed"; game.AllowOn = unavailable != "option";
                game.UnitReason = unavailable == "unit" ? "target_not_controllable" : null;
                game.SensorReason = unavailable == "sensor" ? "sensor_inoperable" : null;
                service.Approve(proposal);
                Require(game.Log.Count == 0 && proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode != null,
                    "A stale sensor proposal partly ran or hid an approval rejection");
            }
        });
        check("Invalid control IDs, unsupported settings, duplicate changes and invented EMCON arguments cannot replace an accepted plan", () =>
        {
            var game = new Game(); var service = Service(game);
            var accepted = OperationSubmissionFixture.Batch(Order(SensorExtension.OperationId, 42, Changes(("radar.power", "off"))));
            service.SubmitPlans("test", accepted); var proposal = service.Proposal("test")!;
            var invalid = new[] {
                (SensorExtension.OperationId, Changes(("invented", "on")), "sensor_control_unavailable"),
                (SensorExtension.OperationId, Changes(("radar.power", "below_layer")), "sensor_setting_unavailable"),
                (SensorExtension.OperationId, Changes(("radar.power", "on"), ("radar.power", "off")), "invalid_sensor_arguments"),
                (EmconExtension.OperationId, new JObject { ["enabled"] = false }, "invalid_emcon_arguments") };
            for (var i = 0; i < invalid.Length; i++)
            {
                var (operation, args, code) = invalid[i]; var request = OperationSubmissionFixture.Batch(Order(operation, 42, args));
                request["request_id"] = "invalid-" + i; Expect(code, () => service.SubmitPlans("test", request));
                Require(JToken.DeepEquals(proposal.Submission, accepted) && game.Log.Count == 0,
                    "A rejected revision replaced the accepted work or changed sensors");
            }
        });
        check("Real sensor commands compose with movement and run after reservation registration", () =>
        {
            var game = new Game(); var service = Service(game, true);
            var move = Order(MovementExtension.OperationId, 42, new JObject { ["waypoints"] = new JArray(new JObject { ["latitude"] = 26, ["longitude"] = 127 }) });
            var batch = OperationSubmissionFixture.Batch(move, Order(SensorExtension.OperationId, 42, Changes(("radar.power", "on"))),
                Order(EmconExtension.OperationId, 43, new JObject()));
            var alternative = (JObject)batch["plans"]![0]!.DeepClone(); alternative["plan_id"] = "two";
            ((JArray)batch["plans"]!).Add(alternative);
            service.SubmitPlans("test", batch); var proposal = service.FinishProposal("test", true)!;
            service.Approve(proposal, new[] { "one" });
            Require(proposal.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[]
                { "begin:unit:42", "move:unit:42", "unit:42:radar.power:on", "emcon:unit:43" }),
                "Sensors forced a single alternative, ran before queue changes or mutated a different target");
        });
        check("Public sensor support retains the existing battle lifetime and game-thread protections", () =>
        {
            var game = new Game(); var context = new AdvisorOperationContext(game, new Map(),
                new AdvisorOperationText(new SensorExtension().GetDefinition().Localizations));
            var support = (IAdvisorSensorSupport)context.Game;
            var reads = game.Reads;
            var wrongThread = Task.Run(() =>
            {
                try { support.GetSensorUnits(); return false; } catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Require(wrongThread && game.Reads == reads, "Worker-thread sensor access reached the native adapter");
            context.Dispose();
            Throws<ObjectDisposedException>(() => support.GetSensorUnits());
            Throws<ObjectDisposedException>(() => support.ApplyEmcon("unit:42"));
            Throws<ObjectDisposedException>(() => support.SetSensorControls("unit:42", new[] { new SensorControlChange("radar.power", "on") }));
            Require(game.Reads == reads && game.Log.Count == 0, "Retired sensor support read or changed a battle");
        });
    }
    private static AdvisorOperations Service(Game game, bool movement = false)
    {
        var catalog = new AdvisorOperationCatalog(); catalog.Register(new SensorExtension()); catalog.Register(new EmconExtension());
        if (movement) catalog.Register(new MovementExtension());
        var service = new AdvisorOperations(_ => true);
        foreach (var operation in catalog.CreateOperations(game, new Map())) service.Register(operation);
        service.BeginProposal("test"); return service;
    }
    private static JObject Order(string operation, int target, JObject args) => new()
    { ["operation"] = operation, ["target_ref"] = "unit:" + target, ["arguments"] = args };
    private static JObject Changes(params (string Id, string Value)[] changes) => new()
    { ["changes"] = new JArray(changes.Select(c => new JObject { ["control_id"] = c.Id, ["value"] = c.Value })) };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Expect(string code, Action action)
    { try { action(); } catch (AdvisorOperationException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private sealed class Game : IAdvisorGameSupport, IAdvisorSensorSupport, IAdvisorMovementSupport, IAdvisorReservationSequenceSupport
    {
        internal bool Information = true, Removed, AllowOn = true;
        internal string? UnitReason, SensorReason;
        internal int Reads;
        internal readonly List<string> Log = new();
        public bool IsInformationEnabled(string category) => Information;
        public IReadOnlyList<SensorUnitInfo> GetSensorUnits()
        {
            Reads++;
            return new[] { 42, 43 }.Select(id => new SensorUnitInfo(new GameUnitInfo(id, "Unit " + id, null, false, false, UnitReason), false,
                Removed ? Array.Empty<SensorControlInfo>() : new[] {
                    new SensorControlInfo("radar.power", "Radar", "radar", "power", "off", new[] { new SensorControlOption("on", AllowOn ? null : "sensor_setting_unavailable"), new SensorControlOption("off") }, SensorReason),
                    new SensorControlInfo("sonar.active", "Sonar", "sonar", "active", "on", new[] { new SensorControlOption("on"), new SensorControlOption("off") }, SensorReason),
                    new SensorControlInfo("guidance.power", "Radar", "radar", "power", "off", new[] { new SensorControlOption("on"), new SensorControlOption("off") }, role: "air", function: "targeting") })).ToArray();
        }
        public void SetSensorControls(string target, IReadOnlyList<SensorControlChange> changes)
        { foreach (var change in changes) Log.Add(target + ":" + change.Control + ":" + change.Value); }
        public void ApplyEmcon(string target) => Log.Add("emcon:" + target);
        public IReadOnlyList<GameUnitInfo> GetUnits() => GetSensorUnits().Select(u => u.Unit).ToArray();
        public IReadOnlyList<MovementUnitInfo> GetMovementUnits() => GetUnits().Select(u => new MovementUnitInfo(u, "vessel", null, null, true, false,
            Array.Empty<MovementPreset>(), Array.Empty<MovementPreset>(), Array.Empty<MovementPreset>())).ToArray();
        public ReservationQueueState InspectReservations(string target, ReservationPlacement placement) => new(new AdvisorMapCoordinate(25, 127), null, false);
        public void SetReservationLoop(string target, IReadOnlyList<AdvisorMapCoordinate> points) => throw new NotSupportedException();
        public bool BeginReservations(string target, ReservationPlacement placement) { Log.Add("begin:" + target); return false; }
        public void AppendMovement(string target, MovementWaypoint waypoint) => Log.Add("move:" + target);
        public void AppendFormationReturn(string target) => throw new InvalidOperationException("unused");
        public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target) => throw new InvalidOperationException("unused");
        public ReservationRoute? GetReservationRoute(string target) => throw new InvalidOperationException("unused");
        public string GetSonobuoyDepth(string target) => throw new InvalidOperationException("unused");
        public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position) => throw new InvalidOperationException("unused");
    }
    private sealed class Map : IAdvisorMap
    {
        public void Center(AdvisorMapCoordinate position) => throw new InvalidOperationException("Sensor commands must not move the map");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings) => throw new InvalidOperationException("Sensor commands need no drawings");
    }
}
