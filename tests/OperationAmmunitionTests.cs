using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class OperationAmmunitionTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Weapon aliases and exact display names share canonical proposals while diagnostics retain the original arguments", () =>
        {
            using var trial = new Trial();
            var source = new BattleAdvisorSource(() => Snapshot(), () => trial.Gateway);
            var knowledge = source.CreateSession("en", "Deploy a buoy");
            var references = ((IAdvisorOperationReferenceSource)knowledge).OperationReferences;
            var run = new AdvisorLocalRun();
            using var session = run.Observe(source.CreateOperations("equivalent", AdvisorPrompts.Current, references)!);
            foreach (var value in new[] { "weapon_reference_18", "Shared buoy", "Reference buoy", "mod_buoy" })
            {
                var request = Request(value); var original = request.DeepClone();
                var answer = Send(trial, session, request);
                Require((string?)answer["status"] == "proposed" && (bool?)answer["registered"] == false, "An exact identity did not stage a proposal");
                if (value != "mod_buoy")
                    Require((string?)OperationSubmissionFixture.ArgumentResolutions(answer)[0]?["requested"] == value &&
                        (string?)OperationSubmissionFixture.ArgumentResolutions(answer)[0]?["resolved"] == "mod_buoy", "The correction is missing from the response");
                else Require(OperationSubmissionFixture.ArgumentResolutions(answer).Count == 0, "A canonical ID was recorded as a correction");
                Require(JToken.DeepEquals(request, original) && JToken.DeepEquals(run.Snapshot()["operations"]!.Last!["request"], original),
                    "Normalization modified the original request or its diagnostic record");
            }
            var proposal = trial.Service.FinishProposal("equivalent", true)!;
            Require(proposal.Orders.Count == 1 && (string?)proposal.Orders[0].Arguments["drops"]![0]!["ammunition"] == "mod_buoy" &&
                trial.Main.Orders.Count == 0, "Alias retries duplicated the proposal or bypassed approval");
            trial.Service.Approve(proposal); trial.Service.Approve(proposal);
            Require(trial.Main.Orders.SequenceEqual(new[] { "mod_buoy" }) &&
                (string?)Send(trial, session, Request("weapon_reference_18"))["status"] == "registered", "Approval or replay duplicated a registration");
            var changed = Request("mod_buoy"); OperationSubmissionFixture.Arguments(changed)!["drops"]![0]!["latitude"] = 27;
            Require(Code(Send(trial, session, changed)) == "request_id_conflict", "Changed content silently replaced a request");
        });
        check("Ambiguous display names are rejected without choosing by stock or availability, while exact references remain usable", () =>
        {
            using var trial = new Trial();
            trial.Main.Ammo.Add(Ammo("second_buoy", "Shared buoy", 0));
            using var session = trial.Gateway.CreateSession("ambiguous", AdvisorPrompts.Current, References())!;
            Require(Code(Send(trial, session, Request("Shared buoy"))) == "ambiguous_ammunition_reference", "A duplicate name selected an available alternative");
            Require((string?)Send(trial, session, Request("weapon_reference_18", "exact"))["status"] == "proposed", "An unambiguous reference was rejected due to another display name");
            var proposal = trial.Service.FinishProposal("ambiguous", true)!;
            Require(proposal.Orders.Count == 1 && trial.Main.Orders.Count == 0, "Ambiguous input left an extra executable order");
            trial.Service.Approve(proposal);
            Require(trial.Main.Orders.SequenceEqual(new[] { "mod_buoy" }), "The wrong variant was registered");
        });
        check("Canonical keys take priority and unknown, uncarried or approximate identities cannot redirect ammunition", () =>
        {
            using var trial = new Trial();
            trial.Main.Ammo.Add(Ammo("second_buoy", "mod_buoy"));
            trial.Main.Ammo.Add(Ammo("name_collision", "foreign_buoy"));
            using var session = trial.Gateway.CreateSession("strict", AdvisorPrompts.Current, References())!;
            Require((string?)Send(trial, session, Request("mod_buoy"))["status"] == "proposed", "A canonical key was reinterpreted as another name");
            var attempt = 0;
            foreach (var value in new[] { "foreign_buoy", "weapon_reference_19", "weapon_reference_999", "shared buoy", "Shared", "Shared buoy " })
            {
                using var invalid = trial.Gateway.CreateSession("invalid-" + ++attempt, AdvisorPrompts.Current, References())!;
                Require(Code(Send(trial, invalid, Request(value))) == "sonobuoy_not_carried", "A foreign or approximate identity was accepted: " + value);
                Require(trial.Service.FinishProposal("invalid-" + attempt, true)!.Orders.Count == 0, "Rejected input left executable work");
            }
            var proposal = trial.Service.FinishProposal("strict", true)!;
            Require(proposal.Orders.Count == 1 && (string?)proposal.Orders[0].Arguments["drops"]![0]!["ammunition"] == "mod_buoy", "Rejected input changed the proposal");
            trial.Service.Approve(proposal);
            Require(trial.Main.Orders.SequenceEqual(new[] { "mod_buoy" }) && trial.Other.Orders.Count == 0, "A name collision redirected the order");
        });
        check("Approval uses the stored ammunition ID despite name changes and still rejects depleted inventory", () =>
        {
            using var trial = new Trial();
            using var session = trial.Gateway.CreateSession("names", AdvisorPrompts.Current, References())!;
            Require((string?)Send(trial, session, Request("Shared buoy"))["status"] == "proposed", "Display-name proposal failed");
            var proposal = trial.Service.FinishProposal("names", true)!;
            trial.Main.Ammo[0] = Ammo("mod_buoy", "Renamed buoy");
            trial.Main.Ammo.Add(Ammo("second_buoy", "Shared buoy"));
            trial.Service.Approve(proposal);
            Require(trial.Main.Orders.SequenceEqual(new[] { "mod_buoy" }), "Approval resolved the display name again");
            using var shortage = trial.Gateway.CreateSession("shortage", AdvisorPrompts.Current, References())!;
            Require((string?)Send(trial, shortage, Request("weapon_reference_18"))["status"] == "proposed", "Reference proposal failed");
            var pending = trial.Service.FinishProposal("shortage", true)!;
            trial.Main.Ammo[0] = Ammo("mod_buoy", "Renamed buoy", 0);
            trial.Service.Approve(pending);
            Require(pending.State == AdvisorProposalState.Pending && pending.ErrorCode == "no_available_sonobuoy" &&
                trial.Main.Orders.Count == 1, "Approval substituted another available variant instead of rejecting depletion");
        });
        check("Consultation reference mappings are isolated and reusing an order ID cannot rebind an alias to different ammunition", () =>
        {
            using var trial = new Trial();
            trial.Main.Ammo.Add(Ammo("second_buoy", "Other buoy"));
            using var first = trial.Gateway.CreateSession("owner", AdvisorPrompts.Current, References())!;
            Require((string?)Send(trial, first, Request("weapon_reference_18"))["status"] == "proposed", "Initial alias was not staged");
            var proposal = trial.Service.FinishProposal("owner", true)!; trial.Service.Approve(proposal);
            var remapped = new AdvisorOperationReferences(References().Units,
                new AdvisorWeaponReferences(new[] { ("weapon_reference_18", "second_buoy", "Other buoy") }));
            using var retry = trial.Gateway.CreateSession("owner", AdvisorPrompts.Current, remapped)!;
            Require(Code(Send(trial, retry, Request("weapon_reference_18"))) == "request_id_conflict", "A fresh reference map redirected an old order");
            using var next = trial.Gateway.CreateSession("next", AdvisorPrompts.Current, remapped)!;
            Require((string?)Send(trial, next, Request("weapon_reference_18"))["status"] == "proposed", "A new consultation reused the previous map");
            Require((string?)trial.Service.FinishProposal("next", true)!.Orders[0].Arguments["drops"]![0]!["ammunition"] == "second_buoy" &&
                trial.Main.Orders.SequenceEqual(new[] { "mod_buoy" }), "A different consultation's proposal executed or lost its identity");
        });
        check("Operation reference maps exclude disabled weapons, excluded units and enemy-only performance records", () =>
        {
            var snapshot = Snapshot();
            var all = new BattleQuerySession(snapshot, "en", "Test", information: AdvisorInformation.All).OperationReferences;
            var off = new BattleQuerySession(snapshot, "en", "Test", information: AdvisorInformation.All.With(InformationKind.BattleWeapons, false)).OperationReferences;
            var hidden = new BattleQuerySession(snapshot, "en", "Test", information: AdvisorInformation.All.With(InformationKind.BattleGroundUnits, false)).OperationReferences;
            var candidates = new[] { ("mod_buoy", "Current name"), ("foreign_buoy", "Another name"), ("enemy_weapon", "Enemy name") };
            Require(all.Weapons.Match("weapon_reference_18", candidates, out _).SequenceEqual(new[] { "mod_buoy" }) &&
                off.Weapons.Match("weapon_reference_18", candidates, out _).Length == 0 &&
                hidden.Weapons.Match("weapon_reference_19", candidates, out _).Length == 0 &&
                all.Weapons.Match("weapon_reference_20", candidates, out _).Length == 0,
                "The operation table included information outside the permission-filtered own inventory");
            using var trial = new Trial();
            trial.Information = AdvisorInformation.Default.With(InformationKind.BattleWeapons, false);
            using var session = trial.Gateway.CreateSession("off", AdvisorPrompts.Current, all)!;
            Require(Code(Send(trial, session, Request("Shared buoy"))) == "information_disabled" && trial.Main.Orders.Count == 0,
                "Display-name matching bypassed a live information restriction");
        });
        check("Commands without argument normalization and explicit developer registrations keep their original argument contract", () =>
        {
            using var trial = new Trial();
            var custom = new CustomOperation(); trial.Service.Register(custom);
            using var session = trial.Gateway.CreateSession("custom", AdvisorPrompts.Current, References())!;
            var request = Request("Shared buoy"); request["plans"]![0]!["operations"]![0]!["operation_id"] = custom.Id;
            request["plans"]![0]!["reservation_updates"] = new JArray();
            request["plans"]![0]!["operations"]![0]!["arguments"] = new JObject { ["ammunition"] = "Shared buoy" };
            Require((string?)Send(trial, session, request)["status"] == "proposed", "A custom operation stopped accepting its arguments");
            trial.Service.FinishProposal("custom", true);
            Require(custom.Value == "Shared buoy", "The host interpreted an arbitrary command's ammunition field");
            var direct = new JObject { ["scope_id"] = trial.Service.ScopeId, ["request_id"] = "manual",
                ["operation"] = SonobuoyDropExtension.OperationId, ["target_ref"] = "unit:42", ["arguments"] = SonobuoyFixture.Arguments("Shared buoy") };
            try { OperationSubmissionFixture.Adopt(trial.Service, direct); throw new Exception("Developer input was normalized"); }
            catch (AdvisorOperationException error) when (error.Code == "sonobuoy_not_carried") { }
            Require(trial.Main.Orders.Count == 0, "Rejected developer input registered an order");
        });
    }

    private static AdvisorOperationReferences References() => new(new AdvisorUnitReferences(new[] { ("own_unit_8", 42) }),
        new AdvisorWeaponReferences(new[] { ("weapon_reference_18", "mod_buoy", "Shared buoy"),
            ("weapon_reference_18", "mod_buoy", "Reference buoy"), ("weapon_reference_19", "foreign_buoy", "Shared buoy") }));
    private static BattleSnapshot Snapshot()
    {
        var data = new JObject
        {
            ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = "Briefing",
            ["friendly_units"] = new JArray(new JObject { ["id"] = "own_unit_8", ["display_name"] = "Hobby 1", ["kind"] = "helicopter" },
                new JObject { ["id"] = "own_unit_9", ["display_name"] = "Excluded unit", ["kind"] = "ground", ["unit_category"] = "ground_unit" }),
            ["unit_capabilities"] = new JArray(Capability("own_unit_8", "weapon_reference_18"), Capability("own_unit_9", "weapon_reference_19")),
            ["weapon_references"] = new JArray(Reference("weapon_reference_18", "mod_buoy"), Reference("weapon_reference_19", "foreign_buoy"),
                Reference("weapon_reference_20", "enemy_weapon"))
        };
        foreach (var name in new[] { "public_objectives", "mission_map_areas", "friendly_bases", "hosted_air_units", "aviation_hosts", "contact_threat_references", "published_reports", "observed_contacts" })
            data[name] = new JArray();
        return new BattleSnapshot(data, References().Units);
    }
    private static JObject Capability(string id, string reference) => new() { ["id"] = id, ["weapons"] = new JObject {
        ["ammunition"] = new JArray(new JObject { ["reference_id"] = reference, ["display_name"] = "Shared buoy", ["remaining_count"] = 12 }) } };
    private static JObject Reference(string id, string key) => new() { ["id"] = id, ["reference_key"] = key, ["display_name"] = "Reference buoy" };
    private static BattleSonobuoyAmmo Ammo(string id, string name, int available = 12) => new(id, name, available, available, true);
    private static JObject Request(string ammunition, string id = "order-1") => OperationSubmissionFixture.AiRequest(new JObject
    {
        ["operation_id"] = SonobuoyDropExtension.OperationId, ["request_id"] = id, ["target_ref"] = "unit:own_unit_8",
        ["arguments"] = SonobuoyFixture.Arguments(ammunition)
    });
    private static JObject Send(Trial trial, IAdvisorOperationSession session, JObject request) =>
        OperationGatewayTests.Finish(trial.Gateway, session.ExecuteAsync(request, CancellationToken.None));
    private static string? Code(JObject answer) => (string?)answer["code"] ?? (string?)answer["error"]?["code"];
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Trial : IDisposable
    {
        internal readonly Helicopter Main = new(42, "mod_buoy"), Other = new(43, "foreign_buoy");
        internal AdvisorInformation Information = AdvisorInformation.Default;
        internal readonly AdvisorOperations Service = new(() => true);
        internal readonly AdvisorOperationGateway Gateway;
        internal Trial()
        {
            Service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { Main, Other }, () => Information));
            Gateway = new AdvisorOperationGateway(Service);
        }
        public void Dispose() => Gateway.Dispose();
    }
    private sealed class Helicopter : IBattleSonobuoyUnit
    {
        public int Id { get; }
        public string Name => "Hobby " + Id;
        public string? UnavailableReason => null;
        public string Depth => "alternating";
        public JObject Position => new() { ["latitude_deg"] = 26.5, ["longitude_deg"] = 127.5 };
        public ReservationRoute? ReservationRoute => new(new AdvisorMapCoordinate(26.5, 127.5), null);
        internal readonly List<BattleSonobuoyAmmo> Ammo;
        internal readonly List<string> Orders = new();
        internal Helicopter(int id, string ammunition) { Id = id; Ammo = new() { OperationAmmunitionTests.Ammo(ammunition, "Shared buoy") }; }
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => Ammo;
        public void ClearReservations() => Orders.Clear();
        public void Append(string ammunition, double latitude, double longitude) => Orders.Add(ammunition);
    }
    private sealed class CustomOperation : IAdvisorOperation
    {
        public string Id => "custom.keep_arguments";
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Unrelated arguments";
        public bool RequiresApproval => false;
        internal string? Value;
        public JObject Definition() => new();
        public JArray Candidates() => new();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => "Custom";
        public JObject Execute(string target, JObject arguments) { Value = (string?)arguments["ammunition"]; return new JObject(); }
    }
}
