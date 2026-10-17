using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using SeaPowerAIAdvisor;

internal static class BattleBriefingTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("XML briefing selects only the displayed pane and follows the game's language fallback", () =>
        {
            var values = new Dictionary<string, string>();
            var files = new Dictionary<string, string> {
                ["missions/test_briefing/BriefingText_ja.xml"] = "<TextBlock Text=\"日本語の命令\"/>",
                ["missions/test_briefing/BriefingText_en.xml"] = "<TextBlock Text=\"English orders\"/>",
                ["custom/BriefingText_ja.xml"] = "<TextBlock Text=\"カスタム説明\"/>",
                ["custom/override.xml"] = "<TextBlock Text=\"選択された説明\"/>"
            };
            string Read(string language = "ja") => BattleBriefingReader.Read("missions/test.ini", language,
                (s, k, fallback) =>
                {
                    Require(k == "MissionBriefingAssetsDirectory" || k == "MissionBriefingLeftPane", "Non-public mission definitions were queried");
                    return values.TryGetValue(s + ":" + k, out var v) ? v : fallback;
                }, p => files.ContainsKey(p) ? p : null, p => files[p]).Text;
            Require(Read() == "日本語の命令", "Current game language was not selected");
            files.Remove("missions/test_briefing/BriefingText_ja.xml");
            Require(Read() == "English orders", "Missing localized pane did not use the game's English fallback");
            values["Language_ja:MissionBriefingAssetsDirectory"] = "custom";
            Require(Read() == "カスタム説明", "Configured assets directory was ignored");
            values["Language_ja:MissionBriefingLeftPane"] = "custom/override.xml";
            Require(Read() == "選択された説明", "Explicit pane override was ignored");
            files.Remove("custom/override.xml"); files.Remove("custom/BriefingText_ja.xml");
            values["Language_en:MissionBriefingLeftPane"] = "missions/test_briefing/BriefingText_en.xml";
            Require(Read() == "English orders", "Explicit English fallback was not respected");
            values.Clear(); files.Clear();
            Require(Read() == "", "A legacy-only mission acquired an invented XML briefing");
            var legacySave = BattleBriefingReader.Read("saves/legacy.sav", "ja", (_, _, fallback) => fallback,
                _ => null, _ => throw new Exception("A legacy-only mission read an XML file"),
                () => new BattleBriefingTemplate("missions/legacy.ini", (_, _, fallback) => fallback));
            Require(legacySave.Text == "" && !legacySave.FromBase, "A legacy save without XML lost its event-log briefing route");
            values["Language_ja:MissionBriefingLeftPane"] = "missing.xml";
            try { _ = Read(); throw new Exception("Missing configured pane was silently ignored"); }
            catch (FileNotFoundException) { }
        });
        check("Saved mission briefing recovery follows BaseFile without reusing stale overrides or replacing invalid content", () =>
        {
            var files = new Dictionary<string, string> {
                ["source/mission07_briefing/BriefingText_ja.xml"] = "<TextBlock Text=\"公開された任務の背景\"/>",
                ["override/source_en.xml"] = "<TextBlock Text=\"Source English briefing\"/>"
            };
            var sourceSettings = new Dictionary<string, string>();
            var sourceReads = 0;
            var fileReads = 0;
            (string Text, bool FromBase) Read() => BattleBriefingReader.Read("saves/resumed.sav", "ja",
                (section, key, fallback) => key switch {
                    "MissionBriefingAssetsDirectory" => "deleted/generated_42_briefing",
                    "MissionBriefingLeftPane" => "deleted/generated_42_briefing/BriefingText_" + section.Substring(9) + ".xml",
                    _ => throw new Exception("Unexpected source of player knowledge")
                }, p => files.ContainsKey(p) ? p : null,
                p => { fileReads++; return files[p]; },
                () => { sourceReads++; return new BattleBriefingTemplate("source/mission07.ini", (section, key, fallback) => {
                    Require(key == "MissionBriefingAssetsDirectory" || key == "MissionBriefingLeftPane", "Hidden mission fields were read");
                    return sourceSettings.TryGetValue(section + ":" + key, out var value) ? value : fallback;
                }); });
            var result = Read();
            Require(result.Text == "公開された任務の背景" && result.FromBase && sourceReads == 1 && fileReads == 1,
                "Missing generated XML was not restored from the referenced public source");
            files["deleted/generated_42_briefing/BriefingText_ja.xml"] = "<TextBlock Text=\"現在の生成済み本文\"/>";
            result = Read();
            Require(result.Text == "現在の生成済み本文" && !result.FromBase && sourceReads == 1,
                "An available generated briefing was replaced by its source");
            files["deleted/generated_42_briefing/BriefingText_ja.xml"] = "<Grid>";
            try { _ = Read(); throw new Exception("Malformed generated XML was hidden by a source fallback"); }
            catch (XmlException) { }
            files.Remove("deleted/generated_42_briefing/BriefingText_ja.xml");
            files.Remove("source/mission07_briefing/BriefingText_ja.xml");
            sourceSettings["Language_en:MissionBriefingLeftPane"] = "override/source_en.xml";
            result = Read();
            Require(result.Text == "Source English briefing" && result.FromBase, "Source-language fallback or configured override was ignored");
            files["override/source_en.xml"] = "<TextBlock>命令 {FlagshipName}</TextBlock>";
            try { _ = Read(); throw new Exception("Generation-time identity was guessed or left as a valid fact"); }
            catch (NotSupportedException) { }
            files.Clear();
            try { _ = Read(); throw new Exception("Missing source XML silently became an empty briefing"); }
            catch (FileNotFoundException) { }
        });
        check("XML briefing preserves readable mixed text and excludes hidden content, resources and source attributes", () =>
        {
            var xml = """
                <?xml version="1.0"?>
                <Grid xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Grid.Resources><x:String x:Key="internal">SECRET RESOURCE</x:String></Grid.Resources>
                  <Grid.RowDefinitions><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                  <!-- SECRET COMMENT -->
                  <TextBlock Text="命令 &amp; 状況" ToolTip="SECRET TOOLTIP"/>
                  <TextBlock>輸送船<Run Text="第十八いびしゅ丸"/><LineBreak/><Bold>を迎撃せよ。</Bold></TextBlock>
                  <StackPanel Visibility="Collapsed"><TextBlock Text="SECRET COLLAPSED"/></StackPanel>
                  <Grid Visibility="Hidden"><TextBlock Text="SECRET HIDDEN"/></Grid>
                  <TextBlock Opacity="0.00" Text="SECRET TRANSPARENT"/>
                  <TextBlock Text="複数行
                の命令"/>
                  <TextBlock Text="{}{literal}"/>
                  <Image Source="SECRET IMAGE PATH"/>
                </Grid>
                """;
            var text = BattleBriefingReader.Text(xml);
            Require(text == "命令 & 状況\n輸送船第十八いびしゅ丸\nを迎撃せよ。\n複数行\nの命令\n{literal}", "Rendered text, Unicode or explicit line breaks changed: " + text);
            Require(!text.Contains("SECRET", StringComparison.Ordinal), "Hidden or non-display XML data leaked");
        });
        check("XML briefing refuses unresolved presentation and malformed or external-entity documents", () =>
        {
            foreach (var xml in new[] {
                "<TextBlock Text=\"{Binding Secret}\"/>",
                "<Grid Visibility=\"{Binding Secret}\"><TextBlock Text=\"hidden\"/></Grid>",
                "<Grid><Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"Visibility\" Value=\"Collapsed\"/></Style></Grid.Resources><TextBlock Text=\"hidden\"/></Grid>",
                "<TextBlock Text=\"hidden\"><TextBlock.Visibility>Collapsed</TextBlock.Visibility></TextBlock>",
                "<TextBlock><Unknown Secret=\"hidden\"/></TextBlock>"
            })
            {
                try { _ = BattleBriefingReader.Text(xml); throw new Exception("Unresolved presentation was treated as readable text"); }
                catch (NotSupportedException) { }
            }
            foreach (var xml in new[] { "<Grid>", "<!DOCTYPE Grid [<!ENTITY secret SYSTEM 'file:///not-permitted'>]><TextBlock>&secret;</TextBlock>" })
            {
                try { _ = BattleBriefingReader.Text(xml); throw new Exception("Malformed XML or an external entity was accepted"); }
                catch (XmlException) { }
            }
        });
    }

    internal static void RunInstalled(string gameAssembly, Action<string, Action> check)
    {
        check("Installed Okinawa briefing supplies public mission intelligence as plain text", () =>
        {
            var original = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameAssembly))!, "StreamingAssets", "original");
            const string source = "campaigns/pacific-strike-task-force/missions/01 Raid on Okinawa.ini";
            var briefing = BattleBriefingReader.Read("saves/resumed.sav", "ja",
                (section, key, fallback) => key == "MissionBriefingLeftPane" ? "missing/generated/BriefingText.xml" : fallback,
                p => { var path = Path.Combine(original, p.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(path) ? path : null; },
                File.ReadAllText, () => new BattleBriefingTemplate(source, (_, _, fallback) => fallback));
            Require(briefing.FromBase, "Installed source recovery was not marked as recovered");
            var text = briefing.Text;
            Require(text.Contains("第十八いびしゅ丸", StringComparison.Ordinal) && text.Contains("特殊部隊", StringComparison.Ordinal) &&
                text.Contains("カニン", StringComparison.Ordinal) && !text.Contains("<TextBlock", StringComparison.Ordinal),
                "The actual player briefing lost mission intelligence or retained its XML markup");
        });
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
