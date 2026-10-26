using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class OperationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Per-command settings default on, discard the legacy switch and persist only disabled IDs across category saves", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-operation-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
                Require(store.Current.IsCommandEnabled("custom.new") && AdvisorOptions.Parse("{}").DisabledCommands.Count == 0 && !File.Exists(store.Path),
                    "New commands were disabled or reading saved a default");
                var supplied = new[] { "custom.off" }; var immutable = new AdvisorOptions(disabledCommands: supplied); supplied[0] = "custom.other";
                Require(!immutable.IsCommandEnabled("custom.off") && immutable.IsCommandEnabled("custom.other") && immutable.IsCommandEnabled("CUSTOM.OFF"),
                    "The settings retained mutable caller state or changed case-sensitive command identity");
                var number = 0;
                foreach (var old in new JToken[] { new JValue(false), new JValue(true), new JValue("unused"), JValue.CreateNull() })
                {
                    var path = Path.Combine(directory, "legacy-" + number++ + ".json");
                    var original = new JObject { ["wip_unit_operations_enabled"] = old }.ToString(); File.WriteAllText(path, original);
                    var legacy = new AdvisorOptionsStore(path);
                    Require(legacy.Current.IsCommandEnabled("custom.new") && File.ReadAllText(path) == original,
                        "The obsolete switch affected commands or loading rewrote the file");
                    legacy.SaveCommands(new[] { "custom.off" });
                    var saved = JObject.Parse(File.ReadAllText(path));
                    Require(saved["wip_unit_operations_enabled"] == null && !new AdvisorOptionsStore(path).Current.IsCommandEnabled("custom.off") &&
                        new AdvisorOptionsStore(path).Current.IsCommandEnabled("custom.new"), "Saving retained the obsolete field or lost individual settings");
                }
                store.SaveCommands(new[] { "custom.uninstalled", "custom.off" }); store.EnsureUserName("Commander");
                store.SaveGeneral(AdvisorTextSize.Large, "Captain", false, true); store.SaveCampaign(true);
                store.SaveInformation(AdvisorInformation.Default); store.SaveConsultation(3, 1, "brief"); store.SaveDeveloper(true);
                var reopened = new AdvisorOptionsStore(store.Path);
                Require(reopened.Current.DisabledCommands.SequenceEqual(new[] { "custom.off", "custom.uninstalled" }) &&
                    reopened.Current.IsCommandEnabled("custom.new") && reopened.Current.DeveloperApiEnabled && reopened.Current.CommonPrompt == "brief" && !reopened.Current.LinkOwnUnits,
                    "Another category lost disabled or temporarily uninstalled command IDs");
                foreach (var value in new JToken[] { new JValue(1), new JValue("true"), JValue.CreateNull(), new JArray(""), new JArray("bad id"), new JArray("same", "same"), new JArray(7) })
                {
                    try { _ = AdvisorOptions.Parse(new JObject { ["disabled_commands"] = value }.ToString()); }
                    catch (AdvisorOptionsException error) when (error.Code == "options_invalid") { continue; }
                    throw new InvalidOperationException("Malformed disabled command IDs were accepted");
                }
                Directory.Delete(directory, true); File.WriteAllText(directory, "blocked parent");
                try { reopened.SaveCommands(Array.Empty<string>()); }
                catch (AdvisorOptionsException error) when (error.Code == "options_write_failed")
                { Require(!reopened.Current.IsCommandEnabled("custom.off"), "Failed save enabled a command at runtime"); return; }
                throw new InvalidOperationException("Command save should have failed");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); else if (File.Exists(directory)) File.Delete(directory); }
        });
        check("Operation permission gates discovery and execution and disabling keeps native reservations", () =>
        {
            var enabled = false; var unit = new Unit();
            var operations = Service(() => enabled, unit);
            Require(!((JArray)operations.Catalog()["operations"]!).Any(), "Disabled operations were advertised");
            Expect(() => operations.Candidates(SonobuoyDropExtension.OperationId), "unit_operations_disabled");
            var request = Request(operations);
            Expect(() => OperationSubmissionFixture.Adopt(operations, request), "unit_operations_disabled");
            Require(unit.Calls == 0, "Disabled request reached the native bridge");
            enabled = true; Require(operations.Catalog()["operations"]!.Count() == 1, "Enabled operation was not registered");
            Require((string?)OperationSubmissionFixture.Adopt(operations, request)["status"] == "registered" && unit.Calls == 1, "Enabled request failed");
            enabled = false; Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "after-disable")), "unit_operations_disabled");
            Require(unit.Queue.Count == 2 && unit.Calls == 1, "Turning commands off cancelled a native order or added another one");
        });
        check("Repeated operation requests are idempotent, conflicting IDs and old battle scopes are rejected", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit);
            var request = Request(operations); var answer = OperationSubmissionFixture.Adopt(operations, request);
            OperationSubmissionFixture.Data(answer)["count"] = 200;
            Require((int?)OperationSubmissionFixture.Data(OperationSubmissionFixture.Adopt(operations, (JObject)request.DeepClone()))["count"] == 1 && unit.Calls == 1, "A retry duplicated an order or mutated the stored receipt");
            var changed = (JObject)request.DeepClone(); changed["arguments"]!["drops"]![0]!["longitude"] = 128;
            Expect(() => OperationSubmissionFixture.Adopt(operations, changed), "request_id_conflict");
            var nextBattle = Service(() => true, unit);
            Expect(() => OperationSubmissionFixture.Adopt(nextBattle, request), "operation_scope_changed");
            Require(unit.Calls == 1, "An old scope was rebound to the current unit");
        });
        check("Unexpected operation errors stay visible and their unknown side effects are never retried blindly", () =>
        {
            var operations = new AdvisorOperations(_ => true); var effect = new FaultyOperation(); operations.Register(effect);
            var request = Request(operations); request["operation"] = effect.Id;
            try { OperationSubmissionFixture.Adopt(operations, request); throw new InvalidOperationException("Expected native error"); }
            catch (IOException) { }
            Require(effect.Calls == 1, "Faulty operation was not invoked");
            Require((string?)OperationSubmissionFixture.Adopt(operations, request)["status"] == "unknown", "Unknown outcome was discarded");
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
            var operations = new AdvisorOperations(_ => true);
            operations.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit }, () => information));
            foreach (var kind in new[] { InformationKind.BattleUnits, InformationKind.BattleWeapons })
            {
                information = AdvisorInformation.Default.With(kind, false);
                Expect(() => operations.Candidates(SonobuoyDropExtension.OperationId), "information_disabled");
                Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "blocked-" + kind)), "information_disabled");
                Require(unit.Calls == 0, "Operation registered while information was disabled");
            }
            information = AdvisorInformation.Default;
            Require(operations.Candidates(SonobuoyDropExtension.OperationId)["candidates"]!.Any() &&
                (string?)OperationSubmissionFixture.Adopt(operations, Request(operations, "enabled"))["status"] == "registered", "Restoring information did not restore the operation");
        });
        check("Sonobuoy registration appends one native order with an unfamiliar ammunition key and preserves prior orders", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit);
            var candidate = operations.Candidates(SonobuoyDropExtension.OperationId)["candidates"]![0]!;
            Require((string?)candidate["target_ref"] == "unit:42" && (int?)candidate["ammunition"]![0]!["available_count"] == 2, "Candidate identity or available inventory was lost");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var result = OperationSubmissionFixture.Adopt(operations, Request(operations));
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
                var operations = new AdvisorOperations(_ => true);
                operations.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit, other }));
                var definition = operations.Catalog()["operations"]![0]!["parameters"]!;
                Require(definition["properties"]!["depth"] == null, "The first release advertised depth control");
                var observed = operations.Candidates(SonobuoyDropExtension.OperationId)["candidates"]![0]!;
                Require((string?)observed["depth"] == "above_layer", "The current setting was not exposed");
                unit.Depth = "alternating";
                var request = Request(operations); request["arguments"]!["depth"] = requestedDepth;
                var result = OperationSubmissionFixture.Adopt(operations, request);
                var current = operations.Candidates(SonobuoyDropExtension.OperationId)["candidates"]![0]!;
                Require((string?)result["status"] == "registered" && (string?)OperationSubmissionFixture.Data(result)["depth"] == "alternating" &&
                    unit.Depth == "alternating" && (string?)current["depth"] == "alternating" && unit.Queue.Count == 2,
                    "A supplied or stale depth overwrote the player's live setting, or the reservation was lost");
                Require(other.Depth == "below_layer" && other.Queue.Count == 1, "Another helicopter was changed");
            }
        });
        check("Omitted depth preserves the current setting and replay does not overwrite later user changes", () =>
        {
            var unit = new Unit { Depth = "below_layer" }; var operations = Service(() => true, unit);
            var request = Request(operations);
            var result = OperationSubmissionFixture.Adopt(operations, request);
            Require((string?)OperationSubmissionFixture.Data(result)["depth"] == "below_layer" && unit.Depth == "below_layer", "An omitted depth reset the setting");
            unit.Depth = "above_layer";
            var replay = OperationSubmissionFixture.Adopt(operations, (JObject)request.DeepClone());
            Require(JToken.DeepEquals(result, replay) && unit.Depth == "above_layer" && unit.Calls == 1 && unit.Queue.Count == 2,
                "A receipt replay registered another order or overwrote a later user choice");
        });
        check("Sonobuoy requests recheck the current unit, flight state, available inventory and single-drop support", () =>
        {
            var unit = new Unit(); var current = new List<IBattleSonobuoyUnit> { unit };
            var operations = new AdvisorOperations(_ => true); operations.Register(SonobuoyFixture.Create(() => current));
            _ = operations.Candidates(SonobuoyDropExtension.OperationId);
            unit.UnavailableReason = "unit_not_airborne";
            Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "landed")), "unit_not_airborne");
            unit.UnavailableReason = "following_formation";
            Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "formation")), "following_formation");
            unit.UnavailableReason = null; unit.Available = 0;
            Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "no-ammo")), "no_available_sonobuoy");
            unit.Available = 2; unit.SingleDrop = false;
            Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "multi-drop")), "single_drop_unavailable");
            current.Clear();
            Expect(() => OperationSubmissionFixture.Adopt(operations, Request(operations, "gone")), "target_unavailable");
            Require(unit.Calls == 0, "A stale or foreign object was still operated");
        });
        check("Invalid sonobuoy coordinates, counts, placement and ammunition never reach the native bridge", () =>
        {
            var unit = new Unit(); var operations = Service(() => true, unit); var index = 0;
            foreach (var pair in new (string Key, JToken Value)[]
            {
                ("latitude", 91), ("longitude", -181), ("latitude", "26.5"), ("longitude", double.NaN),
                ("latitude", JValue.CreateNull()), ("count", 2), ("count", "1"), ("placement", "invalid"),
                ("ammunition", "other-weapon"), ("unknown", true)
            })
            {
                var request = Request(operations, "invalid-" + index++);
                request["arguments"]!["depth"] = "below_layer";
                var arguments = (JObject)request["arguments"]!;
                if (pair.Key == "placement") arguments[pair.Key] = pair.Value;
                else arguments["drops"]![0]![pair.Key] = pair.Value;
                try { OperationSubmissionFixture.Adopt(operations, request); throw new Exception("Invalid arguments were accepted"); }
                catch (AdvisorOperationException) { }
            }
            Require(unit.Calls == 0 && unit.Queue.Count == 1 && unit.Depth == "alternating",
                "An invalid request changed native orders or the helicopter depth setting");
        });
        check("Local operation endpoints need neither an AI connection nor a conversation window and recheck saved permission when pumped", () =>
        {
            var enabled = true; var unit = new Unit(); var operations = Service(() => enabled, unit);
            var api = new AdvisorLocalApi(() => throw new InvalidOperationException("Prompt access"), e => throw e,
                () => throw new InvalidOperationException("Connection access"), (_, _, _, _, _) => throw new InvalidOperationException("AI preparation"));
            var owner = new object();
            api.Attach(owner, "tactical_battle", () => new IdleTarget(), operations); api.Enable();
            var candidates = Send(api, "GET", "/v1/operations/" + SonobuoyDropExtension.OperationId + "/candidates");
            Require(candidates.Status == 200 && (string?)candidates.Data["scope_id"] == operations.ScopeId, "Candidates lost battle scope");
            var request = OperationSubmissionFixture.DeveloperRequest(Request(operations));
            var pending = api.Enqueue("POST", "/v1/operation-plans", request); enabled = false; api.Pump();
            Require(pending.GetAwaiter().GetResult().Status == 403 && unit.Calls == 0, "A queued request bypassed the saved command switch");
            enabled = true;
            Require(Send(api, "POST", "/v1/operation-plans", request).Status == 200 && unit.Calls == 1, "Offline registration failed");
            Require(Send(api, "POST", "/v1/operation-plans", request).Status == 200 && unit.Calls == 1, "HTTP retry duplicated the reservation");
            Require(Send(api, "POST", "/v1/operations", Request(operations, "obsolete")).Status == 404 && unit.Calls == 1, "An obsolete route changed reservations");
            Require(Send(api, "GET", "/v1/operations/candidates").Status == 404, "Malformed path was dispatched");
            api.Detach(owner);
            Require(Send(api, "POST", "/v1/operation-plans", OperationSubmissionFixture.DeveloperRequest(Request(operations, "detached"))).Status == 409 && unit.Calls == 1, "Detached battle still accepted orders");
            api.Disable();
        });
    }
    private static AdvisorOperations Service(Func<bool> enabled, Unit unit)
    {
        var operations = new AdvisorOperations(_ => enabled()); operations.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit })); return operations;
    }
    private static JObject Request(AdvisorOperations operations, string id = "request-1") => new()
    {
        ["scope_id"] = operations.ScopeId, ["request_id"] = id, ["operation"] = SonobuoyDropExtension.OperationId, ["target_ref"] = "unit:42",
        ["arguments"] = SonobuoyFixture.Arguments("custom_mod_sonobuoy")
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
    private sealed class IdleTarget : IAdvisorLocalTarget
    {
        public JObject LocalStatus() => new();
        public AdvisorLocalRun LocalConsult(string? question, string? shortcut, bool allowFallback, Func<AdvisorSituationDelivery?, AdvisorBackendSetup> prepare,
            bool plainAdvisor, bool? allowOperations, AdvisorSituationDelivery? situationDelivery) => throw new InvalidOperationException("AI access");
        public JObject LocalPreviewNotice() => throw new InvalidOperationException("Unexpected notice preview");
        public void LocalClear() => throw new InvalidOperationException("Conversation access");
        public void LocalCancel() => throw new InvalidOperationException("Conversation access");
    }
    private sealed class Unit : IBattleSonobuoyUnit
    {
        public int Id { get; }
        internal Unit(int id = 42) => Id = id;
        public string Name => "Test helicopter";
        public string? UnavailableReason { get; set; }
        public string Depth { get; set; } = "alternating";
        public JObject Position => new() { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 };
        public ReservationRoute? ReservationRoute => new(new AdvisorMapCoordinate(26.5, 127.5), null);
        internal int Calls, Available = 2;
        internal bool SingleDrop = true;
        internal string? LastAmmo;
        internal double LastLatitude, LastLongitude;
        internal readonly List<string> Queue = new() { "existing waypoint" };
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("custom_mod_sonobuoy", "Custom buoy", 3, Available, SingleDrop) };
        public void ClearReservations() => Queue.Clear();
        public void Append(string ammunition, double latitude, double longitude)
        { Calls++; LastAmmo = ammunition; LastLatitude = latitude; LastLongitude = longitude; Queue.Add("one buoy reservation"); }
    }
    private sealed class FaultyOperation : IAdvisorOperation
    {
        public bool RequiresApproval => false;
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => "Custom operation";
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Test failure";
        internal int Calls;
        public string Id => "extension.test";
        public JObject Definition() => new() { ["id"] = Id };
        public JArray Candidates() => new();
        public JObject Execute(string target, JObject arguments) { Calls++; throw new IOException("Unexpected bridge failure"); }
    }
}
