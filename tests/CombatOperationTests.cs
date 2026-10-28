using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class CombatOperationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Attack discovery keeps actor/observed target identities and per-ammunition compatibility without issuing attacks", () =>
        {
            var game = new Game(); var s = Service(game);
            var row = s.Candidates(AttackExtension.OperationId)["candidates"]![0]!;
            Require((string?)row["target_ref"] == "unit:42" && (string?)row["targets"]![0]!["target_ref"] == "unit:7022" &&
                (int?)row["ammunition"]![0]!["available_count"] == 6 && game.Log.Count == 0, "Discovery changed state or mixed target and actor identities");
            game.Information = false;
            Expect("information_disabled", () => s.Candidates(AttackExtension.OperationId));
            Expect("information_disabled", () => s.Candidates(CancelAttackExtension.OperationId));
        });
        check("An approved multi-target attack applies one switch for its actor, preserves other actors and cannot replay", () =>
        {
            var game = new Game(); var s = Service(game);
            OperationSubmissionFixture.Submit(s, "test", Attack(42, "replace", ("unit:7022", "missile", 3), ("unit:7023", "missile", 2)),
                Attack(43, "append", ("unit:7023", "missile", 1)));
            var p = s.FinishProposal("test", true)!;
            foreach (var lang in new[] { "en", "ja" })
            {
                var text = s.DescribeProposal(p, lang);
                Require(text.Contains("Unit 42", StringComparison.Ordinal) && text.Contains("Contact 7023", StringComparison.Ordinal) && game.Log.Count == 0,
                    "Review omitted the actors/targets or executed work");
            }
            s.Approve(p); s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] { "attack:unit:42:Replace:7022/missile/3,7023/missile/2", "attack:unit:43:Append:7023/missile/1" }),
                "An attack batch switched repeatedly, changed another actor or replayed");
        });
        check("Aggregate attack ammunition respects an initial switch and cannot discard preceding new attacks", () =>
        {
            var game = new Game(); var s = Service(game);
            Expect("insufficient_attack_ammunition", () => OperationSubmissionFixture.Submit(s, "test",
                Attack(42, "append", ("unit:7022", "missile", 4)), Attack(42, "append", ("unit:7023", "missile", 4))));
            Expect("attack_switch_after_new_attacks", () => OperationSubmissionFixture.Submit(s, "test",
                Attack(42, "append", ("unit:7022", "missile", 1)), Attack(42, "replace", ("unit:7023", "missile", 1))));
            OperationSubmissionFixture.Submit(s, "test", Attack(42, "replace", ("unit:7022", "missile", 4)), Attack(42, "append", ("unit:7023", "missile", 4)));
            var p = s.FinishProposal("test", true)!; s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.Count == 2, "An initial switch did not release old commitments, or later attacks were lost");
        });
        check("Attack approval revalidates contact, ammunition compatibility, inventory, control and permissions before mutation", () =>
        {
            foreach (var fault in new[] { "target", "ammo", "compatibility", "quantity", "control", "permission" })
            {
                var game = new Game(); var s = Service(game);
                OperationSubmissionFixture.Submit(s, "test", CancelAttack(43), Attack(42, "append", ("unit:7022", "missile", 2)));
                var p = s.FinishProposal("test", true)!;
                game.Fault = fault; s.Approve(p);
                Require(p.State == AdvisorProposalState.Pending && p.ErrorCode != null && game.Log.Count == 0,
                    "Stale attack data permitted partial execution: " + fault);
            }
        });
        check("Attack shape, shot counts and unknown target IDs are rejected without replacing an accepted proposal", () =>
        {
            var game = new Game(); var s = Service(game);
            OperationSubmissionFixture.Submit(s, "test", Attack(42, "append", ("unit:7022", "missile", 1)));
            var p = s.Proposal("test")!; var original = p.Submission!.DeepClone();
            foreach (var request in new[] { Attack(42, "guess", ("unit:7022", "missile", 1)),
                Attack(42, "append", ("unit:7022", "missile", 0)), Attack(42, "append", ("unit:9999", "missile", 1)) })
            {
                var batch = OperationSubmissionFixture.Batch(request); batch["request_id"] = Guid.NewGuid().ToString("N");
                try { s.SubmitPlans("test", batch); throw new InvalidOperationException("Invalid attack accepted"); } catch (AdvisorOperationException) { }
                Require(JToken.DeepEquals(original, p.Submission) && game.Log.Count == 0, "Rejected input changed accepted work");
            }
        });
        check("Attack cancellation needs no weapon/contact disclosure, affects only the specified actor and rejects invented arguments", () =>
        {
            var game = new Game { Weapons = false, Contacts = false }; var s = Service(game);
            OperationSubmissionFixture.Submit(s, "test", CancelAttack(43)); var p = s.FinishProposal("test", true)!;
            s.Approve(p); s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] { "cancel-attack:unit:43" }), "Attack cancellation required unrelated data or propagated");
            var context = new AdvisorOperationContext(game, new Map(), new AdvisorOperationText(new CancelAttackExtension().GetDefinition().Localizations));
            var handler = new CancelAttackOperation(context);
            try { handler.Validate(new[] { new OperationOrder("unit:43", "{\"formation\":true}") }); throw new InvalidOperationException("Unexpected attack-cancellation scope accepted"); }
            catch (OperationException e) when (e.Code == "invalid_cancel_attack_arguments") { }
        });
        check("Attack target and ammunition aliases normalize only through consultation and carried identities", () =>
        {
            var game = new Game(); var s = Service(game);
            var catalog = new AdvisorOperationCatalog(); catalog.Register(new AttackExtension());
            var command = catalog.CreateOperations(game, new Map()).Single();
            var args = (JObject)Attack(42, "append", ("unit:observed_contact_1", "weapon_reference_9", 1))["arguments"]!;
            var resolutions = new JArray();
            var canonical = command.NormalizeArguments("unit:42", args,
                new AdvisorOperationReferences(new AdvisorUnitReferences(new[] { ("observed_contact_1", 7022) }),
                    new AdvisorWeaponReferences(new[] { ("weapon_reference_9", "missile", "Test missile") })), resolutions);
            Require((string?)canonical["attacks"]![0]!["target_ref"] == "unit:7022" &&
                (string?)canonical["attacks"]![0]!["ammunition"] == "missile" && resolutions.Count == 2 &&
                (string?)args["attacks"]![0]!["target_ref"] == "unit:observed_contact_1" && game.Log.Count == 0,
                "Normalization guessed an identity, rewrote input or issued an attack");
        });
        check("Actual movement and attack handlers compose with reservation registration before an immediate attack", () =>
        {
            var game = new Game(); var s = Service(game, true);
            var move = new JObject { ["operation"] = MovementExtension.OperationId, ["target_ref"] = "unit:42",
                ["arguments"] = new JObject { ["waypoints"] = new JArray(new JObject { ["latitude"] = 26, ["longitude"] = 127 }) } };
            OperationSubmissionFixture.Submit(s, "test", Attack(42, "replace", ("unit:7022", "missile", 2)), move);
            var p = s.FinishProposal("test", true)!; s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] { "begin:unit:42", "move:unit:42", "attack:unit:42:Replace:7022/missile/2" }),
                "Attack executed before movement registration or broke command composition");
        });
        check("Cancellation commands have independent approval, support multiple actors and do not replay", () =>
        {
            var game = new Game { Weapons = false, Contacts = false }; var s = Service(game);
            var candidate = s.Candidates(CancelReservationsExtension.OperationId)["candidates"]![0]!;
            Require((string?)candidate["target_ref"] == "unit:42" && game.Log.Count == 0, "Cancellation discovery executed or required weapon data");
            OperationSubmissionFixture.Submit(s, "test", CancelAttack(42), CancelReservations(43), CancelReservations(42));
            var p = s.FinishProposal("test", true)!;
            foreach (var lang in new[] { "en", "ja" })
                Require(s.DescribeProposal(p, lang).Contains("Unit 43", StringComparison.Ordinal) && game.Log.Count == 0,
                    "Review omitted an actor or cancelled before approval");
            s.Approve(p); s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] {
                "cancel-attack:unit:42", "cancel-reservations:unit:43", "cancel-reservations:unit:42" }),
                "Cancellation crossed service/actor boundaries, changed order or replayed");
        });
        check("Reservation cancellation rejects invented scope and revalidates all actors before any mutation", () =>
        {
            var game = new Game(); var s = Service(game);
            OperationSubmissionFixture.Submit(s, "test", CancelAttack(43), CancelReservations(42));
            var p = s.FinishProposal("test", true)!; game.Fault = "control"; s.Approve(p);
            Require(p.State == AdvisorProposalState.Pending && game.Log.Count == 0, "Stale cancellation changed an earlier actor");
            var context = new AdvisorOperationContext(game, new Map(), new AdvisorOperationText(new CancelReservationsExtension().GetDefinition().Localizations));
            using var handler = new CancelReservationsOperation(context);
            try { handler.Validate(new[] { new OperationOrder("unit:43", "{\"attacks\":true}") }); throw new InvalidOperationException("Invented cancellation accepted"); }
            catch (OperationException e) when (e.Code == "invalid_cancel_reservations_arguments") { }
        });
        check("Reservation cancellation during plan adoption runs after reservation registration", () =>
        {
            var game = new Game(); var s = Service(game, true);
            var move = new JObject { ["operation"] = MovementExtension.OperationId, ["target_ref"] = "unit:42",
                ["arguments"] = new JObject { ["waypoints"] = new JArray(new JObject { ["latitude"] = 26, ["longitude"] = 127 }) } };
            OperationSubmissionFixture.Submit(s, "test", CancelReservations(42), move);
            var p = s.FinishProposal("test", true)!; s.Approve(p);
            Require(p.State == AdvisorProposalState.Registered && game.Log.SequenceEqual(new[] {
                "begin:unit:42", "move:unit:42", "cancel-reservations:unit:42" }), "Cancellation bypassed shared phase order");
        });
        check("Combat support retains host thread and lifetime guards", () =>
        {
            var game = new Game(); var context = new AdvisorOperationContext(game, new Map(), new AdvisorOperationText(new AttackExtension().GetDefinition().Localizations));
            var support = (IAdvisorCombatSupport)context.Game;
            Require(Task.Run(() => { try { support.GetAttackUnits(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(), "Combat accessed on a worker thread");
            var reservations = (IAdvisorReservationSupport)context.Game;
            Require(Task.Run(() => { try { reservations.GetReservationUnits(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(), "Cancellation accessed on a worker thread");
            context.Dispose();
            try { reservations.ClearReservations("unit:42"); throw new InvalidOperationException("Retired reservations reached the game"); } catch (ObjectDisposedException) { }
            try { support.CancelAttack("unit:42"); throw new InvalidOperationException("Retired combat support reached the game"); } catch (ObjectDisposedException) { }
            Require(game.Log.Count == 0, "Retired support changed combat");
        });
        check("Combat partial and unknown outcomes retain receipts and never replay earlier immediate operations", () =>
        {
            foreach (var unknown in new[] { false, true })
            {
                var game = new Game(); var s = Service(game);
                OperationSubmissionFixture.Submit(s, "test", Attack(42, "append", ("unit:7022", "missile", 1)), CancelAttack(43));
                var p = s.FinishProposal("test", true)!; game.ExecutionFailure = unknown ? "unknown" : "rejected";
                try { s.Approve(p); } catch (InvalidOperationException) when (unknown) { }
                var count = game.Log.Count; s.Approve(p);
                Require(p.State == (unknown ? AdvisorProposalState.Unknown : AdvisorProposalState.Partial) && count == game.Log.Count && count == 1,
                    "Failed combat replayed a successful attack or hid its partial outcome");
            }
        });
    }
    private static AdvisorOperations Service(Game game, bool movement = false)
    {
        var catalog = new AdvisorOperationCatalog(); catalog.Register(new AttackExtension()); catalog.Register(new CancelAttackExtension()); catalog.Register(new CancelReservationsExtension());
        if (movement) catalog.Register(new MovementExtension());
        var s = new AdvisorOperations(_ => true); foreach (var op in catalog.CreateOperations(game, new Map())) s.Register(op);
        s.BeginProposal("test"); return s;
    }
    private static JObject Attack(int unit, string mode, params (string Target, string Ammo, int Rounds)[] attacks) => new()
    { ["operation"] = AttackExtension.OperationId, ["target_ref"] = "unit:" + unit, ["arguments"] = new JObject
        { ["mode"] = mode, ["attacks"] = new JArray(attacks.Select(a => new JObject { ["target_ref"] = a.Target, ["ammunition"] = a.Ammo, ["rounds"] = a.Rounds })) } };
    private static JObject CancelReservations(int unit) => new() { ["operation"] = CancelReservationsExtension.OperationId, ["target_ref"] = "unit:" + unit, ["arguments"] = new JObject() };
    private static JObject CancelAttack(int unit) => new() { ["operation"] = CancelAttackExtension.OperationId, ["target_ref"] = "unit:" + unit, ["arguments"] = new JObject() };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    { try { action(); } catch (AdvisorOperationException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private sealed class Game : IAdvisorGameSupport, IAdvisorCombatSupport, IAdvisorMovementSupport, IAdvisorReservationSequenceSupport, IAdvisorReservationSupport
    {
        internal bool Information = true, Weapons = true, Contacts = true;
        internal string Fault = "", ExecutionFailure = "";
        internal readonly List<string> Log = new();
        private readonly Dictionary<string, int> Used = new();
        private readonly HashSet<string> Switched = new();
        public bool IsInformationEnabled(string category) => Information && Fault != "permission" &&
            (category != "battle_weapons" || Weapons) && (category != "battle_contacts" || Contacts);
        public IReadOnlyList<GameUnitInfo> GetCombatUnits() => new[] { 42, 43 }.Select(id => new GameUnitInfo(id, "Unit " + id, null, false, false,
            Fault == "control" && id == 42 ? "target_not_controllable" : null)).ToArray();
        public IReadOnlyList<CombatTargetInfo> GetAttackTargets() => new[] { 7022, 7023 }.Where(id => Fault != "target" || id != 7022)
            .Select(id => new CombatTargetInfo("unit:" + id, "Contact " + id, "surface", "hostile", null, false, false)).ToArray();
        public IReadOnlyList<AttackUnitInfo> GetAttackUnits() => GetCombatUnits().Select(u => new AttackUnitInfo(u,
            Fault == "ammo" ? Array.Empty<AttackAmmunitionInfo>() : new[] { new AttackAmmunitionInfo("missile", "Test missile", 10,
                Fault == "quantity" ? 0 : Math.Max(0, (Switched.Contains(u.Target) ? 10 : 6) - (Used.TryGetValue(u.Target, out var n) ? n : 0)), 10,
                Fault == "compatibility" ? Array.Empty<string>() : new[] { "unit:7022", "unit:7023" }) }, Array.Empty<AttackInstruction>())).ToArray();
        public void IssueAttack(string target, AttackUpdateMode mode, IReadOnlyList<AttackInstruction> attacks)
        {
            if (mode == AttackUpdateMode.Replace) { Used[target] = 0; Switched.Add(target); }
            Used.TryGetValue(target, out var n); Used[target] = n + attacks.Sum(a => a.Rounds);
            Log.Add("attack:" + target + ":" + mode + ":" + string.Join(",", attacks.Select(a => a.Target.Replace("unit:", "") + "/" + a.Ammunition + "/" + a.Rounds)));
        }
        public IReadOnlyList<GameUnitInfo> GetReservationUnits() => GetCombatUnits();
        public void ClearReservations(string target) => Log.Add("cancel-reservations:" + target);
        public void CancelAttack(string target)
        {
            if (ExecutionFailure == "rejected") throw new OperationException(409, "cancel_attack_rejected");
            if (ExecutionFailure == "unknown") throw new InvalidOperationException("Native failure");
            Log.Add("cancel-attack:" + target);
        }
        public IReadOnlyList<GameUnitInfo> GetUnits() => GetCombatUnits();
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
        public void Center(AdvisorMapCoordinate location) => throw new InvalidOperationException("Combat commands must not move the map");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> values) => throw new InvalidOperationException("unused");
    }
}
