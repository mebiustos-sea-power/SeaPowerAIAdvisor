using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunMissionAreas(Action<string, Action> check)
    {
        check("Mission circles disclose only displayed labels and geometry, including duplicate and absent labels", () =>
        {
            var f = new Fixture();
            f.MissionAreas.Add(new HiddenMissionArea());
            f.MissionAreas.Add(new MissionArea());
            f.MissionAreas.Add(new MissionArea { Position = new AreaPosition { Latitude = 25, Longitude = 130 }, Radius = 27.5595f });
            f.MissionAreas.Add(new MissionArea { Label = null, Radius = 0 });
            var areas = (JArray)f.Capture().Copy()["mission_map_areas"]!;
            Require(areas.Count == 3 && (string?)areas[0]!["display_text"] == "上陸を阻止せよ！" &&
                (string?)areas[0]!["shape"] == "circle", "A hidden area leaked or its label/shape changed: " + areas);
            Require((double?)areas[0]!["center"]!["latitude_deg"] == 26.2 &&
                (double?)areas[0]!["center"]!["longitude_deg"] == 127.7 &&
                Math.Abs((double)areas[0]!["radius_nm"]! - 5) < 0.0001,
                "Displayed geometry changed coordinates or units: " + areas[0]);
            Require((double?)areas[1]!["center"]!["latitude_deg"] == 25 &&
                Math.Abs((double)areas[1]!["radius_nm"]! - 1) < 0.0001,
                "Identically labelled circles were merged: " + areas[1]);
            Require((string?)areas[2]!["display_text"] == null && (double?)areas[2]!["radius_nm"] == 0,
                "An unlabelled circle was dropped or invented a radius: " + areas[2]);
        });
        check("Mission circles freeze for a consultation and newly displayed or moved circles appear in the next capture", () =>
        {
            var f = new Fixture(); var first = new MissionArea(); var later = new MissionArea { Visible = false };
            f.MissionAreas.Add(first); f.MissionAreas.Add(later);
            var captured = f.Capture();
            first.Label = "移動後"; first.Position.Latitude = 27; first.Radius = 27.5595f;
            later.Visible = true;
            f.MissionAreas.Add(new MissionArea());
            var frozen = (JArray)captured.Copy()["mission_map_areas"]!;
            var current = (JArray)f.Capture().Copy()["mission_map_areas"]!;
            Require(frozen.Count == 1 && (string?)frozen[0]!["display_text"] == "上陸を阻止せよ！" &&
                (double?)frozen[0]!["center"]!["latitude_deg"] == 26.2 &&
                Math.Abs((double)frozen[0]!["radius_nm"]! - 5) < 0.0001 &&
                current.Count == 3 && (string?)current[0]!["display_text"] == "移動後" &&
                (double?)current[0]!["center"]!["latitude_deg"] == 27,
                "A live change mutated an earlier consultation or newly displayed areas were not refreshed");
            first.Visible = false;
            Require(f.Capture().Copy()["mission_map_areas"]!.Count() == 2, "A no-longer-displayed circle remained available");
        });
        check("Mission circles remain basic information and follow all three situation-delivery modes", () =>
        {
            var f = new Fixture(); f.MissionAreas.Add(new MissionArea()); var snapshot = f.Capture();
            var expected = snapshot.Copy()["mission_map_areas"]!;
            var none = AdvisorInformation.Items.Aggregate(AdvisorInformation.All, (policy, item) => policy.With(item.Kind, false));
            foreach (var mode in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var knowledge = new BattleQuerySession(snapshot, "en", "Mission area?", information: none);
                var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>());
                var input = context.PrepareStandardInput(mode);
                if (mode != AdvisorSituationDelivery.OnRequest)
                {
                    Require(JToken.DeepEquals(input.Data["situation"]?["mission_map_areas"], expected),
                        "Optional information selections removed basic mission areas");
                    continue;
                }
                Require(input.Data["situation"] == null && !input.Data.ToString().Contains("上陸を阻止せよ！", StringComparison.Ordinal),
                    "Requested delivery disclosed mission areas before a situation request");
                var call = new AiToolCall("areas", "get_situation", new JObject());
                var result = context.Tools.ExecuteAsync(new[] { call }, CancellationToken.None, (_, _) => { }, (_, _) => { }).GetAwaiter().GetResult();
                var full = result.Results.Single().Data["results"]?[0]?["data"];
                Require(JToken.DeepEquals(full?["situation"]?["mission_map_areas"], expected), "The requested full snapshot lost mission areas");
                var repeated = context.Tools.ExecuteAsync(new[] { call }, CancellationToken.None, (_, _) => { }, (_, _) => { }).GetAwaiter().GetResult();
                Require((string?)repeated.Results.Single().Data["results"]?[0]?["data"]?["status"] == "already_supplied",
                    "A repeated situation request duplicated the mission data");
            }
        });
        check("Invalid displayed mission geometry fails visibly instead of dropping a circle or inventing coordinates", () =>
        {
            var invalidLatitude = new MissionArea { Position = new AreaPosition { Latitude = 91 } };
            foreach (var area in new[] { invalidLatitude, new MissionArea { Radius = -1 }, new MissionArea { Radius = float.NaN } })
            {
                try { _ = BattleMissionAreaReader.Capture(new[] { area }); }
                catch (InvalidOperationException) { continue; }
                throw new InvalidOperationException("Invalid displayed geometry was silently accepted");
            }
        });
    }

    private sealed class AreaPosition
    {
        public double Latitude { get; set; } = 26.2;
        public double Longitude { get; set; } = 127.7;
    }
    private sealed class MissionArea
    {
        public bool Visible { get; set; } = true;
        public string? Label { get; set; } = "上陸を阻止せよ！";
        public AreaPosition Position { get; set; } = new();
        public float Radius { get; set; } = 137.7975f;
        public object ParentTrigger => throw new Exception("Read hidden trigger");
        public object PositionAnchor => throw new Exception("Read true anchor state");
        public bool Completed => throw new Exception("Read hidden completion state");
    }
    private sealed class HiddenMissionArea
    {
        public bool Visible => false;
        public string Label => throw new Exception("Read hidden area label");
        public object Position => throw new Exception("Read hidden area position");
        public float Radius => throw new Exception("Read hidden area radius");
    }
}
