using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class SensorUiControlTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Battle sensor candidates exclude automatic optical, ESM, fire-control, MAD and ECM power while retaining native search radars", () =>
        {
            var unit = new ObjectBase(); var search = new SensorSystemRadar { _sectionName = "search" };
            var fcr = new SensorSystemRadar { _sectionName = "fcr", _type = "Targeting" };
            unit._obp._sensorSystems.AddRange(new SensorSystem[] { search, fcr, new SensorSystemVisual { _sectionName = "visual" },
                new SensorSystemESM { _sectionName = "esm" }, new SensorSystemECM { _sectionName = "ecm" }, new SensorSystemMAD { _sectionName = "mad" } });
            unit._obp._searchRadarSystems.Add(search);
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Select(c => c.Id).SequenceEqual(new[] { "search.power" }),
                "Generic toggle commands were mistaken for controls exposed by the standard UI");
            foreach (var hidden in new[] { "fcr", "visual", "esm", "ecm", "mad" })
                Reject("sensor_control_unavailable", () => support.SetSensorControls("unit:42", new[] { new SensorControlChange(hidden + ".power", "off") }));
            Require(unit._obp._sensorSystems.All(s => s.Executed.Count == 0), "A hidden control reached a native operation");
        });
        check("Submarine periscopes, ESM and fire-control radar masts remain operable without exposing ordinary optical or ECM power", () =>
        {
            var unit = new Submarine();
            unit._obp._sensorSystems.AddRange(new SensorSystem[] {
                new SensorSystemVisual { _sectionName = "scope", _systemType = BaseSystem.SystemType.SensorPeriscope },
                new SensorSystemVisual { _sectionName = "visual" }, new SensorSystemESM { _sectionName = "esm" },
                new SensorSystemRadar { _sectionName = "mast-fcr", _type = "Targeting" }, new SensorSystemECM { _sectionName = "ecm" } });
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Select(c => c.Id).SequenceEqual(new[] { "scope.power", "esm.power", "mast-fcr.power" }),
                "A ship-wide exclusion removed legitimate submarine mast controls");
            unit._obp._sensorSystems[0].IsOn.Value = false; unit._obp._sensorSystems[3].IsOn.Value = false;
            support.SetSensorControls("unit:42", new[] { new SensorControlChange("scope.power", "on"), new SensorControlChange("mast-fcr.power", "on") });
            Require(unit._obp._sensorSystems[0].Executed.SequenceEqual(new[] { "on" }) && unit._obp._sensorSystems[3].Executed.SequenceEqual(new[] { "on" }),
                "A legitimate mast request did not reach the requested sensor");
        });
        check("Hull sonars expose the standard active switch without exposing a passive receiver power switch", () =>
        {
            var unit = new ObjectBase(); var active = new SensorSystemSonar { _sectionName = "active" };
            var activeOnly = new SensorSystemSonar { _sectionName = "active-only" }; activeOnly._sonar._sp._isActiveOnly = true;
            var passive = new SensorSystemSonar { _sectionName = "passive" };
            unit._obp._sensorSystems.AddRange(new[] { active, activeOnly, passive });
            unit._obp._activeSonarSystems.AddRange(new[] { active, activeOnly });
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Select(c => c.Id).SequenceEqual(new[] { "active.active", "active-only.active" }),
                "Hull sonar power was exposed, or a native active-only UI switch was removed");
            support.SetSensorControls("unit:42", new[] { new SensorControlChange("active.active", "on") });
            Require(active._sonar.IsActive.Value && active.Executed.Count == 0 && passive.Executed.Count == 0,
                "An active-mode change switched receiver power or another sonar");
        });
        check("Towed and dipping UI controls distinguish deployment, layer and active mode including fixed clip-on arrays", () =>
        {
            var unit = new ObjectBase(); var dipping = new SensorSystemSonar { _sectionName = "dip", IsActiveSensor = true };
            var passive = new SensorSystemSonar { _sectionName = "tow" };
            var clip = new SensorSystemSonar { _sectionName = "clip", IsClipOnArray = true, IsActiveSensor = true };
            unit._obp._sensorSystems.AddRange(new[] { dipping, passive, clip });
            unit._obp._towedSonarSystems.AddRange(new[] { dipping, passive, clip });
            unit._obp._activeSonarSystems.Add(dipping);
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Select(c => c.Id).SequenceEqual(new[] {
                "dip.power", "dip.active", "dip.depth", "tow.power", "tow.depth", "clip.active", "clip.depth" }),
                "Sonar groups advertised hidden power/modes or lost an available layer selector");
            Reject("sensor_control_unavailable", () => support.SetSensorControls("unit:42", new[] { new SensorControlChange("clip.power", "off") }));
            support.SetSensorControls("unit:42", new[] { new SensorControlChange("tow.depth", "below_layer") });
            Require(passive.SearchBelowLayer.Value && clip.Executed.Count == 0, "Layer selection altered clip-on deployment");
            var submarine = new Submarine();
            var subTow = new SensorSystemSonar { _sectionName = "sub-tow", IsActiveSensor = true };
            var subClip = new SensorSystemSonar { _sectionName = "sub-clip", IsClipOnArray = true };
            submarine._obp._sensorSystems.AddRange(new[] { subTow, subClip });
            submarine._obp._towedSonarSystems.AddRange(new[] { subTow, subClip });
            var subSupport = new BattleOperationSupport(new[] { submarine });
            Require(subSupport.GetSensorUnits().Single().Controls.Select(c => c.Id).SequenceEqual(new[] { "sub-tow.power" }),
                "Submarine towed sonar exposed the surface-unit layer or active submenu");
            Reject("sensor_control_unavailable", () => subSupport.SetSensorControls("unit:42", new[] { new SensorControlChange("sub-tow.depth", "below_layer") }));
        });
        check("Native sensor preflight reevaluates UI membership and validates the complete change batch before any switch", () =>
        {
            var unit = new ObjectBase(); var radar = new SensorSystemRadar { _sectionName = "radar" };
            var ecm = new SensorSystemECM { _sectionName = "ecm" };
            unit._obp._sensorSystems.AddRange(new SensorSystem[] { radar, ecm }); unit._obp._searchRadarSystems.Add(radar);
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Count == 1, "Search radar was unavailable before its UI classification changed");
            Reject("sensor_control_unavailable", () => support.SetSensorControls("unit:42", new[] {
                new SensorControlChange("radar.power", "on"), new SensorControlChange("ecm.power", "off") }));
            Require(radar.Executed.Count == 0 && ecm.Executed.Count == 0, "An invalid later control allowed a partial native change");
            unit._obp._searchRadarSystems.Clear();
            Reject("sensor_control_unavailable", () => support.SetSensorControls("unit:42", new[] { new SensorControlChange("radar.power", "on") }));
            Require(support.GetSensorUnits().Single().Controls.Count == 0 && radar.Executed.Count == 0, "Execution reused stale candidate permissions");
        });
        check("UI-visible controls still enforce damage, startup restrictions and command availability while preserving EMCON semantics", () =>
        {
            var unit = new ObjectBase(); var radar = new SensorSystemRadar { _sectionName = "radar", _allowTurnOn = false };
            var sonar = new SensorSystemSonar { _sectionName = "sonar" }; sonar.ActiveAvailable = false;
            unit._obp._sensorSystems.AddRange(new SensorSystem[] { radar, sonar }); unit._obp._searchRadarSystems.Add(radar);
            unit._obp._activeSonarSystems.Add(sonar);
            var support = new BattleOperationSupport(new[] { unit });
            var controls = support.GetSensorUnits().Single().Controls;
            Require(controls[0].Options.Where(o => o.UnavailableReason == null).Select(o => o.Value).SequenceEqual(new[] { "off" }) &&
                controls[1].Options.All(o => o.UnavailableReason == "sensor_control_unavailable"), "UI eligibility removed live control restrictions");
            Reject("sensor_setting_unavailable", () => support.SetSensorControls("unit:42", new[] { new SensorControlChange("radar.power", "on") }));
            radar.Inoperable.Value = true;
            Reject("sensor_inoperable", () => support.SetSensorControls("unit:42", new[] { new SensorControlChange("radar.power", "off") }));
            support.ApplyEmcon("unit:42");
            Require(unit.EmconRequests == 1 && radar.Executed.Count == 0, "EMCON was expanded into direct sensor power changes");
        });
        check("Native UI classification supports newly added search radar types without a display-name allowlist", () =>
        {
            var unit = new ObjectBase(); var radar = new AddedRadar { _sectionName = "added" };
            unit._obp._sensorSystems.Add(radar); unit._obp._searchRadarSystems.Add(radar);
            var support = new BattleOperationSupport(new[] { unit });
            Require(support.GetSensorUnits().Single().Controls.Single().Id == "added.power", "An added search radar required a hard-coded platform or equipment name");
        });
    }
    private sealed class AddedRadar : SensorSystemRadar { }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (OperationException error) when (error.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
}

