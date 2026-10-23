using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunSensors(Action<string, Action> check)
    {
        check("Own sensor status preserves individual aircraft/ship operation and freezes before communication", () =>
        {
            var f = new Fixture();
            var air = new SensorSystemRadar("Added radar", "Air", true);
            var surface = new SensorSystemRadar("Surface radar", "Surface", false) { Inoperable = new Reactive<bool>(true) };
            var sonar = new SensorSystemSonar();
            f.Own._obp._sensorSystems = new object[] { air, surface, sonar, new SensorSystemVisual() };
            var aircraft = new OwnUnit { _type = "Aircraft" };
            aircraft._obp._sensorSystems = new object[] { new SensorSystemRadar("Aircraft radar", "SurfaceAndAir", true) };
            f.OwnUnits.Add(aircraft);
            var snapshot = f.Capture();
            air.IsOn.Value = false; air.DisplayName.Value = "Changed"; sonar._sonar.IsActive.Value = true;
            var session = new BattleQuerySession(snapshot, "ja", "Sensor operation", 20,
                AdvisorInformation.Default.With(InformationKind.BattleSensorPerformance, false));
            var initial = session.InitialInput();
            Require(initial["knowledge_catalog"]!["unit_capabilities"]!["available_sections"]!.Values<string>().Contains("sensors") &&
                !initial.ToString().Contains("Added radar", StringComparison.Ordinal), "Sensor status is unavailable or was sent before being requested");
            var data = SensorQuery(session, new[] { "own_unit_1", "own_unit_2" });
            var items = data["units"]![0]!["sensors"]!;
            Require(items.Count() == 4 && (string?)items[0]!["display_name"] == "Added radar" && (bool?)items[0]!["on"] == true &&
                (string?)items[0]!["role"] == "air" && (bool?)items[1]!["on"] == false && (bool?)items[1]!["inoperable"] == true &&
                (bool?)items[2]!["on"] == true && (bool?)items[2]!["active_mode"] == false &&
                (string?)data["units"]![1]!["sensors"]![0]!["display_name"] == "Aircraft radar",
                "Individual operation, inoperability, passive sonar mode, aircraft equipment or frozen state was lost");
            Require(data["units"]![0]!["sensor_performance"] == null && data["sensor_references"] == null,
                "Disabled sensor performance was disclosed");
            var fuel = SensorQuery(session, new[] { "own_unit_1" }, "fuel");
            Require(fuel["units"]![0]!["sensors"] == null, "A fuel-only request also returned sensor status");
        });
        check("Sensor performance uses displayed units, remains independent of status and shares definitions transactionally", () =>
        {
            var f = new Fixture();
            f.Own._obp._sensorSystems = new object[] { new SensorSystemRadar("Shared radar", "Air", true), new SensorSystemSonar() };
            var second = new OwnUnit();
            second._obp._sensorSystems = new object[] { new SensorSystemRadar("Shared radar", "Air", false) };
            f.OwnUnits.Add(second);
            var snapshot = f.Capture();
            var policy = AdvisorInformation.Default.With(InformationKind.BattleSensors, false).With(InformationKind.BattleSensorPerformance, true);
            var session = new BattleQuerySession(snapshot, "en", "Compare sensors", 20, policy);
            var data = SensorQuery(session, new[] { "own_unit_1", "own_unit_2" }, "sensor_performance");
            Require(data["sensor_references"]!.Count() == 2 && data["units"]![0]!["sensors"] == null &&
                (string?)data["units"]![0]!["sensor_performance"]![0]!["reference_id"] ==
                    (string?)data["units"]![1]!["sensor_performance"]![0]!["reference_id"], "Repeated equipment duplicated definitions or required permission for live status");
            var radar = data["sensor_references"]![0]!; var sonar = data["sensor_references"]![1]!;
            Require(Math.Abs((double)radar["maximum_instrumented_range_nm"]! - 200) < 0.001 &&
                Math.Abs((double)radar["maximum_altitude_ft"]! - 60000) < 0.001 &&
                (string?)radar["frequency_band"] == "s_band" && radar["gain_db"] == null &&
                (double?)sonar["gain_db"] == 10 && (double?)sonar["active_gain_db"] == 12 &&
                sonar["supported_modes"]!.Values<string>().SequenceEqual(new[] { "active", "passive" }) &&
                sonar["maximum_instrumented_range_nm"] == null, "Sensor specifications changed displayed units, supplied hidden radar gain or invented an unconditional sonar range");
            Require(!SensorQuery(session, new[] { "own_unit_1" }, "sensor_performance")["sensor_references"]!.HasValues,
                "Sensor definitions were repeated in the same consultation");
            var fresh = new BattleQuerySession(snapshot, "en", "Retry", 20, policy);
            var failed = fresh.Execute(Request(fresh,
                new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray("sensor_performance") },
                new JObject { ["query"] = "get_units", ["ids"] = new JArray("missing") }));
            Require((string?)failed["status"] == "error" && SensorQuery(fresh, new[] { "own_unit_1" }, "sensor_performance")["sensor_references"]!.Count() == 2,
                "A failed batch consumed the sensor definitions before they were delivered");
        });
        check("Independent sensor selections retain defaults and saved choices without bypassing own-unit permissions", () =>
        {
            var f = new Fixture(); f.Own._obp._sensorSystems = new object[] { new SensorSystemRadar("Sensor", "Air", true) };
            var snapshot = f.Capture();
            var oldSelections = AdvisorInformation.Default.Data(); oldSelections.Remove("battle_sensors"); oldSelections.Remove("battle_sensor_performance");
            var defaults = AdvisorInformation.ParseSelections(oldSelections);
            Require(defaults.Allows(InformationKind.BattleSensors) && defaults.Allows(InformationKind.BattleSensorPerformance), "New defaults were not applied to existing selections");
            foreach (var status in new[] { false, true })
            foreach (var performance in new[] { false, true })
            {
                var info = defaults.With(InformationKind.BattleSensors, status).With(InformationKind.BattleSensorPerformance, performance);
                var saved = AdvisorOptions.Parse(Encoding.UTF8.GetString(new AdvisorOptions(information: info).Data())).Information;
                Require(saved.Allows(InformationKind.BattleSensors) == status && saved.Allows(InformationKind.BattleSensorPerformance) == performance,
                    "Saving reset a sensor selection");
                var session = new BattleQuerySession(snapshot, "en", "Sensors", 20, saved);
                var data = SensorQuery(session, new[] { "own_unit_1" });
                Require((data["units"]![0]!["sensors"] != null) == status &&
                    (data["units"]![0]!["sensor_performance"] != null) == performance &&
                    (data["sensor_references"] != null) == performance, "Omitted sections bypassed sensor selections");
                foreach (var (section, permitted) in new[] { ("sensors", status), ("sensor_performance", performance) })
                {
                    var response = session.Execute(Request(session, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1"), ["sections"] = new JArray(section) }));
                    Require(permitted ? (string?)response["status"] == "ok" : (string?)response["error"]?["code"] == "information_disabled", "A direct section request bypassed disabled sensor information");
                }
            }
            var noOwn = AdvisorInformation.Default.With(InformationKind.BattleSensorPerformance, true).With(InformationKind.BattleUnits, false);
            var hidden = new BattleQuerySession(snapshot, "en", "No own data", 20, noOwn);
            Require(!noOwn.Allows(InformationKind.BattleSensors) && !noOwn.Allows(InformationKind.BattleSensorPerformance) &&
                (string?)hidden.Execute(Request(hidden, new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray("own_unit_1") }))["error"]?["code"] == "information_disabled",
                "Sensor data bypassed disabled own-unit information");
        });
        check("Contact detection methods use only the displayed player track and remain language independent", () =>
        {
            var f = new Fixture();
            f.Track.DetectingSensors = 8 | 512 | 64 | 128;
            var snapshot = f.Capture(); f.Track.DetectingSensors = 0;
            foreach (var language in new[] { "en", "ja" })
            {
                var session = new BattleQuerySession(snapshot, language, "How was this detected?");
                var contact = session.Execute(Request(session, new JObject { ["query"] = "list_contacts" }))["results"]![0]!["data"]!["items"]![0]!;
                Require(contact["detection_methods"]!.Values<string>().SequenceEqual(new[] { "radar", "sonar", "esm" }),
                    "Detection sources changed with language, read live state or duplicated sonar");
            }
            Require(BattleSensorReader.DetectionMethods(f.Track).Type == JTokenType.Null, "Unknown detection sources were represented as known sensor inactivity");
            f.Track.DetectingSensors = 16;
            Require(BattleSensorReader.DetectionMethods(f.Track).Values<string>().SequenceEqual(new[] { "radar" }), "The displayed fire-control radar source was lost");
            f.Track.DetectingSensors = 2 | 4 | 32 | 1024;
            Require(BattleSensorReader.DetectionMethods(f.Track).Values<string>().SequenceEqual(new[] { "visual", "tv_seeker", "active_radar_seeker", "mad" }),
                "Other displayed detection sources were lost");
        });
    }

    private static JToken SensorQuery(BattleQuerySession session, string[] ids, params string[] sections)
    {
        var query = new JObject { ["query"] = "get_unit_capabilities", ["ids"] = new JArray(ids) };
        if (sections.Length != 0) query["sections"] = new JArray(sections);
        var result = session.Execute(Request(session, query));
        Require((string?)result["status"] == "ok", "Sensor query failed: " + result.ToString());
        return result["results"]![0]!["data"]!;
    }
    private sealed class SensorSystemRadar
    {
        public Reactive<string> DisplayName { get; }
        public Reactive<bool> IsOn { get; }
        public Reactive<bool> Inoperable { get; set; } = new(false);
        public string _systemName { get; }
        public string _type => "Search";
        public object _radar { get; }
        internal SensorSystemRadar(string name, string role, bool on)
        {
            DisplayName = new(name); IsOn = new(on); _systemName = name;
            _radar = new { _rp = new { _role = role, _frequency = "S_Band", _maxRange = 200 / 0.03628513,
                _minAltitude = 100 * 0.00453571, _maxAltitude = 60000 * 0.00453571 } };
        }
    }
    private sealed class SensorSystemSonar
    {
        public Reactive<string> DisplayName { get; } = new("Sonar");
        public Reactive<bool> IsOn { get; } = new(true);
        public Reactive<bool> Inoperable { get; } = new(false);
        public string _systemName => "Sonar";
        public bool IsActiveSensor => true;
        public TestSonar _sonar { get; } = new();
    }
    private sealed class TestSonar
    {
        public Reactive<bool> IsActive { get; } = new(false);
        public object _sp { get; } = new { _type = "HullMounted", _frequency = "MF", _isActiveIntercept = false,
            _isActiveOnly = false, _outputPower = 100.0, _gain = 10.0, _activeGain = 12.0, _detectionTreshold = 5.0 };
    }
    private sealed class SensorSystemVisual
    {
        public Reactive<string> DisplayName { get; } = new("Optics");
        public Reactive<bool> IsOn { get; } = new(true);
        public Reactive<bool> Inoperable { get; } = new(false);
        public string _systemName => "Optics";
    }
}
