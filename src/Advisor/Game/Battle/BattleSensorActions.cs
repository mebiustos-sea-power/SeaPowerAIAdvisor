using System;
using System.Collections.Generic;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Physical conditions and UI preparation belong to the game adapter. Reads never call native commands.
internal static class BattleSensorActions
{
    // SensorSystem.Enable uses this native deployment altitude for helicopter towed sonars.
    private const float HelicopterDeployAltitude = 0.1814284f;
    private const float TowedClearance = 0.2267855f;

    internal static IReadOnlyList<SensorControlOption> Options(ObjectBase unit, SensorSystem sensor, string setting)
    {
        var values = setting == "depth" ? new[] { "above_layer", "below_layer" } : new[] { "on", "off" };
        var result = new List<SensorControlOption>();
        foreach (var value in values)
        {
            string? reason = null, preparation = null;
            var activation = setting == "depth" || setting == "power" && value == "on";
            if (setting == "active")
            {
                if (sensor is not SensorSystemSonar sonar || sonar.ToggleActiveCommand?.CanExecute(null) != true)
                    reason = "sensor_control_unavailable";
                else if (value == "on" && !sensor.IsOn.Value)
                    reason = "sensor_not_deployed";
            }
            if (activation && !sensor._allowTurnOn) reason = "sensor_setting_unavailable";
            if (activation && reason == null && !RequestedOn(sensor))
            {
                reason = EnableUnavailableReason(unit, sensor);
                if (reason == null)
                {
                    if (sensor.IsTowedSensor && !sensor.IsClipOnArray && unit is Helicopter)
                        preparation = "hover_at_deployment_altitude";
                    else if (setting == "power" && sensor._isDippingSonar && unit is Vessel && unit.getTelegraph() > 0)
                        preparation = "stop_for_deployment";
                    else if (setting == "depth") preparation = "deploy_sensor";
                }
            }
            result.Add(new SensorControlOption(value, reason, preparation));
        }
        return result;
    }

    // IsOn can stay true while shutting down; use the native intent when deciding whether ON is already requested.
    private static bool RequestedOn(SensorSystem sensor) =>
        !Flag(sensor, "_requestDisable") && (sensor._wasSetOn || Flag(sensor, "_requestEnable"));
    private static bool Flag(SensorSystem sensor, string name) => (bool)BattleValue.Required(sensor, name);

    private static string? EnableUnavailableReason(ObjectBase unit, SensorSystem sensor)
    {
        if (sensor.IsDestroyed || sensor.Inoperable.Value) return "sensor_inoperable";
        if (!sensor._allowTurnOn) return "sensor_setting_unavailable";
        if (BattleSensorUiControls.IsMast(unit, sensor) && unit is Submarine submarine && submarine.IsSubmerged.Value)
        {
            if (submarine.isBelowPeriscopeDepth()) return "mast_below_periscope_depth";
            if (SeaPower.Environment.Instance.SeaState > 5) return "mast_sea_state_too_high";
        }
        if (!sensor.IsTowedSensor || sensor.IsClipOnArray) return null;
        // The native shallow-water helper reads Globals._currentGeoPosition instead of its argument.
        // Use the requested unit's position; never change selection or global map state to query it.
        var conditions = BattleValue.Required(sensor, "_towedDeployConditions");
        var minimum = BattleValue.Number(BattleValue.Required(conditions, "_minDeployDepth"));
        var terrain = BattleValue.Number(TerrainUtils.GetHeightAt(unit.Position.Value, false, false));
        if (terrain > minimum - TowedClearance) return "sensor_water_too_shallow";
        if (sensor.isSurfacedSubmarine(unit)) return "sensor_requires_submerged_unit";
        if (sensor.isSubmarineNearBottom(unit)) return "sensor_too_close_to_seabed";
        // The helicopter controller retains the deployment intent and prepares hover/descent itself.
        if (unit is Helicopter) return null;
        if (!sensor.isTowedSystemSpeedWithinDeployConditions(unit)) return "sensor_deployment_speed";
        if (!sensor.isTowedSystemAltitudeWithinDeployConditions(unit)) return "sensor_deployment_altitude";
        return null;
    }

    internal static void Apply(ObjectBase unit, SensorSystem sensor, string setting, string value)
    {
        if (setting == "power")
        {
            if (value == "on") Enable(unit, sensor, true);
            else if (sensor.IsOn.Value || sensor._wasSetOn || Flag(sensor, "_requestEnable"))
            {
                sensor.Disable(false);
                if (!Flag(sensor, "_requestDisable")) throw new OperationException(409, "sensor_order_rejected");
            }
        }
        else if (setting == "active")
        {
            var sonar = (SensorSystemSonar)sensor;
            if (sonar._sonar.IsActive.Value != (value == "on")) sonar.ToggleActiveCommand.Execute(null);
            if (sonar._sonar.IsActive.Value != (value == "on")) throw new OperationException(409, "sensor_order_rejected");
        }
        else
        {
            Enable(unit, sensor, false);
            sensor.SearchBelowLayer.Value = value == "below_layer";
        }
    }

    private static void Enable(ObjectBase unit, SensorSystem sensor, bool stopShip)
    {
        // Reissuing Enable while a dipping sonar is preparing can cancel the native request.
        if (RequestedOn(sensor)) return;
        if (stopShip && sensor._isDippingSonar && unit is Vessel && unit.getTelegraph() > 0)
            unit.setTelegraph(0);
        if (sensor.IsTowedSensor && !sensor.IsClipOnArray && unit is Helicopter)
            unit.DesiredAltitude.Value = HelicopterDeployAltitude;
        sensor.Enable(false, false);
        if (!Flag(sensor, "_requestEnable")) throw new OperationException(409, "sensor_order_rejected");
    }
}
