using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class ReservationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Developer reservation inspection retains order, ammunition and detached values", () =>
        {
            var unit = new Unit(42); var service = Service(new AdvisorOperations(() => true), unit);
            var view = service.Read(42);
            Require((int?)view["unit_id"] == 42 && (int?)view["count"] == 2 && (string?)view["target_ref"] == "unit:42", "Inspection selected the wrong unit");
            Require(JToken.DeepEquals(view["reservations"], unit.Rows), "Inspection lost reservation order or details");
            view["reservations"]![0]!["count"] = 99;
            Require((int?)service.Read(42)["reservations"]![0]!["count"] == 1 && unit.Clears == 0, "Reading mutated the native queue");
        });
        check("Reservation reset removes all selected-unit orders and preserves other units and inventory", () =>
        {
            var first = new Unit(42); var other = new Unit(43); var ops = new AdvisorOperations(() => true);
            var service = Service(ops, first, other); var result = service.Clear(42, ops.ScopeId);
            Require((int?)result["removed_count"] == 2 && (int?)result["count"] == 0 && !((JArray)result["reservations"]!).Any(), "Reset did not report the actual queue change");
            Require(first.Clears == 1 && first.Inventory == 12 && first.Depth == "alternating" && other.Clears == 0 && other.Rows.Count == 2, "Reset changed unrelated game state");
            Require((int?)service.Clear(42, ops.ScopeId)["removed_count"] == 0, "An empty queue was not handled");
            Expect(() => service.Clear(43, "old-battle"), "operation_scope_changed");
            Expect(() => service.Read(999), "target_unavailable");
            other.UnavailableReason = "target_not_controllable";
            Expect(() => service.Clear(43, ops.ScopeId), "target_not_controllable");
            Require(other.Clears == 0, "Rejected reset changed another unit");
        });
        check("Developer reservation reset runs on the game thread and rejects busy, malformed and stale requests", () =>
        {
            var enabled = true; var ops = new AdvisorOperations(() => enabled); var unit = new Unit(42);
            var target = new Target(); var owner = new object(); var service = Service(ops, unit);
            var api = Api(e => throw e); api.Attach(owner, "tactical_battle", () => target, ops, service); api.Enable();
            var body = new JObject { ["scope_id"] = ops.ScopeId };
            var request = api.Enqueue("POST", "/v1/units/42/reservations/clear", body); body["scope_id"] = "mutated";
            Require(!request.IsCompleted && unit.Clears == 0, "HTTP enqueue executed a game mutation");
            api.Pump(); Require(request.GetAwaiter().GetResult().Status == 200 && unit.Thread == Environment.CurrentManagedThreadId, "Reset ran outside the pumping thread or aliased its body");
            target.Busy = true;
            Require(Send(api, "POST", "/v1/units/42/reservations/clear", Scope(ops)).Status == 409, "Busy adviser allowed destructive reset");
            target.Busy = false; target.Editing = true;
            Require(Send(api, "POST", "/v1/units/42/reservations/clear", Scope(ops)).Status == 409, "Open settings allowed reset");
            target.Editing = false;
            foreach (var invalid in new[] { new JObject(), new JObject { ["scope_id"] = true }, new JObject { ["scope_id"] = ops.ScopeId, ["extra"] = true } })
                Require(Send(api, "POST", "/v1/units/42/reservations/clear", invalid).Status == 400, "Malformed reset was accepted");
            Require(Send(api, "GET", "/v1/units/-1/reservations").Status == 400 && Send(api, "GET", "/v1/units/999/reservations").Status == 409, "Invalid or missing unit was accepted");
            var queued = api.Enqueue("POST", "/v1/units/42/reservations/clear", Scope(ops)); enabled = false; api.Pump();
            Require(queued.GetAwaiter().GetResult().Status == 403 && unit.Clears == 1, "Queued reset bypassed current command permission");
            enabled = true;
            var old = api.Enqueue("POST", "/v1/units/42/reservations/clear", Scope(ops));
            var fresh = new AdvisorOperations(() => true); api.Attach(new object(), "tactical_battle", () => target, fresh, Service(fresh, unit)); api.Pump();
            Require(old.GetAwaiter().GetResult().Status == 409 && unit.Clears == 1, "Old battle request cleared a new battle queue");
            Require(Send(api, "GET", "/v1/units/42/reservations").Status == 200, "Live queue inspection failed");
            api.Disable();
        });
        check("Unavailable and failed native resets are visible and never reported as success", () =>
        {
            var ops = new AdvisorOperations(() => true); var unit = new Unit(42) { FailClear = true }; var service = Service(ops, unit);
            Exception? observed = null; var api = Api(e => observed = e); var owner = new object();
            api.Attach(owner, "tactical_battle", () => new Target(), ops, service); api.Enable();
            Require(Send(api, "POST", "/v1/units/42/reservations/clear", Scope(ops)).Status == 500 && observed is InvalidOperationException,
                "A native failure was hidden or returned success");
            api.Detach(owner);
            Require(Send(api, "GET", "/v1/units/42/reservations").Status == 409, "Detached scene exposed old reservations");
            api.Disable();
            Require(Send(api, "POST", "/v1/units/42/reservations/clear", Scope(ops)).Status == 503 && unit.Clears == 1, "Disabled local API executed reset");
        });
    }
    private static AdvisorUnitReservations Service(AdvisorOperations ops, params Unit[] units) => new(ops, () => units);
    private static AdvisorLocalApi Api(Action<Exception> unexpected) => new(() => throw new InvalidOperationException("Prompt access"), unexpected,
        () => throw new InvalidOperationException("Connection access"), (_, _, _, _) => throw new InvalidOperationException("AI access"));
    private static JObject Scope(AdvisorOperations ops) => new() { ["scope_id"] = ops.ScopeId };
    private static AdvisorLocalResponse Send(AdvisorLocalApi api, string method, string path, JObject? body = null)
    { var task = api.Enqueue(method, path, body ?? new()); api.Pump(); return task.GetAwaiter().GetResult(); }
    private static void Expect(Action action, string code)
    { try { action(); } catch (AdvisorOperationException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Target : IAdvisorLocalTarget
    {
        internal bool Busy, Editing;
        public JObject LocalStatus() => new() { ["busy"] = Busy, ["settings_open"] = Editing };
        public AdvisorLocalRun LocalConsult(string? question, string? shortcut, bool fallback, Func<AdvisorSituationDelivery?, AdvisorBackendSetup> prepare, bool plain, bool? allowOperations, AdvisorSituationDelivery? situationDelivery) => throw new InvalidOperationException("Unexpected consultation");
        public void LocalClear() => throw new InvalidOperationException("Unexpected history clear");
        public void LocalCancel() => throw new InvalidOperationException("Unexpected cancellation");
    }
    private sealed class Unit : IAdvisorReservationUnit
    {
        public int Id { get; }
        internal Unit(int id) => Id = id;
        public string Name => "Test unit";
        public string? UnavailableReason { get; set; }
        internal int Clears, Thread, Inventory = 12;
        internal string Depth = "alternating";
        internal bool FailClear;
        internal readonly JArray Rows = new(new JObject { ["sequence"] = 1, ["kind"] = "sonobuoy_drop", ["ammunition"] = "mod_buoy", ["count"] = 1,
            ["position"] = new JObject { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 } },
            new JObject { ["sequence"] = 2, ["kind"] = "waypoint", ["position"] = new JObject { ["latitude_deg"] = 26.6, ["longitude_deg"] = 127.6 } });
        public JArray Reservations() => Rows;
        public void ClearReservations() { Clears++; Thread = Environment.CurrentManagedThreadId; if (!FailClear) Rows.Clear(); }
    }
}
