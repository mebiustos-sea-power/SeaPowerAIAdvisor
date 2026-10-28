using System;
using SeaPower;

namespace SeaPowerAIAdvisor;

[Flags]
internal enum SensorUiControl { None = 0, Power = 1, Active = 2, Depth = 4 }

// The control groups follow ObjectSensorViewModel.GetContextMenuItems, not the presence of a generic toggle command.
internal static class BattleSensorUiControls
{
    internal static bool IsMast(ObjectBase unit, SensorSystem sensor) => unit is Submarine &&
        (sensor._systemType == BaseSystem.SystemType.SensorPeriscope || sensor.GetType() == typeof(SensorSystemRadar) || sensor.GetType() == typeof(SensorSystemESM));

    internal static SensorUiControl Get(ObjectBase unit, SensorSystem sensor)
    {
        var controls = SensorUiControl.None;
        var parameters = unit._obp;
        if (sensor is SensorSystemRadar search && parameters._searchRadarSystems.Contains(search))
            controls |= SensorUiControl.Power;
        if (IsMast(unit, sensor))
            controls |= SensorUiControl.Power;
        if (sensor is SensorSystemSonar sonar)
        {
            if (parameters._towedSonarSystems.Contains(sonar))
            {
                if (!sonar.IsClipOnArray) controls |= SensorUiControl.Power;
                if (unit is not Submarine)
                {
                    controls |= SensorUiControl.Depth;
                    if (sonar.IsActiveSensor) controls |= SensorUiControl.Active;
                }
            }
            else if (parameters._activeSonarSystems.Contains(sonar))
                controls |= SensorUiControl.Active;
        }
        return controls;
    }
}
