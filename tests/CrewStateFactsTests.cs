using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

internal static class CrewStateFactsTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Crew weather preserves simultaneous conditions and rejects invalid wind-force values", () =>
        {
            foreach (var force in new[] { 0, 5, 12 })
                foreach (var rain in new[] { false, true })
                    foreach (var snow in new[] { false, true })
                    {
                        var weather = new Weather { SeaState = force, IsRaining = rain, IsSnowing = snow, IsFog = snow, IsLightning = rain };
                        var data = BattleWeatherFacts.Capture(weather);
                        var precipitation = data["precipitation"]!.Values<string>().ToArray();
                        Require((int)data["wind_force_beaufort"]! == force && precipitation.Contains("rain") == rain && precipitation.Contains("snow") == snow &&
                            precipitation.Length == (rain ? 1 : 0) + (snow ? 1 : 0) && (bool)data["fog"]! == snow && (bool)data["lightning"]! == rain,
                            "Weather conditions were inferred, conflated or lost");
                        weather.SeaState = 1; weather.IsRaining = !rain;
                        Require((int)data["wind_force_beaufort"]! == force && precipitation.Contains("rain") == rain, "Native changes mutated the weather snapshot");
                    }
            foreach (var force in new[] { -1, 13 })
            {
                try { BattleWeatherFacts.Capture(new Weather { SeaState = force }); }
                catch (InvalidOperationException) { continue; }
                throw new InvalidOperationException("An invalid wind-force value was silently accepted");
            }
        });
        check("Crew fire uses the most severe current compartment state and omits unsupported units", () =>
        {
            Require(UnitStatusFacts.FireSeverity(new Unit()) == null, "A unit without compartments was described as fire-free");
            var expected = new[] { "none", "self_extinguishing", "moderate", "severe" };
            for (var i = 0; i < expected.Length; i++)
            {
                var unit = new Unit { Compartments = new Compartments { _systemCompartments = Enumerable.Range(0, i + 1)
                    .Select(value => new Compartment { FireState = (Fire)value }).ToArray() } };
                Require(UnitStatusFacts.FireSeverity(unit) == expected[i], "Compartment fire severity was not aggregated correctly");
                foreach (var compartment in unit.Compartments._systemCompartments) compartment.FireState = Fire.No;
                Require(UnitStatusFacts.FireSeverity(unit) == "none", "An extinguished fire persisted");
            }
            try { UnitStatusFacts.FireSeverity(new Unit { Compartments = new Compartments { _systemCompartments = new[] { new Compartment { FireState = (Fire)99 } } } }); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("An unknown compartment state was silently accepted");
        });
        check("Crew air operations retain independent launch and recovery handling and omit non-hosts", () =>
        {
            Require(UnitStatusFacts.AirOperations(new Parameters()) == null, "A unit without a deck received an operations state");
            foreach (var launch in new[] { false, true })
                foreach (var recovery in new[] { false, true })
                {
                    var deck = new Deck { LaunchInProgress = launch, LandingInProgress = recovery };
                    var data = UnitStatusFacts.AirOperations(new Parameters { _flightDeck = deck })!;
                    Require((bool)data["launch_handling"]! == launch && (bool)data["recovery_handling"]! == recovery,
                        "Launch and recovery handling were conflated");
                    deck.LaunchInProgress = !launch;
                    Require((bool)data["launch_handling"]! == launch, "Native changes mutated the handling snapshot");
                }
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Weather
    {
        public int SeaState { get; set; }
        public bool IsRaining { get; set; }
        public bool IsSnowing { get; set; }
        public bool IsFog { get; set; }
        public bool IsLightning { get; set; }
    }
    private sealed class Unit { public Compartments? Compartments { get; set; } }
    private sealed class Compartments
    {
        public Compartment[] _systemCompartments { get; set; } = Array.Empty<Compartment>();
        public int _firesCount => throw new InvalidOperationException("Fire display-region capacity was read as an active fire count");
    }
    private enum Fire { No, SelfExtinguishing, Moderate, Severe }
    private sealed class Compartment { public Fire FireState { get; set; } }
    private sealed class Parameters { public Deck? _flightDeck { get; set; } }
    private sealed class Deck
    {
        public bool LaunchInProgress { get; set; }
        public bool LandingInProgress { get; set; }
        public int AircraftCurrentlyOnBoard => throw new InvalidOperationException("Crew read an unnecessary aircraft inventory");
    }
}
