using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static SeaPowerAIAdvisor.InformationKind;
using static SituationTests;

internal static partial class InformationTests
{
    private static void RunPersistence(Action<string, Action> check)
    {
        check("Explicit data choices survive saving, legacy migration and disabled-parent round trips", () =>
        {
            var old = AdvisorOptions.Parse("{\"disabled_information\":[\"battle_weapons\",\"campaign_forces\"]}");
            Require(!old.Information.Allows(BattleWeapons) && !old.Information.Allows(CampaignShips) &&
                old.Information.Allows(BattleBases) && old.Information.Allows(BattleGroundUnits) && old.Information.Allows(BattleAviation),
                "Legacy choices changed or new categories did not receive their defaults");
            var selected = old.Information.With(BattleBases, false).With(BattleAviation, true).With(BattleUnits, false);
            var data = new AdvisorOptions(information:selected).Data();
            var json = JObject.Parse(Encoding.UTF8.GetString(data));
            Require(json["disabled_information"] == null && (bool?)json["information_selection"]?["battle_bases"] == false &&
                (bool?)json["information_selection"]?["battle_aviation"] == true, "Explicit on/off or inactive child choices were not recorded");
            var restored = AdvisorOptions.Parse(Encoding.UTF8.GetString(data)).Information.With(BattleUnits, true);
            Require(!restored.Allows(BattleBases) && restored.Allows(BattleAviation) && !restored.Allows(BattleWeapons),
                "Reopening or enabling a parent overwrote a saved child choice");
            var partial = AdvisorOptions.Parse("{\"information_selection\":{\"battle_bases\":false,\"battle_aviation\":true}}").Information;
            Require(!partial.Allows(BattleBases) && partial.Allows(BattleAviation) && partial.Allows(BattleGroundUnits),
                "Missing new selections replaced explicit choices instead of using item defaults");
        });
        check("Malformed explicit information choices and conflicting legacy formats are reported", () =>
        {
            foreach (var json in new[] {
                "{\"information_selection\":null}", "{\"information_selection\":[]}",
                "{\"information_selection\":{\"battle_bases\":\"false\"}}",
                "{\"information_selection\":{\"unknown\":true}}",
                "{\"information_selection\":{\"battle_bases\":true,\"battle_bases\":false}}",
                "{\"information_selection\":{},\"disabled_information\":[]}" })
            {
                try { _ = AdvisorOptions.Parse(json); }
                catch (AdvisorOptionsException e) when (e.Code is "information_invalid" or "options_invalid") { continue; }
                throw new InvalidOperationException("Malformed selection silently reset the user's preferences");
            }
        });
    }
    private static void RunGroundCampaign(string campaignPath, Action<string, Action> check)
    {
        check("Campaign ground/base capture excludes enemies, freezes own stocks and honors independent selections", () =>
        {
            var f = new Fixture(Assembly.LoadFrom(campaignPath));
            var stock = f.New("Core.State.BaseStock"); Set(stock,"ShipFuelT",100d); Set(stock,"JetFuelT",40d);
            Dictionary(stock,"Ammo").Add("known-ammo",12); Dictionary(f.State,"Stocks").Add("own-port",stock);
            Dictionary(f.State,"Garrisons").Add("own-port",25d);
            var unit = f.New("Core.State.GroundUnit"); Set(unit,"SideId","BLUE"); Set(unit,"Name","味方地上部隊");
            Set(unit,"BaseId","own-port"); Set(unit,"Strength",20d); Set(unit,"MaxStrength",30d); Sequence(f.State,"GroundUnits").Add(unit);
            var secret = f.New("Core.State.GroundUnit"); Set(secret,"SideId","RED"); Set(secret,"Name",null);
            Set(secret,"BaseId","SECRET BASE"); Set(secret,"Strength",double.NaN); Sequence(f.State,"GroundUnits").Add(secret);
            var snapshot = new CampaignSituationReader().Capture(f.Sim,f.DisplayShipName);
            var before = snapshot.Ground.Bases().ToString() + snapshot.Ground.Units().ToString();
            Set(stock,"ShipFuelT",1d); Set(unit,"Strength",1d);
            Require(snapshot.Ground.Bases().ToString() + snapshot.Ground.Units().ToString() == before, "Campaign evidence retained live stocks or troops");
            Require(((System.Collections.IDictionary)Get(f.State,"Stocks")).Count == 1, "Capture created missing stock records");
            var session = new AdvisorQuerySession(snapshot,"ja","基地と地上部隊",20);
            var data = session.Execute(new JObject { ["consultation_id"] = session.Id, ["requests"] = new JArray(
                new JObject { ["query"] = "get_bases", ["ids"] = new JArray(SnapshotIds.Base(0)) },
                new JObject { ["query"] = "get_ground_units", ["ids"] = new JArray("ground_unit_1") }) });
            Require((string?)data["status"] == "ok" && (double?)data["results"]![0]!["data"]![0]!["stocks"]!["ship_fuel_tonnes"] == 100 &&
                (string?)data["results"]![0]!["data"]![0]!["stocks"]!["ammunition"]![0]!["display_name"] == "表示兵装：known-ammo" &&
                (double?)data["results"]![1]!["data"]![0]!["strength"] == 20, "Own campaign base and troop evidence was lost");
            Require((string?)session.Execute(new JObject { ["consultation_id"] = session.Id, ["requests"] = new JArray(
                new JObject { ["query"] = "get_bases", ["ids"] = new JArray(SnapshotIds.Base(1)) }) })["error"]?["code"] == "unknown_id",
                "The own-base route returned live enemy base details");
            var noGround = new AdvisorQuerySession(snapshot,"en","Bases",information:AdvisorInformation.All.With(CampaignGroundUnits,false));
            var baseData = noGround.Execute(new JObject { ["consultation_id"] = noGround.Id, ["requests"] = new JArray(
                new JObject { ["query"] = "get_bases", ["ids"] = new JArray(SnapshotIds.Base(0)) }) });
            Require(baseData["results"]![0]!["data"]![0]!["garrison_strength"] == null &&
                noGround.InitialInput()["situation"]!["overview"]!["friendly_ground_unit_count"] == null,
                "Base details or metadata bypassed the ground-unit exclusion");
        });
    }
}