// Native-shape fixtures let the actual battle adapter run offline; commands and UI classification are not reimplemented here.
namespace SeaPower
{
    internal sealed class NativeValue<T> { public T Value { get; set; } public NativeValue(T value) { Value = value; } }
    internal class ObjectBase
    {
        public int Id = 42;
        public NativeValue<string> Name = new("Unit");
        public bool IsDestroyed { get; set; }
        public bool Disabled { get; set; }
        public bool AcceptsOrdersFromPlayer = true;
        public bool IsAirUnit { get; set; }
        public ObjectBaseParameters _obp = new();
        public NativeValue<bool> Emcon = new(true);
        public NativeValue<float> DesiredAltitude = new(1);
        public NativeValue<GeoPosition> Position = new(new());
        public bool SpeedValid = true, AltitudeValid = true, NearBottom;
        public int Telegraph, StopRequests;
        public int getTelegraph() => Telegraph;
        public void setTelegraph(int value) { Telegraph = value; StopRequests++; }
        public int EmconRequests;
        public void setEMCON(bool enabled, bool formation) { EmconRequests++; Emcon.Value = enabled; }
    }
    internal sealed class Submarine : ObjectBase
    {
        public NativeValue<bool> IsSubmerged = new(false);
        public bool BelowPeriscope;
        public bool isBelowPeriscopeDepth() => BelowPeriscope;
    }
    internal sealed class Vessel : ObjectBase { }
    internal sealed class Aircraft : ObjectBase { public bool _isInFlight = true; }
    internal sealed class Helicopter : ObjectBase { public bool _isInFlight = true; }
    internal sealed class ObjectBaseParameters
    {
        public readonly List<SensorSystem> _sensorSystems = new();
        public readonly List<SensorSystemRadar> _searchRadarSystems = new();
        public readonly List<SensorSystemSonar> _activeSonarSystems = new(), _towedSonarSystems = new();
    }
    internal class BaseSystem
    {
        public enum SystemType { Generic, SensorPeriscope }
        public SystemType _systemType;
        public bool IsDestroyed;
        public sealed class TowedDeployConditions { public float _minDeployDepth = -1; }
        protected TowedDeployConditions _towedDeployConditions = new();
        public bool isTowedSystemSpeedWithinDeployConditions(ObjectBase unit) => unit.SpeedValid;
        public bool isTowedSystemAltitudeWithinDeployConditions(ObjectBase unit) => unit.AltitudeValid;
        public bool isSurfacedSubmarine(ObjectBase unit) => unit is Submarine s && !s.IsSubmerged.Value;
        public bool isSubmarineNearBottom(ObjectBase unit) => unit.NearBottom;
        public NativeValue<string> DisplayName = new("Sensor");
        public NativeValue<bool> IsOn = new(true), Inoperable = new(false);
    }
    internal class SensorSystem : BaseSystem
    {
        public string _sectionName = "sensor";
        public bool _allowTurnOn = true, _requestEnable, _requestDisable, _wasSetOn, _isDippingSonar, IsTowedSensor;
        public bool IsClipOnArray, IsActiveSensor, RejectEnable;
        public NativeValue<bool> SearchBelowLayer = new(false);
        public readonly List<string> Executed = new();
        public System.Windows.Input.ICommand ToggleEnableCommand => new NativeCommand(() => { });
        public void Enable(bool message, bool force)
        {
            if (RejectEnable) return;
            if (_wasSetOn && !IsOn.Value) { Disable(false); return; }
            _requestEnable = true; _requestDisable = false; _wasSetOn = true; Executed.Add("on");
        }
        public void Disable(bool message) { _requestEnable = false; _requestDisable = true; _wasSetOn = false; Executed.Add("off"); }
    }
    internal class SensorSystemRadar : SensorSystem
    {
        public Radar _radar = new(); public string _type = "Search";
    }
    internal sealed class Radar { public RadarParameters _rp = new(); }
    internal sealed class RadarParameters { public string _role = "SurfaceAndAir"; }
    internal sealed class SensorSystemSonar : SensorSystem
    {
        public Sonar _sonar = new(); public bool ActiveAvailable = true;
        public System.Windows.Input.ICommand ToggleActiveCommand => new NativeCommand(() => _sonar.IsActive.Value = !_sonar.IsActive.Value, ActiveAvailable);
    }
    internal sealed class Sonar { public SonarParameters _sp = new(); public NativeValue<bool> IsActive = new(false); }
    internal sealed class SonarParameters { public string _type = "HullMounted"; public bool _isActiveOnly; }
    internal sealed class SensorSystemVisual : SensorSystem { }
    internal sealed class SensorSystemESM : SensorSystem { }
    internal sealed class SensorSystemECM : SensorSystem { }
    internal sealed class SensorSystemMAD : SensorSystem { }
    internal sealed class Environment { public static Environment Instance = new(); public int SeaState; }
    internal sealed class GeoPosition { public float Terrain = -10; }
    internal static class TerrainUtils { public static float GetHeightAt(GeoPosition position, bool a, bool b) => position.Terrain; }
    internal sealed class NativeCommand : System.Windows.Input.ICommand
    {
        private readonly Action action; private readonly bool available;
        public NativeCommand(Action action, bool available = true) { this.action = action; this.available = available; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => available;
        public void Execute(object? parameter) => action();
    }
}
namespace SeaPowerAIAdvisor
{
    internal sealed partial class BattleOperationSupport : IAdvisorGameSupport
    {
        public bool IsInformationEnabled(string category) => true;
        public IReadOnlyList<GameUnitInfo> GetUnits() => GetSensorUnits().Select(u => u.Unit).ToArray();
        public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target) => throw new NotSupportedException();
        public ReservationRoute? GetReservationRoute(string target) => throw new NotSupportedException();
        public string GetSonobuoyDepth(string target) => throw new NotSupportedException();
        public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position) => throw new NotSupportedException();
        private readonly IReadOnlyList<SeaPower.ObjectBase> testUnits;
        internal BattleOperationSupport(IReadOnlyList<SeaPower.ObjectBase> units) { testUnits = units; }
        private void Require(string category) { }
        private IEnumerable<SeaPower.ObjectBase> VisibleUnits() => testUnits;
        private SeaPower.ObjectBase Unit(string target) => testUnits.Single(u => "unit:" + u.Id == target);
    }
}
