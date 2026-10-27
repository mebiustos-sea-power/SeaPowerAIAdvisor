using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunAircraftCruise(Action<string, Action> check)
    {
        check("Economic cruise pairs use each aircraft's native preset at its cruise altitude", () =>
        {
            var calls = 0;
            foreach (var sample in new[] { (Altitude: 25000f, Mach: 0.57f, Sound: 310f), (Altitude: 30000f, Mach: 0.54f, Sound: 300f) })
            {
                var parameters = new { _cruiseAltitudeFeet = sample.Altitude,
                    _speedValuesInMach = new[] { 0.2f, 0.3f, sample.Mach, 0.9f }, _cruiseMach = 0.99f };
                var data = BattleAircraftCruise.Capture(parameters, height =>
                {
                    calls++;
                    Require(Math.Abs(height - sample.Altitude * 0.3048) < 0.01, "Speed conversion used the current altitude or feet instead of cruise metres");
                    return sample.Sound;
                });
                Require((double?)data["altitude_ft"] == sample.Altitude &&
                    (double?)data["speed_knots"] == Math.Round((double)sample.Mach * sample.Sound * 1.94384, 2),
                    "Cruise performance used another aircraft's values or the separate cruise Mach field");
            }
            Require(calls == 2, "Cruise conversion did not use the game conversion boundary");
        });
        check("Cruise references remain frozen, respect fuel selection and reach every situation delivery mode", () =>
        {
            var f = new Fixture(); f.Own._type = "Aircraft";
            f.OwnUnits.Add(new OwnUnit { _type = "Helicopter", _obp = new Parameters { _speedValuesInMach = Array.Empty<float>() } });
            f.OwnUnits.Add(new OwnUnit());
            var snapshot = f.Capture();
            f.Own._obp._cruiseAltitudeFeet = 1000; f.Own._obp._speedValuesInMach[2] = 0.9f;
            foreach (var enabled in new[] { true, false })
            foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var knowledge = new BattleQuerySession(snapshot, "en", "Fuel-efficient transit", information:
                    AdvisorInformation.All.With(InformationKind.BattleFuel, enabled));
                var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>());
                var input = context.PrepareStandardInput(mode);
                JArray rows;
                if (mode == AdvisorSituationDelivery.OnDemand)
                    rows = (JArray)knowledge.Execute(Request(knowledge, new JObject { ["query"] = "get_unit_capabilities",
                        ["ids"] = new JArray("own_unit_1", "own_unit_2", "own_unit_3") }))["results"]![0]!["data"]!["units"]!;
                else
                {
                    var full = mode == AdvisorSituationDelivery.Upfront ? (JArray)input.Data["situation_data"]! :
                        (JArray)context.Reads.ExecuteAsync(new JObject { ["requests"] = new JArray(new JObject {
                            ["source"] = "situation", ["query"] = "get_situation" }) }, CancellationToken.None)
                            .GetAwaiter().GetResult()["results"]![0]!["data"]!["situation_data"]!;
                    rows = (JArray)full.Single(r => (string?)r["query"] == "get_unit_capabilities")["data"]!["units"]!;
                }
                var plane = rows.Single(r => (string?)r["id"] == "own_unit_1");
                if (enabled)
                    Require((double?)plane["fuel"]?["economic_cruise"]?["altitude_ft"] == 25000 &&
                        (double?)plane["fuel"]?["economic_cruise"]?["speed_knots"] == 332.4 &&
                        rows.Where(r => (string?)r["id"] != "own_unit_1").All(r => r["fuel"]?["economic_cruise"] == null),
                        "Cruise references changed after capture, were missing from delivery, or leaked to other unit kinds");
                else Require(rows.All(r => r["fuel"] == null), "Disabling fuel still exposed cruise performance");
            }
        });
        check("Malformed cruise parameters fail visibly instead of producing an invented reference", () =>
        {
            foreach (var parameters in new object[] {
                new { _cruiseAltitudeFeet = 25000f, _speedValuesInMach = Array.Empty<float>() },
                new { _cruiseAltitudeFeet = -1f, _speedValuesInMach = new[] { 0.1f, 0.2f, 0.3f } },
                new { _cruiseAltitudeFeet = float.NaN, _speedValuesInMach = new[] { 0.1f, 0.2f, 0.3f } },
                new { _cruiseAltitudeFeet = 25000f, _speedValuesInMach = new[] { 0.1f, 0.2f, 0f } },
                new { _cruiseAltitudeFeet = 25000f, _speedValuesInMach = new[] { 0.1f, 0.2f, float.PositiveInfinity } } })
            {
                try { _ = BattleAircraftCruise.Capture(parameters, _ => 300); }
                catch (InvalidOperationException) { continue; }
                throw new Exception("Invalid cruise performance was accepted");
            }
        });
    }
}
