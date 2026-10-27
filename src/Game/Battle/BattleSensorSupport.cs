using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport : IAdvisorSensorSupport
{
    private static string? SensorUnavailableReason(ObjectBase unit) => unit.IsDestroyed || unit.Disabled ? "target_unavailable"
        : !unit.AcceptsOrdersFromPlayer ? "target_not_controllable" : unit._obp == null ? "target_not_ready" : null;
    public IReadOnlyList<SensorUnitInfo> GetSensorUnits()
    {
        Require("battle_units"); Require("battle_sensors");
        return VisibleUnits().Select(unit => new SensorUnitInfo(new GameUnitInfo(unit.Id, unit.Name.Value, null,
            unit.IsAirUnit, unit is Aircraft plane ? plane._isInFlight : unit is Helicopter helicopter && helicopter._isInFlight,
            SensorUnavailableReason(unit)), unit.Emcon.Value, SensorControls(unit).Select(c => c.Info))).ToArray();
    }
    private static IEnumerable<(SensorSystem Sensor, SensorControlInfo Info)> SensorControls(ObjectBase unit)
    {
        if (unit._obp == null) yield break;
        foreach (var sensor in unit._obp._sensorSystems)
        {
            if (sensor.ToggleEnableCommand == null) continue;
            var id = sensor._sectionName;
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("A sensor control has no native section identity.");
            var name = sensor.DisplayName.Value;
            var kind = sensor is SensorSystemRadar ? "radar" : sensor is SensorSystemSonar ? "sonar" : sensor is SensorSystemECM ? "ecm"
                : sensor is SensorSystemESM ? "esm" : sensor is SensorSystemVisual ? "visual" : sensor is SensorSystemMAD ? "mad" : "other";
            var role = sensor is SensorSystemRadar radar ? BattleValue.Code(radar._radar._rp._role)
                : sensor is SensorSystemSonar sonarRole ? BattleValue.Code(sonarRole._sonar._sp._type) : null;
            var function = sensor is SensorSystemRadar radarType ? BattleValue.Code(radarType._type) : null;
            var reason = sensor.Inoperable.Value ? "sensor_inoperable" : null;
            var options = new List<string>();
            if (sensor._allowTurnOn) options.Add("on");
            if (!sensor.IsClipOnArray) options.Add("off");
            yield return (sensor, new SensorControlInfo(id + ".power", name, kind, "power", sensor.IsOn.Value ? "on" : "off", options, reason ?? (options.Count == 0 ? "sensor_control_unavailable" : null), role, function));
            if (sensor is not SensorSystemSonar sonar) continue;
            if (sonar.IsActiveSensor && !sonar._sonar._sp._isActiveOnly)
                yield return (sensor, new SensorControlInfo(id + ".active", name, kind, "active", sonar._sonar.IsActive.Value ? "on" : "off",
                    new[] { "on", "off" }, reason, role));
            if (sonar.IsTowedSensor && !sonar.IsClipOnArray)
                yield return (sensor, new SensorControlInfo(id + ".depth", name, kind, "depth", sonar.SearchBelowLayer.Value ? "below_layer" : "above_layer",
                    new[] { "above_layer", "below_layer" }, reason, role));
        }
    }
    private ObjectBase SensorUnit(string target)
    {
        Require("battle_sensors"); var unit = Unit(target);
        if (SensorUnavailableReason(unit) is string reason) throw new OperationException(409, reason);
        return unit;
    }
    public void SetSensorControls(string target, IReadOnlyList<SensorControlChange> changes)
    {
        var unit = SensorUnit(target); var controls = SensorControls(unit).ToDictionary(c => c.Info.Id, StringComparer.Ordinal);
        var prepared = changes.Select(change =>
        {
            if (!controls.TryGetValue(change.Control, out var control)) throw new OperationException(409, "sensor_control_unavailable");
            if (control.Info.UnavailableReason is string reason) throw new OperationException(409, reason);
            if (!control.Info.Options.Contains(change.Value)) throw new OperationException(409, "sensor_setting_unavailable");
            return (control.Sensor, control.Info.Setting, change.Value);
        }).ToArray();
        var changed = false;
        try
        {
            foreach (var (sensor, setting, value) in prepared)
            {
                if (setting == "power")
                {
                    if (value == "on") sensor.Enable(false, false); else sensor.Disable(false);
                    var accepted = (bool)BattleValue.Required(sensor, value == "on" ? "_requestEnable" : "_requestDisable");
                    if (!accepted) throw new OperationException(409, "sensor_order_rejected");
                }
                else if (setting == "active")
                {
                    var sonar = (SensorSystemSonar)sensor;
                    if (sonar._sonar.IsActive.Value != (value == "on")) sonar.ToggleActiveCommand.Execute(null);
                }
                else sensor.SearchBelowLayer.Value = value == "below_layer";
                changed = true;
            }
        }
        catch (OperationException error) when (changed)
        { throw new InvalidOperationException("Sensor control stopped after earlier changes.", error); }
    }
    public void ApplyEmcon(string target) => SensorUnit(target).setEMCON(true, false);
}
