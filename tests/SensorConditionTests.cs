using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class SensorConditionTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("All submarine mast types reject ON below periscope depth but retain OFF and recover at allowed depth and sea state", () =>
        {
            var sub = new Submarine { BelowPeriscope = true }; sub.IsSubmerged.Value = true;
            var sensors = new SensorSystem[] { new SensorSystemESM { _sectionName = "esm" },
                new SensorSystemRadar { _sectionName = "radar" },
                new SensorSystemVisual { _sectionName = "scope", _systemType = BaseSystem.SystemType.SensorPeriscope } };
            sub._obp._sensorSystems.AddRange(sensors); foreach (var s in sensors) s.IsOn.Value = false;
            var game = new BattleOperationSupport(new[] { sub });
            foreach (var sensor in sensors)
            {
                var controls = game.GetSensorUnits().Single().Controls;
                var c = controls.Single(c => c.Id == sensor._sectionName + ".power");
                Require(c.Options.Single(o => o.Value == "on").UnavailableReason == "mast_below_periscope_depth" &&
                    c.Options.Single(o => o.Value == "off").UnavailableReason == null, "ON restrictions incorrectly disabled the mast's OFF choice");
                Reject("mast_below_periscope_depth", () => game.SetSensorControls("unit:42", new[] { new SensorControlChange(c.Id, "on") }));
            }
            var candidate = Service(game).Candidates(SensorExtension.OperationId)["candidates"]![0]!["controls"]![0]!;
            Require(candidate["options"]!.Values<string>().SequenceEqual(new[] { "off" }) && (bool)candidate["available"]! &&
                (string?)candidate["option_conditions"]![0]!["unavailable_reason"] == "mast_below_periscope_depth",
                "The AI was still offered ON or lost the legal OFF choice/rejection reason");
            Require(sensors.All(s => s.Executed.Count == 0) && sub.DesiredAltitude.Value == 1, "Discovery or rejected ON changed depth or equipment");
            sub.BelowPeriscope = false;
            try
            {
                SeaPower.Environment.Instance.SeaState = 6;
                Reject("mast_sea_state_too_high", () => game.SetSensorControls("unit:42", Changes("esm", "on")));
                SeaPower.Environment.Instance.SeaState = 5;
                game.SetSensorControls("unit:42", Changes("esm", "on"));
                Require(sensors[0]._requestEnable, "A mast remained unavailable after its live conditions recovered");
            }
            finally { SeaPower.Environment.Instance.SeaState = 0; }
        });
        check("The selected plan is rejected before any unit changes when a mast's conditions change before adoption", () =>
        {
            var ship = new Vessel(); var radar = new SensorSystemRadar { _sectionName = "search" }; radar.IsOn.Value = false;
            ship._obp._sensorSystems.Add(radar); ship._obp._searchRadarSystems.Add(radar);
            var sub = new Submarine { Id = 43 }; sub.IsSubmerged.Value = true;
            var mast = new SensorSystemESM { _sectionName = "esm" }; mast.IsOn.Value = false; sub._obp._sensorSystems.Add(mast);
            var service = Service(new BattleOperationSupport(new ObjectBase[] { ship, sub }));
            OperationSubmissionFixture.Submit(service, "test", Order(42, "search", "on"), Order(43, "esm", "on"));
            var proposal = service.FinishProposal("test", true)!; sub.BelowPeriscope = true;
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "mast_below_periscope_depth" &&
                radar.Executed.Count == 0 && mast.Executed.Count == 0, "A late mast refusal was discovered only after another target had executed");
        });
        check("Target-specific towed deployment checks preserve OFF and reject speed, altitude, shallow water, surface and seabed restrictions", () =>
        {
            foreach (var cause in new[] { "speed", "altitude", "shallow", "surface", "bottom" })
            {
                ObjectBase unit = cause is "surface" or "bottom" ? new Submarine() : new Vessel();
                var sensor = Tow(unit); var support = new BattleOperationSupport(new[] { unit });
                unit.SpeedValid = cause != "speed"; unit.AltitudeValid = cause != "altitude";
                unit.Position.Value.Terrain = cause == "shallow" ? 0 : -10; unit.NearBottom = cause == "bottom";
                if (unit is Submarine sub) { sub.IsSubmerged.Value = cause != "surface"; sub.BelowPeriscope = true; }
                var control = support.GetSensorUnits().Single().Controls.Single(c => c.Setting == "power");
                Require(control.Options.Single(o => o.Value == "on").UnavailableReason != null &&
                    control.Options.Single(o => o.Value == "off").UnavailableReason == null,
                    "A physical restriction was missed or incorrectly prevented OFF: " + cause);
                var expected = cause switch { "speed" => "sensor_deployment_speed", "altitude" => "sensor_deployment_altitude",
                    "shallow" => "sensor_water_too_shallow", "surface" => "sensor_requires_submerged_unit", _ => "sensor_too_close_to_seabed" };
                Require(control.Options.Single(o => o.Value == "on").UnavailableReason == expected, "The reported physical restriction did not match the target state");
                Reject(expected, () => support.SetSensorControls("unit:42", Changes("tow", "on")));
                Require(sensor.Executed.Count == 0 && unit.StopRequests == 0, "A refused request already issued preparation or deployment");
            }
            var shallow = new Vessel(); var deep = new Vessel { Id = 43 }; Tow(shallow); var valid = Tow(deep);
            shallow.Position.Value.Terrain = 0;
            var game = new BattleOperationSupport(new ObjectBase[] { shallow, deep });
            game.SetSensorControls("unit:43", Changes("tow", "on"));
            Require(valid._requestEnable && shallow._obp._sensorSystems[0].Executed.Count == 0,
                "A different unit's shallow position or selected state affected the requested target");
        });
        check("Helicopter deployment advertises hover and descent without mutation, preserves native intent, and repeated ON cannot cancel it", () =>
        {
            var helicopter = new Helicopter { SpeedValid = false, AltitudeValid = false }; var sonar = Tow(helicopter);
            var game = new BattleOperationSupport(new[] { helicopter }); var service = Service(game);
            var candidates = service.Candidates(SensorExtension.OperationId)["candidates"]![0]!["controls"]!;
            var power = candidates.Single(c => (string?)c["id"] == "tow.power");
            Require(power["options"]!.Values<string>().Contains("on") && power["option_conditions"]!.Single()["preparation"] != null &&
                helicopter.DesiredAltitude.Value == 1 && sonar.Executed.Count == 0, "Preparations ran during discovery or a supported native preparation was excluded");
            OperationSubmissionFixture.Submit(service, "test", Order(42, "tow", "on")); var proposal = service.FinishProposal("test", true)!;
            var description = service.DescribeProposal(proposal, "en");
            Require(description.Contains("Hover", StringComparison.Ordinal) && helicopter.DesiredAltitude.Value == 1, "Review hid the motion side effect or performed it before adoption");
            service.Approve(proposal); game.SetSensorControls("unit:42", Changes("tow", "on"));
            Require(proposal.State == AdvisorProposalState.Registered && helicopter.DesiredAltitude.Value < 1 && sonar._wasSetOn &&
                sonar._requestEnable && !sonar._requestDisable && !sonar.IsOn.Value && sonar.Executed.SequenceEqual(new[] { "on" }),
                "Startup was mistaken for completion, the request did not persist, or repeated ON cancelled it");
            game.SetSensorControls("unit:42", Changes("tow", "off"));
            Require(sonar._requestDisable && !sonar._wasSetOn, "OFF failed to cancel native pending deployment");
            // Native shutdown can consume the OFF request before the visible IsOn state becomes false.
            sonar.IsOn.Value = true; sonar._requestDisable = false;
            game.SetSensorControls("unit:42", Changes("tow", "on"));
            Require(sonar._requestEnable && sonar._wasSetOn && sonar.Executed.SequenceEqual(new[] { "on", "off", "on" }),
                "ON during shutdown was mistaken for an already satisfied request");
        });
        check("Ship dipping preparation only stops the requested ship after approval and does not accept a native speed-rejected request", () =>
        {
            var ship = new Vessel { Telegraph = 3 }; var other = new Vessel { Id = 43, Telegraph = 2 };
            var sonar = Tow(ship); sonar._isDippingSonar = true; Tow(other)._isDippingSonar = true;
            var game = new BattleOperationSupport(new ObjectBase[] { ship, other });
            var choice = game.GetSensorUnits()[0].Controls.Single(c => c.Setting == "power").Options.Single(o => o.Value == "on");
            Require(choice.UnavailableReason == null && choice.Preparation == "stop_for_deployment" && ship.StopRequests == 0,
                "Ship-stop preparation was not disclosed or ran during discovery");
            game.SetSensorControls("unit:42", Changes("tow", "on"));
            Require(ship.Telegraph == 0 && ship.StopRequests == 1 && sonar._requestEnable && other.Telegraph == 2 && other.StopRequests == 0,
                "Ship-stop preparation was omitted or propagated to another unit");
            var moving = new Vessel { Telegraph = 3, SpeedValid = false }; Tow(moving)._isDippingSonar = true;
            var support = new BattleOperationSupport(new[] { moving });
            Reject("sensor_deployment_speed", () => support.SetSensorControls("unit:42", Changes("tow", "on")));
            Require(moving.StopRequests == 0, "An unsupported delayed deployment started despite failed native speed conditions");
        });
        check("Layer selection requests native deployment and persists its layer, while active ON waits for the sensor to be deployed", () =>
        {
            var helicopter = new Helicopter(); var sonar = Tow(helicopter); sonar.IsActiveSensor = true;
            var game = new BattleOperationSupport(new[] { helicopter });
            Reject("sensor_not_deployed", () => game.SetSensorControls("unit:42", new[] { new SensorControlChange("tow.active", "on") }));
            game.SetSensorControls("unit:42", new[] { new SensorControlChange("tow.active", "off") });
            Require(sonar.Executed.Count == 0, "Active OFF deployed an inactive sensor");
            game.SetSensorControls("unit:42", new[] { new SensorControlChange("tow.depth", "below_layer") });
            Require(sonar._requestEnable && sonar._wasSetOn && sonar.SearchBelowLayer.Value, "Layer selection lost native deployment intent or the selected layer");
            sonar.IsOn.Value = true;
            game.SetSensorControls("unit:42", new[] { new SensorControlChange("tow.active", "on") });
            Require(sonar._sonar.IsActive.Value && sonar.Executed.SequenceEqual(new[] { "on" }), "Active mode redundantly deployed or cancelled the sonar");
        });
        check("A native refusal after preparation remains an observable failure with possible changes rather than success or rollback", () =>
        {
            var ship = new Vessel { Telegraph = 3 }; var sonar = Tow(ship); sonar._isDippingSonar = true; sonar.RejectEnable = true;
            var game = new BattleOperationSupport(new[] { ship });
            Exception? failure = null;
            try { game.SetSensorControls("unit:42", Changes("tow", "on")); } catch (InvalidOperationException error) { failure = error; }
            Require(failure?.InnerException is OperationException native && native.Code == "sensor_order_rejected" &&
                ship.Telegraph == 0 && ship.StopRequests == 1 && !sonar._requestEnable,
                "The rejected native request hid its preparation side effect or was reported as successful");
        });
        check("Destroyed sensor controls reject both values before native calls", () =>
        {
            var ship = new Vessel(); var radar = new SensorSystemRadar { _sectionName = "search", IsDestroyed = true };
            ship._obp._sensorSystems.Add(radar); ship._obp._searchRadarSystems.Add(radar);
            var game = new BattleOperationSupport(new[] { ship });
            foreach (var value in new[] { "on", "off" }) Reject("sensor_inoperable", () => game.SetSensorControls("unit:42", Changes("search", value)));
            Require(radar.Executed.Count == 0, "A destroyed sensor was sent a native command");
        });
    }
    private static SensorSystemSonar Tow(ObjectBase unit)
    {
        var sonar = new SensorSystemSonar { _sectionName = "tow", IsTowedSensor = true }; sonar.IsOn.Value = false;
        unit._obp._sensorSystems.Add(sonar); unit._obp._towedSonarSystems.Add(sonar); return sonar;
    }
    private static SensorControlChange[] Changes(string id, string value) => new[] { new SensorControlChange(id + ".power", value) };
    private static JObject Order(int target, string id, string value) => new()
    { ["operation"] = SensorExtension.OperationId, ["target_ref"] = "unit:" + target,
      ["arguments"] = new JObject { ["changes"] = new JArray(new JObject { ["control_id"] = id + ".power", ["value"] = value }) } };
    private static AdvisorOperations Service(BattleOperationSupport game)
    {
        var catalog = new AdvisorOperationCatalog(); catalog.Register(new SensorExtension());
        var service = new AdvisorOperations(_ => true);
        foreach (var operation in catalog.CreateOperations(game, new Map())) service.Register(operation);
        service.BeginProposal("test"); return service;
    }
    private sealed class Map : IAdvisorMap
    {
        public void Center(AdvisorMapCoordinate position) => throw new InvalidOperationException("No map mutation expected");
        public IDisposable Draw(System.Collections.Generic.IReadOnlyList<AdvisorMapDrawing> drawings) => throw new InvalidOperationException("No map mutation expected");
    }
    private static void Reject(string code, Action action)
    { try { action(); } catch (OperationException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
