using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunWeapons(Action<string, Action> check)
    {
        check("Launched weapons are captured separately, fetched in bounded pages and frozen before communication", () =>
        {
            var f = new Fixture();
            var missile = new OwnWeapon("Missile") { _launchPlatform = f.Own, CurrentIntendedTargetObject = f.Secret };
            missile._seeker.Target = f.Secret;
            f.OwnWeapons.Add(missile);
            f.OwnWeapons.Add(new OwnWeapon("Torpedo") { Altitude = new Reactive<double>(-150) });
            f.OwnWeapons.Add(new OwnWeapon("Missile") { Launched = false });
            f.OwnWeapons.Add(new OwnWeapon("Torpedo") { IsDestroyed = true });
            f.OwnWeapons.Add(new OwnWeapon("Missile") { _flightStage = "Debris" });
            f.OwnWeapons.Add(new OwnWeapon("Bomb"));
            var snapshot = f.Capture();
            missile.Name.Value = "Changed after snapshot"; missile.Position.Value = new Geo(0, 0);
            missile.CurrentIntendedTargetObject = null; missile._seeker.Target = null;
            var session = new BattleQuerySession(snapshot, "en", "What is already attacking this contact?", 20);
            var initial = session.InitialInput();
            Require((int?)initial["knowledge_catalog"]!["friendly_launched_weapons"]!["item_count"] == 2 &&
                !initial.ToString().Contains("Launched test weapon", StringComparison.Ordinal), "Launched-weapon details were sent before being requested");
            var batch = session.Execute(Request(session,
                new JObject { ["query"] = "list_launched_weapons", ["limit"] = 1 },
                new JObject { ["query"] = "list_launched_weapons", ["offset"] = 1, ["limit"] = 1 }));
            var first = batch["results"]![0]!["data"]!; var row = first["items"]![0]!;
            Require((int?)first["total_count"] == 2 && (bool?)first["has_more"] == true && (int?)first["next_offset"] == 1 &&
                (string?)row["display_name"] == "Launched test weapon" && (double?)row["position"]!["latitude_deg"] == 31 &&
                (double?)row["speed_knots"] == 500 && (double?)row["heading_deg"] == 90 && (double?)row["altitude_ft"] == 50 &&
                (string?)row["launch_platform_id"] == "own_unit_1" &&
                (string?)row["targeting"]!["commanded_target_id"] == "observed_contact_1" &&
                (string?)row["targeting"]!["tracked_target_id"] == "observed_contact_1",
                "A weapon lost its motion, launcher, target association or detached snapshot");
            Require((double?)batch["results"]![1]!["data"]!["items"]![0]!["depth_ft"] == 150 &&
                (bool?)batch["results"]![1]!["data"]!["has_more"] == false,
                "Torpedo depth or pagination was incorrect");
            var invalid = session.Execute(Request(session, new JObject { ["query"] = "list_launched_weapons", ["limit"] = 21 }));
            Require((string?)invalid["error"]?["code"] == "invalid_arguments", "Launched weapons bypassed page limits");
        });
        check("Own tracking, commanded targets, positional orders and missing target knowledge stay distinct", () =>
        {
            var f = new Fixture();
            var weapon = new OwnWeapon("Missile") { CurrentIntendedTargetObject = f.Secret };
            weapon._seeker.Target = f.Own;
            f.OwnWeapons.Add(weapon);
            JObject Targeting() => (JObject)f.Capture().Copy()["friendly_launched_weapons"]![0]!["targeting"]!;
            var targeting = Targeting();
            Require((string?)targeting["commanded_target_id"] == "observed_contact_1" &&
                (string?)targeting["tracked_target_id"] == "own_unit_1", "The commanded target replaced the current captured target");
            weapon._seeker.Target = null;
            Require((string?)Targeting()["tracking_status"] == "searching", "No capture was reported as a lock on the commanded target");
            weapon._seeker.Active = false;
            Require((string?)Targeting()["tracking_status"] == "inactive", "An inactive seeker was reported as searching");
            weapon._seeker.Active = true; weapon._seeker.Target = new object();
            Require((string?)Targeting()["tracking_status"] == "unknown" && Targeting()["tracked_target_id"]!.Type == JTokenType.Null,
                "An unobserved target was disclosed or converted into no target");
            weapon.CurrentIntendedTargetObject = null; weapon.CommandPosition = new Geo(30, 132);
            targeting = Targeting();
            Require((string?)targeting["commanded_target_status"] == "none" && targeting["commanded_target_id"]!.Type == JTokenType.Null &&
                (double?)targeting["commanded_position"]!["longitude_deg"] == 132, "A positional order became an object target");
        });
        check("Observed weapons report only visible tracking and never read another side's commanded target", () =>
        {
            var f = new Fixture();
            var enemy = new ForeignWeapon { Target = f.Own };
            var model = AddWeaponContact(f, enemy, "Missile");
            JToken Contact() => f.Capture().Copy()["observed_contacts"]![1]!;
            var visible = Contact();
            Require((string?)visible["targeting"]!["tracking_status"] == "tracking" &&
                (string?)visible["targeting"]!["tracked_target_id"] == "own_unit_1" &&
                visible["targeting"]!["commanded_target_id"] == null && visible["targeting"]!["commanded_position"] == null &&
                (string?)visible["display_name"] == "Contact 8", "An opposing weapon disclosed commands or an actual unit name");
            model.LineToTargetColor = new LineColor(0); enemy.ForbidEcho = true;
            Require((string?)Contact()["targeting"]!["tracking_status"] == "unknown", "A hidden target line leaked tracking");
            model.LineToTargetColor = new LineColor(255); enemy.Reportable = false;
            Require((string?)Contact()["targeting"]!["tracking_status"] == "unknown", "A non-reportable target leaked through a stale line");
            enemy.Reportable = true; enemy.ForbidEcho = false; enemy.Target = null;
            Require((string?)Contact()["targeting"]!["tracking_status"] == "unknown", "A missing capture fell back to an enemy command");
            enemy.Target = new SecretEnemy();
            Require(Contact()["targeting"]!["tracked_target_id"]!.Type == JTokenType.Null, "An unobserved target received a usable ID");
        });
        check("Independent launched/contact permissions remove data and references across every battle query route", () =>
        {
            var f = new Fixture();
            var enemy = new ForeignWeapon { Target = f.Own };
            AddWeaponContact(f, enemy, "Torpedo");
            var own = new OwnWeapon("Missile") { _launchPlatform = f.Own, CurrentIntendedTargetObject = enemy };
            own._seeker.Target = enemy; f.OwnWeapons.Add(own);
            var snapshot = f.Capture();
            BattleQuerySession Session(AdvisorInformation info) => new(snapshot, "en", "Weapons", 20, info);
            JToken Query(BattleQuerySession session, string query) => session.Execute(Request(session, new JObject { ["query"] = query }));
            var noOwn = Session(AdvisorInformation.Default.With(InformationKind.BattleLaunchedWeapons, false));
            Require(noOwn.InitialInput()["knowledge_catalog"]!["friendly_launched_weapons"] == null &&
                (string?)Query(noOwn, "list_launched_weapons")["error"]?["code"] == "information_disabled" &&
                Query(noOwn, "list_contacts")["results"]![0]!["data"]!["items"]!.Count() == 2, "Own weapon exclusion also hid observed weapons or allowed a direct bypass");
            var noContacts = Session(AdvisorInformation.Default.With(InformationKind.BattleWeaponContacts, false));
            Require(!BattleContactAssessment.IsAvailable(AdvisorInformation.Default.With(InformationKind.BattleWeaponContacts, false),
                false, false, "hostile", false, "torpedo") &&
                BattleContactAssessment.IsAvailable(AdvisorInformation.Default, false, false, "hostile", false, "torpedo"),
                "The information-window assessment button ignored the weapon-contact selection");
            Require((int?)noContacts.InitialInput()["situation"]!["overview"]!["observed_contact_count"] == 1 &&
                Query(noContacts, "list_contacts")["results"]![0]!["data"]!["items"]!.Count() == 1,
                "Excluded weapon contacts remained in counts or the contact list");
            var ownRow = Query(noContacts, "list_launched_weapons")["results"]![0]!["data"]!["items"]![0]!;
            Require(ownRow["targeting"]!["tracked_target_id"]!.Type == JTokenType.Null &&
                ownRow["targeting"]!["commanded_target_id"]!.Type == JTokenType.Null &&
                (string?)ownRow["targeting"]!["commanded_target_status"] == "unknown",
                "An excluded contact reappeared through own targeting");
            var filtered = noContacts.Execute(Request(noContacts, new JObject { ["query"] = "list_contacts", ["kind"] = "torpedo" }));
            Require((string?)filtered["error"]?["code"] == "information_disabled", "A kind filter bypassed the weapon-contact exclusion");
            var noBasics = Session(AdvisorInformation.Default.With(InformationKind.BattleUnits, false));
            var contactRow = Query(noBasics, "list_contacts")["results"]![0]!["data"]!["items"]![1]!;
            Require(contactRow["targeting"]!["tracked_target_id"]!.Type == JTokenType.Null &&
                noBasics.InitialInput()["knowledge_catalog"]!["friendly_launched_weapons"] == null,
                "Weapon targeting bypassed disabled own identities");
            var noInventory = Session(AdvisorInformation.Default.With(InformationKind.BattleWeapons, false));
            Require((string?)Query(noInventory, "list_launched_weapons")["status"] == "ok", "Inventory permission incorrectly blocked independently enabled launched weapons");
            foreach (var kind in new[] { InformationKind.BattleLaunchedWeapons, InformationKind.BattleWeaponContacts })
            foreach (var enabled in new[] { false, true })
            {
                var choice = AdvisorInformation.Default.With(kind, enabled);
                var saved = AdvisorOptions.Parse(Encoding.UTF8.GetString(new AdvisorOptions(information: choice).Data())).Information;
                Require(saved.Allows(kind) == enabled, "Saving reset a weapon selection");
            }
        });
    }

    private static WeaponContactViewModel AddWeaponContact(Fixture f, ForeignWeapon weapon, string kind)
    {
        var model = new WeaponContactViewModel(weapon);
        var track = new Track { Object = weapon, Id = 8 };
        track.IsDormant.Value = false;
        f.ExtraMapUnits.Add(new { Unit = track, ObjectViewModel = model, VisualType = kind, VisualPosture = "Hostile" });
        return model;
    }
    private sealed class WeaponSeeker
    {
        internal bool Active = true;
        internal object? Target;
        public bool isActive() => Active;
    }
    private sealed class ForeignWeapon
    {
        internal object? Target;
        internal bool Reportable = true, ForbidEcho;
        public bool IsDestroyed => false;
        public bool TargetedAtUnitWhichCanReportDetection() => Reportable;
        public object? CurrentStrongestTargetEcho => ForbidEcho ? throw new InvalidOperationException("Read non-displayed enemy tracking") :
            Target == null ? null : new { _objectBase = Target };
        public object CurrentIntendedTargetObject => throw new InvalidOperationException("Read an enemy's commanded target");
        public object CurrentTarget => throw new InvalidOperationException("Read the enemy's fallback target");
        public object CommandPosition => throw new InvalidOperationException("Read an enemy's commanded position");
        public object _launchPlatform => throw new InvalidOperationException("Read an enemy's launch platform");
        public object Position => throw new InvalidOperationException("Read an enemy's actual position");
        public string Name => throw new InvalidOperationException("Read an enemy's actual name");
        public object _obp => throw new InvalidOperationException("Read enemy capabilities");
    }
    private sealed class WeaponContactViewModel
    {
        public object WeaponBase { get; }
        public object[] LineToTarget { get; set; } = { new Geo(31, 130), new Geo(32, 131) };
        public LineColor LineToTargetColor { get; set; } = new(255);
        internal WeaponContactViewModel(object weapon) { WeaponBase = weapon; }
    }
    private sealed class LineColor
    {
        public byte A { get; }
        internal LineColor(byte alpha) { A = alpha; }
    }
}
