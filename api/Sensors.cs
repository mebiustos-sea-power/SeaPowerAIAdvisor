using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

// Detached controls from the player's sensor UI; native objects and actuation stay in the game adapter.
public interface IAdvisorSensorSupport
{
    IReadOnlyList<SensorUnitInfo> GetSensorUnits();
    void SetSensorControls(string target, IReadOnlyList<SensorControlChange> changes);
    void ApplyEmcon(string target);
}

public sealed class SensorUnitInfo
{
    public GameUnitInfo Unit { get; }
    public bool Emcon { get; }
    public IReadOnlyList<SensorControlInfo> Controls { get; }
    public SensorUnitInfo(GameUnitInfo unit, bool emcon, IEnumerable<SensorControlInfo> controls)
    { Unit = unit; Emcon = emcon; Controls = new List<SensorControlInfo>(controls).AsReadOnly(); }
}

public sealed class SensorControlInfo
{
    public string Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public string? Role { get; }
    public string? Function { get; }
    public string Setting { get; }
    public string Value { get; }
    public IReadOnlyList<SensorControlOption> Options { get; }
    public string? UnavailableReason { get; }
    public SensorControlInfo(string id, string name, string kind, string setting, string value,
        IEnumerable<SensorControlOption> options, string? unavailableReason = null, string? role = null, string? function = null)
    { Id = id; Name = name; Kind = kind; Setting = setting; Value = value;
        Options = new List<SensorControlOption>(options).AsReadOnly(); UnavailableReason = unavailableReason; Role = role; Function = function; }
}

// Availability is value-specific: a blocked ON must not remove a valid OFF.
// Preparation describes native side effects performed only after adoption.
public sealed class SensorControlOption
{
    public string Value { get; }
    public string? UnavailableReason { get; }
    public string? Preparation { get; }
    public SensorControlOption(string value, string? unavailableReason = null, string? preparation = null)
    { Value = value; UnavailableReason = unavailableReason; Preparation = preparation; }
}

public sealed class SensorControlChange
{
    public string Control { get; }
    public string Value { get; }
    public SensorControlChange(string control, string value) { Control = control; Value = value; }
}
