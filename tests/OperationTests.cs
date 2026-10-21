using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OperationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("WIP unit orders default off, round-trip, and survive saves from every other category", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-operation-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
                Require(!store.Current.UnitOperationsEnabled && !AdvisorOptions.Parse("{}").UnitOperationsEnabled && !File.Exists(store.Path), "Orders started enabled or reading persisted a default");
                store.SaveWip(true); store.EnsureUserName("Commander");
                store.SaveGeneral(AdvisorTextSize.Large, true, "Captain", false, true);
                store.SaveInformation(AdvisorInformation.Default); store.SaveConsultation(3, 1, "brief"); store.SaveDeveloper(true, () => { });
                var reopened = new AdvisorOptionsStore(store.Path);
                Require(reopened.Current.UnitOperationsEnabled && reopened.Current.DeveloperApiEnabled && reopened.Current.CommonPrompt == "brief" && !reopened.Current.LinkOwnUnits, "Another category lost the WIP choice");
                reopened.SaveWip(false);
                Require(!new AdvisorOptionsStore(store.Path).Current.UnitOperationsEnabled && reopened.Current.DeveloperApiEnabled && reopened.Current.UserName == "Captain", "Disabling changed unrelated settings");
                foreach (var value in new JToken[] { new JValue(1), new JValue("true"), JValue.CreateNull() })
                {
                    try { _ = AdvisorOptions.Parse(new JObject { ["wip_unit_operations_enabled"] = value }.ToString()); }
                    catch (AdvisorOptionsException error) when (error.Code == "options_invalid") { continue; }
                    throw new InvalidOperationException("Malformed WIP permission was accepted");
                }
                Directory.Delete(directory, true); File.WriteAllText(directory, "blocked parent");
                try { reopened.SaveWip(true); }
                catch (AdvisorOptionsException error) when (error.Code == "options_write_failed")
                { Require(!reopened.Current.UnitOperationsEnabled, "Failed WIP save changed runtime permission"); return; }
                throw new InvalidOperationException("WIP save should have failed");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); else if (File.Exists(directory)) File.Delete(directory); }
        });
        check("Operation permission gates discovery and execution and disabling keeps native reservations", () =>
        {
            var enabled = false; var unit = new Unit();
            var operations = Service(() => enabled, unit);
            Require(!((JArray)operations.Catalog()["operations"]!).Any(), "Disabled operations were advertised");
            Expect(() => operations.Candidates(BattleSonobuoyOperation.OperationId), "unit_operations_disabled");
            var request = Request(operations);
            Expect(() => operations.Execute(request), "unit_operations_disabled");
            Require(unit.Calls == 0, "Disabled request reached the native bridge");
            enabled = true; Require(operations.Catalog()["operations"]!.Count() == 1, "Enabled operation was not registered");
            Require((string?)operations.Execute(request)["status"] == "registered" && unit.Calls == 1, "Enabled request failed");
            enabled = false; Expect(() => operations.Execute(Request(operations, "after-disable")), "unit_operations_disabled");
            Require(unit.Queue.Count == 2 && unit.Calls == 1, "Turning WIP off cancelled a native order or added another one");
        });
        check("Repeated operation requests are idempotent, conflicting IDs and old battle scopes are rejected", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit);
            var request = Request(operations); var answer = operations.Execute(request);
            answer["data"]!["count"] = 200;
            Require((int?)operations.Execute((JObject)request.DeepClone())["data"]!["count"] == 1 && unit.Calls == 1, "A retry duplicated an order or mutated the stored receipt");
            var changed = (JObject)request.DeepClone(); changed["arguments"]!["longitude"] = 128;
            Expect(() => operations.Execute(changed), "request_id_conflict");
            var nextBattle = Service(() => true, unit);
            Expect(() => nextBattle.Execute(request), "operation_scope_changed");
            Require(unit.Calls == 1, "An old scope was rebound to the current unit");
        });
        check("Unexpected operation errors stay visible and their unknown side effects are never retried blindly", () =>
        {
            var operations = new AdvisorOperations(() => true); var effect = new FaultyOperation(); operations.Register(effect);
            var request = Request(operations); request["operation"] = effect.Id;
            try { operations.Execute(request); throw new InvalidOperationException("Expected native error"); }
            catch (IOException) { }
            Require(effect.Calls == 1, "Faulty operation was not invoked");
            Expect(() => operations.Execute(request), "operation_outcome_unknown");
            Require(effect.Calls == 1, "Retry duplicated an order after an unexpected error");
            var offThread = Task.Run(() =>
            {
                try { operations.Catalog(); }
                catch (InvalidOperationException) { return true; }
                return false;
            }).GetAwaiter().GetResult();
            Require(offThread, "A background worker accessed game operations directly");
        });
        check("Sonobuoy discovery and registration cannot bypass disabled own-unit or ammunition disclosure", () =>
        {
            var unit = new Unit(); var information = AdvisorInformation.Default;
            var operations = new AdvisorOperations(() => true);
            operations.Register(new BattleSonobuoyOperation(() => new IBattleSonobuoyUnit[] { unit }, () => information));
            foreach (var kind in new[] { InformationKind.BattleUnits, InformationKind.BattleWeapons })
            {
                information = AdvisorInformation.Default.With(kind, false);
                Expect(() => operations.Candidates(BattleSonobuoyOperation.OperationId), "information_disabled");
                Require((string?)operations.Execute(Request(operations, "blocked-" + kind))["code"] == "information_disabled" && unit.Calls == 0,
                    "Operation bypassed excluded data or registered while information was disabled");
            }
            information = AdvisorInformation.Default;
            Require(operations.Candidates(BattleSonobuoyOperation.OperationId)["candidates"]!.Any() &&
                (string?)operations.Execute(Request(operations, "enabled"))["status"] == "registered", "Restoring information did not restore the operation");
        });
        check("Sonobuoy registration appends one native order with an unfamiliar ammunition key and preserves prior orders", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit);
            var candidate = operations.Candidates(BattleSonobuoyOperation.OperationId)["candidates"]![0]!;
            Require((string?)candidate["target_ref"] == "unit:42" && (int?)candidate["ammunition"]![0]!["available_count"] == 2, "Candidate identity or available inventory was lost");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var result = operations.Execute(Request(operations));
                Require((string?)result["status"] == "registered" && unit.Queue.Count == 2 && unit.Queue[0] == "existing waypoint" &&
                    unit.LastAmmo == "custom_mod_sonobuoy" && unit.LastLatitude == 26.5 && unit.LastLongitude == 127.5 && unit.Calls == 1,
                    "The native request replaced a route, changed coordinates or used a fixed weapon name");
            }
            finally { CultureInfo.CurrentCulture = culture; }
        });
        check("Incoming depth choices cannot overwrite live helicopter settings changed after discovery", () =>
        {
            foreach (var requestedDepth in new JToken[] { new JValue("above_layer"), new JValue("below_layer"),
                new JValue("alternating"), new JValue("unknown"), new JValue(2), new JValue(true),
                JValue.CreateNull(), new JObject(), new JArray("above_layer", "below_layer") })
            {
                var unit = new Unit { Depth = "above_layer" }; var other = new Unit(43) { Depth = "below_layer" };
                var operations = new AdvisorOperations(() => true);
                operations.Register(new BattleSonobuoyOperation(() => new IBattleSonobuoyUnit[] { unit, other }));
                var definition = operations.Catalog()["operations"]![0]!["parameters"]!;
                Require(definition["properties"]!["depth"] == null, "The first release advertised depth control");
                var observed = operations.Candidates(BattleSonobuoyOperation.OperationId)["candidates"]![0]!;
                Require((string?)observed["depth"] == "above_layer", "The current setting was not exposed");
                unit.Depth = "alternating";
                var request = Request(operations); request["arguments"]!["depth"] = requestedDepth;
                var result = operations.Execute(request);
                var current = operations.Candidates(BattleSonobuoyOperation.OperationId)["candidates"]![0]!;
                Require((string?)result["status"] == "registered" && (string?)result["data"]!["depth"] == "alternating" &&
                    unit.Depth == "alternating" && (string?)current["depth"] == "alternating" && unit.Queue.Count == 2,
                    "A supplied or stale depth overwrote the player's live setting, or the reservation was lost");
                Require(other.Depth == "below_layer" && other.Queue.Count == 1, "Another helicopter was changed");
            }
        });
        check("Omitted depth preserves the current setting and replay does not overwrite later user changes", () =>
        {
            var unit = new Unit { Depth = "below_layer" }; var operations = Service(() => true, unit);
            var request = Request(operations);
            var result = operations.Execute(request);
            Require((string?)result["data"]!["depth"] == "below_layer" && unit.Depth == "below_layer", "An omitted depth reset the setting");
            unit.Depth = "above_layer";
            var replay = operations.Execute((JObject)request.DeepClone());
            Require(JToken.DeepEquals(result, replay) && unit.Depth == "above_layer" && unit.Calls == 1 && unit.Queue.Count == 2,
                "A receipt replay registered another order or overwrote a later user choice");
        });
        check("Sonobuoy requests recheck the current unit, flight state, available inventory and single-drop support", () =>
        {
            var unit = new Unit(); var current = new List<IBattleSonobuoyUnit> { unit };
            var operations = new AdvisorOperations(() => true); operations.Register(new BattleSonobuoyOperation(() => current));
            _ = operations.Candidates(BattleSonobuoyOperation.OperationId);
            unit.UnavailableReason = "helicopter_not_airborne";
            Require((string?)operations.Execute(Request(operations, "landed"))["code"] == "helicopter_not_airborne", "Landed candidate was still registered");
            unit.UnavailableReason = "following_formation";
            Require((string?)operations.Execute(Request(operations, "formation"))["code"] == "following_formation", "Follower's control mode was silently changed");
            unit.UnavailableReason = null; unit.Available = 0;
            Require((string?)operations.Execute(Request(operations, "no-ammo"))["code"] == "no_available_sonobuoy", "A previously reserved round was reused");
            unit.Available = 2; unit.SingleDrop = false;
            Require((string?)operations.Execute(Request(operations, "multi-drop"))["code"] == "single_drop_unavailable", "A batch-release launcher was treated as one round");
            current.Clear();
            Require((string?)operations.Execute(Request(operations, "gone"))["code"] == "target_unavailable" && unit.Calls == 0, "A stale or foreign object was still operated");
        });
        check("Invalid sonobuoy coordinates, counts, placement and ammunition never reach the native bridge", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit); var index = 0;
            foreach (var pair in new (string Key, JToken Value)[]
            {
                ("latitude", 91), ("longitude", -181), ("latitude", "26.5"), ("longitude", double.NaN),
                ("latitude", JValue.CreateNull()), ("count", 2), ("count", "1"), ("placement", "replace"),
                ("ammunition", "other-weapon"), ("unknown", true)
            })
            {
                var request = Request(operations, "invalid-" + index++);
                request["arguments"]!["depth"] = "below_layer"; request["arguments"]![pair.Key] = pair.Value;
                Require((string?)operations.Execute(request)["status"] == "rejected", "Invalid arguments were accepted");
            }
            Require(unit.Calls == 0 && unit.Queue.Count == 1 && unit.Depth == "alternating",
                "An invalid request changed native orders or the helicopter depth setting");
        });
        check("Local operation endpoints need neither an AI connection nor a conversation window and recheck saved permission when pumped", () =>
        {
            var enabled = true; var unit = new Unit(); var operations = Service(() => enabled, unit);
            var api = new AdvisorLocalApi(() => throw new InvalidOperationException("Prompt access"), e => throw e,
                () => throw new InvalidOperationException("Connection access"), (_, _) => throw new InvalidOperationException("AI preparation"));
            var owner = new object();
            api.Attach(owner, "tactical_battle", () => throw new InvalidOperationException("Conversation access"), operations); api.Enable();
            var candidates = Send(api, "GET", "/v1/operations/" + BattleSonobuoyOperation.OperationId + "/candidates");
            Require(candidates.Status == 200 && (string?)candidates.Data["scope_id"] == operations.ScopeId, "Candidates lost battle scope");
            var request = Request(operations);
            var pending = api.Enqueue("POST", "/v1/operations", request); enabled = false; api.Pump();
            Require(pending.GetAwaiter().GetResult().Status == 403 && unit.Calls == 0, "A queued request bypassed the saved WIP switch");
            enabled = true;
            Require(Send(api, "POST", "/v1/operations", request).Status == 200 && unit.Calls == 1, "Offline registration failed");
            Require(Send(api, "POST", "/v1/operations", request).Status == 200 && unit.Calls == 1, "HTTP retry duplicated the reservation");
            Require(Send(api, "GET", "/v1/operations/candidates").Status == 404, "Malformed path was dispatched");
            api.Detach(owner);
            Require(Send(api, "POST", "/v1/operations", Request(operations, "detached")).Status == 409 && unit.Calls == 1, "Detached battle still accepted orders");
            api.Disable();
        });
    }
    private static AdvisorOperations Service(Func<bool> enabled, Unit unit)
    {
        var operations = new AdvisorOperations(enabled); operations.Register(new BattleSonobuoyOperation(() => new IBattleSonobuoyUnit[] { unit })); return operations;
    }
    private static JObject Request(AdvisorOperations operations, string id = "request-1") => new()
    {
        ["scope_id"] = operations.ScopeId, ["request_id"] = id, ["operation"] = BattleSonobuoyOperation.OperationId, ["target_ref"] = "unit:42",
        ["arguments"] = new JObject { ["latitude"] = 26.5, ["longitude"] = 127.5, ["ammunition"] = "custom_mod_sonobuoy", ["count"] = 1, ["placement"] = "append" }
    };
    private static AdvisorLocalResponse Send(AdvisorLocalApi api, string method, string path, JObject? body = null)
    { var task = api.Enqueue(method, path, body ?? new()); api.Pump(); return task.GetAwaiter().GetResult(); }
    private static void Expect(Action action, string code)
    {
        try { action(); }
        catch (AdvisorOperationException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected operation rejection: " + code);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Unit : IBattleSonobuoyUnit
    {
        public int Id { get; }
        internal Unit(int id = 42) => Id = id;
        public string Name => "Test helicopter";
        public string? UnavailableReason { get; set; }
        public string Depth { get; set; } = "alternating";
        public JObject Position => new() { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 };
        internal int Calls, Available = 2;
        internal bool SingleDrop = true;
        internal string? LastAmmo;
        internal double LastLatitude, LastLongitude;
        internal readonly List<string> Queue = new() { "existing waypoint" };
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("custom_mod_sonobuoy", "Custom buoy", 3, Available, SingleDrop) };
        public void Append(string ammunition, double latitude, double longitude)
        { Calls++; LastAmmo = ammunition; LastLatitude = latitude; LastLongitude = longitude; Queue.Add("one buoy reservation"); }
    }
    private sealed class FaultyOperation : IAdvisorOperation
    {
        public bool RequiresApproval => false;
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => "Custom operation";
        public string Description => "Test failure";
        internal int Calls;
        public string Id => "extension.test";
        public JObject Definition() => new() { ["id"] = Id };
        public JArray Candidates() => new();
        public JObject Execute(string target, JObject arguments) { Calls++; throw new IOException("Unexpected bridge failure"); }
    }
}
