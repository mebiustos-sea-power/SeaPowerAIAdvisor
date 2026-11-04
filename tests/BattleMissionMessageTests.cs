using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Shared;

internal static class BattleMissionMessageTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Mission publications are detached values and are cleared on every load, including the same mission", () =>
        {
            var history = new BattleMissionMessageHistory(); var mission = new object();
            history.Begin(mission);
            Require(history.Capture(mission).Count == 0, "A fresh load invented prior publications");
            history.Publish(mission, "Orders", "Protect the convoy.", Time);
            var snapshot = history.Capture(mission);
            Require((string?)snapshot[0]!["text"] == "Orders\n\nProtect the convoy.", "The displayed title or body was lost");
            snapshot[0]!["text"] = "edited snapshot";
            Require((string?)history.Capture(mission)[0]!["text"] != "edited snapshot", "A snapshot modified retained publications");
            Require(history.Capture(new object()).Count == 0, "Another mission inherited published text");
            history.Begin(mission);
            Require(history.Capture(mission).Count == 0, "Reloading the same mission restored old publications");
            history.Publish(mission, "New orders", "Return to base.", Time.AddMinutes(1));
            Require(history.Capture(mission).Count == 1 && (string?)history.Capture(mission)[0]!["text"] == "New orders\n\nReturn to base.", "Reload retained the earlier message");
            history.Clear();
            Require(history.Capture(mission).Count == 0, "Mission teardown retained publications");
        });
        check("Mission messages default off and their permission is independent of briefing and published reports", () =>
        {
            Require(!AdvisorOptions.Parse("{}").Information.Allows(InformationKind.BattleMissionMessages), "New settings enabled message delivery");
            var history = History(); var mission = Owner;
            foreach (var briefing in new[] { false, true })
                foreach (var messages in new[] { false, true })
                {
                    var policy = AdvisorInformation.All.With(InformationKind.BattleBriefing, briefing)
                        .With(InformationKind.BattleMissionMessages, messages).With(InformationKind.BattleReports, false);
                    var saved = AdvisorInformation.ParseSelections(policy.Data());
                    Require(saved.Allows(InformationKind.BattleMissionMessages) == messages && saved.Allows(InformationKind.BattleBriefing) == briefing, "Saving joined independent permissions");
                    var session = Session(history.Capture(mission), policy);
                    Require(session.InitialInput().ToString().Contains("Original briefing", StringComparison.Ordinal) == false, "On-demand setup exposed mission text");
                    var result = Read(session);
                    if (!briefing && !messages)
                        Require((string?)result["error"]?["code"] == "information_disabled", "A direct read bypassed both exclusions");
                    else
                    {
                        var data = result["results"]![0]!["data"]!;
                        Require((data["text"] != null) == briefing && (data["messages"] != null) == messages, "A disabled source leaked or an enabled source was blocked");
                        if (messages) Require(data["messages"]!.Count() == 1 && (string?)data["message_history_scope"] == "since_current_load", "Message coverage was lost");
                    }
                    var full = session.ExportSituation().ToString();
                    Require(full.Contains("Original briefing", StringComparison.Ordinal) == briefing && full.Contains("Protect the convoy", StringComparison.Ordinal) == messages, "Full delivery bypassed independent permissions");
                }
            Require(history.Capture(mission).Count == 1, "Disabling delivery erased the memory used after re-enabling it");
        });
        check("Mission publications survive the report retention limit and consultation snapshots do not acquire later messages", () =>
        {
            var history = History();
            for (var i = 0; i < 12; i++) history.Publish(Owner, "Update " + i, "Published order " + i, Time.AddMinutes(i + 1));
            var session = Session(history.Capture(Owner));
            history.Publish(Owner, "Later", "Not part of this consultation", Time.AddHours(1));
            var items = Read(session)["results"]![0]!["data"]!["messages"]!;
            Require(items.Count() == 13 && (string?)items[0]!["text"] == "Orders\n\nProtect the convoy.", "The start message was pushed out by report retention");
            Require(!items.ToString().Contains("Not part of this consultation", StringComparison.Ordinal), "A pending consultation acquired later game information");
            Require(history.Capture(Owner).Count == 14, "New publications were not available to the next consultation");
        });
        check("An exact briefing duplicate is omitted only when that briefing is actually delivered", () =>
        {
            var history = History(); var text = (string)history.Capture(Owner)[0]!["text"]!;
            var both = Session(history.Capture(Owner), briefing: text);
            Require(Read(both)["results"]![0]!["data"]!["messages"]!.Count() == 0, "The same text was sent twice in one briefing response");
            var onlyMessages = Session(history.Capture(Owner), AdvisorInformation.All.With(InformationKind.BattleBriefing, false), text);
            Require(Read(onlyMessages)["results"]![0]!["data"]!["messages"]!.Count() == 1, "Duplicate handling dropped the only permitted source");
        });
        check("An unreadable XML briefing remains explicit without blocking already-published messages", () =>
        {
            var session = Session(History().Capture(Owner), unavailable: true);
            var data = Read(session)["results"]![0]!["data"]!;
            Require((string?)data["briefing_error"]?["code"] == "briefing_unavailable" && data["text"] == null && data["messages"]!.Count() == 1, "The briefing error was hidden or valid messages were lost");
            var full = session.ExportSituation().Single(r => (string?)r["query"] == "get_briefing")["data"]!;
            Require(JToken.DeepEquals(data, full), "Full delivery discarded the partial source result");
            var excluded = Session(History().Capture(Owner), AdvisorInformation.All.With(InformationKind.BattleBriefing, false), unavailable: true);
            Require(Read(excluded)["results"]![0]!["data"]!["briefing_error"] == null, "An excluded briefing blocked enabled messages");
        });
    }
    private static readonly object Owner = new();
    private static readonly DateTime Time = new(1985, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static BattleMissionMessageHistory History()
    {
        var history = new BattleMissionMessageHistory(); history.Begin(Owner);
        history.Publish(Owner, "Orders", "Protect the convoy.", Time); return history;
    }
    internal static JObject Snapshot(JArray messages, string briefing = "Original briefing", bool unavailable = false)
    {
        var data = new JObject { ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = briefing,
            ["mission_briefing_source"] = "event_log", ["mission_briefing_unavailable"] = unavailable, ["mission_messages"] = messages,
            ["weather"] = new JObject { ["wind_force_beaufort"] = 3, ["precipitation"] = new JArray(), ["fog"] = false, ["lightning"] = false } };
        foreach (var key in new[] { "friendly_units", "observed_contacts", "public_objectives", "mission_map_areas", "friendly_bases", "hosted_air_units", "aviation_hosts", "air_tasks", "unit_capabilities", "contact_threat_references", "weapon_references", "published_reports" }) data[key] = new JArray();
        return data;
    }
    private static BattleQuerySession Session(JArray messages, AdvisorInformation? information = null, string briefing = "Original briefing", bool unavailable = false) =>
        new(new BattleSnapshot(Snapshot(messages, briefing, unavailable)), "en", "Mission context", information: information ?? AdvisorInformation.All);
    private static JObject Read(BattleQuerySession session) => session.Execute(new JObject { ["consultation_id"] = session.Id,
        ["requests"] = new JArray(new JObject { ["query"] = "get_briefing" }) });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
