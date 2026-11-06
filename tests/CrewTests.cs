using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Crew;

internal static class CrewTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Chatter opt in only on saved WIP settings and remain enabled after unrelated category saves", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "options.json"); var store = new AdvisorOptionsStore(path);
            Require(!store.Current.CrewEnabled && !File.Exists(path), "Chatter started or wrote settings by default");
            store.SaveGeneral(store.Current.TextSize, "Commander"); store.SaveWip(false, true);
            store.SaveInformation(AdvisorInformation.Default); store.SaveConsultation(7, 1, "adviser-only instruction");
            store.SaveDeveloper(false); store.SaveCommands(new[] { "mock.disabled" }); store.SaveWip(false, store.Read().CrewEnabled);
            store.SaveTemplates(new AdvisorTemplateOptions()); store.SaveGeneral(store.Current.TextSize, "Commander");
            Require(new AdvisorOptionsStore(path).Current.CrewEnabled, "Another settings form reset AI Crew");
            store.SaveWip(true, false);
            var saved = new AdvisorOptionsStore(path).Current;
            Require(!saved.CrewEnabled && saved.CampaignEnabled && saved.UserName == "Commander", "WIP save lost one choice or changed general settings");
            Throws<AdvisorOptionsException>(() => AdvisorOptions.Parse("{\"crew_enabled\":\"true\"}"));
        }));
        check("Chatter settings persist independently and failed writes cannot apply a draft", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(store.Current.ConnectionId == null && store.Current.ShowHelpTab && CrewSettings.Parse("{}").ShowHelpTab && !File.Exists(path), "Opening Chatter selected a connection, hid guidance or saved settings");
            store.Save(new CrewSettings("chatter-only", 2, 9, showHelpTab: false));
            var reopened = new CrewSettingsStore(path);
            Require(reopened.Current.ConnectionId == "chatter-only" && reopened.Current.IntervalMinutes == 2 && reopened.Current.DisplaySeconds == 9 && !reopened.Current.ShowHelpTab, "Chatter preferences were lost");
            foreach (var interval in new[] { 1, 8, 15 })
            {
                store.Save(new CrewSettings("chatter-only", interval, 9, showHelpTab: store.Current.ShowHelpTab));
                Require(new CrewSettingsStore(path).Current.IntervalMinutes == interval && !new CrewSettingsStore(path).Current.ShowHelpTab, "Saving an interval lost the selected interval or help visibility");
            }
            store.Save(new CrewSettings("chatter-only", 15, 9, showHelpTab: true));
            Require(new CrewSettingsStore(path).Current.ShowHelpTab && store.Current.IntervalMinutes == 15 && store.Current.DisplaySeconds == 9,
                "Re-enabling guidance changed other Chatter preferences or did not persist");
            Require(new CrewSettings().ExchangeChancePercent == 50 && CrewSettings.Parse("{}").ExchangeChancePercent == 50,
                "Unset exchange preference did not use its requested default");
            foreach (var percent in new[] { 0, 33, 100 })
            {
                store.Save(new CrewSettings("chatter-only", 15, 9, showHelpTab: true, exchangeChancePercent: percent));
                var read = new CrewSettingsStore(path).Current;
                Require(read.ExchangeChancePercent == percent && read.ConnectionId == "chatter-only" && read.IntervalMinutes == 15 && read.DisplaySeconds == 9 && read.ShowHelpTab,
                    "Exchange preference did not persist or changed another saved preference");
            }
            foreach (var invalid in new JToken[] { new JValue(-1), new JValue(101), new JValue(33.5), new JValue("33"), JValue.CreateNull() })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["exchange_chance_percent"] = invalid }.ToString()));
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => store.Save(new CrewSettings("other", 3, 8)));
            Require(store.Current.ConnectionId == "chatter-only", "Failed storage published the pending connection");
            Throws<CrewException>(() => new CrewSettings("id", 0)); Throws<CrewException>(() => new CrewSettings("id", 16));
            Throws<CrewException>(() => CrewSettings.Parse("{\"interval_minutes\":1.5}"));
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("false"), new JValue(0) })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["show_help_tab"] = invalid }.ToString()));
        }));
        check("Automatic appointment preferences default off, persist independently and reject malformed flags", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(!store.Current.AutoAppointments && !CrewSettings.Parse("{}").AutoAppointments && !File.Exists(path),
                "Opening settings enabled automatic appointments or wrote a file");
            var selected = CrewInformation.Default.With("fuel", false);
            store.Save(new CrewSettings("crew-api", 7, 12, CrewNotificationPosition.BottomLeft, false, 75, selected, false, true));
            var saved = new CrewSettingsStore(path).Current;
            Require(saved.AutoAppointments && saved.ConnectionId == "crew-api" && saved.IntervalMinutes == 7 && saved.DisplaySeconds == 12 &&
                saved.NotificationPosition == CrewNotificationPosition.BottomLeft && !saved.ShowHelpTab && !saved.ShowRanks &&
                saved.ExchangeChancePercent == 75 && saved.Information.Key == selected.Key, "Saving appointments changed other preferences");
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("false"), new JValue(0) })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["auto_appointments"] = invalid }.ToString()));
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => store.Save(new CrewSettings(autoAppointments: false)));
            Require(store.Current.AutoAppointments, "A failed write applied an appointment draft");
        }));
        check("Chatter positions default to top right, persist all six choices and reject invalid saved values", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(store.Current.NotificationPosition == CrewNotificationPosition.TopRight && !File.Exists(path), "Unset position did not use top right without writing");
            Require(CrewSettings.Parse("{}").NotificationPosition == CrewNotificationPosition.TopRight, "An unset field did not use the requested default");
            foreach (CrewNotificationPosition position in Enum.GetValues(typeof(CrewNotificationPosition)))
            {
                store.Save(new CrewSettings("chatter-only", 3, 9, position));
                var read = new CrewSettingsStore(path).Current;
                Require(read.NotificationPosition == position && read.ConnectionId == "chatter-only" && read.IntervalMinutes == 3 && read.DisplaySeconds == 9,
                    "A saved position was lost across reopening or changed another Chatter preference");
            }
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("offscreen"), new JValue(2), new JValue(true) })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["notification_position"] = invalid }.ToString()));
            Throws<CrewException>(() => new CrewSettings(notificationPosition: (CrewNotificationPosition)99));
        }));
        check("Rank display defaults on, persists independently and rejects malformed flags", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(store.Current.ShowRanks && CrewSettings.Parse("{}").ShowRanks, "Rank display did not default on");
            var selected = CrewInformation.Default.With("fuel", false);
            store.Save(new CrewSettings("chatter", 7, 14, CrewNotificationPosition.BottomLeft, false, 60, selected, showRanks: false));
            var saved = new CrewSettingsStore(path).Current;
            Require(!saved.ShowRanks && saved.ConnectionId == "chatter" && saved.IntervalMinutes == 7 && saved.DisplaySeconds == 14 &&
                saved.NotificationPosition == CrewNotificationPosition.BottomLeft && !saved.ShowHelpTab && saved.ExchangeChancePercent == 60 && saved.Information.Key == selected.Key,
                "Saving the display flag changed unrelated preferences");
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("false"), new JValue(0) })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["show_ranks"] = invalid }.ToString()));
        }));
        check("Rank display combines the unchanged full name and rank with one space in the requested order", () =>
        {
            foreach (var sample in new[] { ("Angus MacGregor", "Cmdr.", "before", "Cmdr. Angus MacGregor"), ("アンガス・マクレガー", "中佐", "after", "アンガス・マクレガー 中佐") })
            {
                var batch = CrewTestGeneration.Appointments("unit:1");
                var row = AppointmentRow("unit:1", sample.Item1); row["rank"] = sample.Item2; row["name_with_rank"] = sample.Item4;
                var appointment = CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(row))).Appointments["unit:1"]; var person = appointment.Person;
                var vote = appointment.RankBeforeName ?? throw new InvalidOperationException("Expected a valid rank vote");
                Require(person.DisplayName(false, vote) == sample.Item1 && person.DisplayName(true, vote) == sample.Item4 && person.Name == sample.Item1 &&
                    person.Rank == sample.Item2 && appointment.RankBeforeName == (sample.Item3 == "before"),
                    "Rank display abbreviated the name, lost the separator or changed the requested order");
                var exported = person.Json();
                Require((string?)exported["name"] == sample.Item1 && (string?)exported["rank"] == sample.Item2 && exported["rank_position"] == null && exported["name_with_rank"] == null,
                    "Person export changed the name, rank or display order");
                var lower = (JObject)row.DeepClone(); lower["name_with_rank"] = sample.Item4.ToLowerInvariant();
                var lowerResult = CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(lower))).Appointments["unit:1"];
                Require(lowerResult.RankBeforeName == appointment.RankBeforeName && lowerResult.Person.Name == sample.Item1,
                    "Case differences in the hint changed the vote or the stored personal name");
                foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue(1), new JValue("  "), new JValue(new string('a', 81)) })
                { var bad = (JObject)row.DeepClone(); bad["rank"] = invalid; Throws<CrewException>(() => CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(bad)))); }
            }
        });
        check("Captured name forms produce one before and four after votes without abbreviating displayed people", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; var targets = Enumerable.Range(1, 5).Select(i => Target(i)).ToArray();
            rig.Chatter.Refresh(targets.Take(1).ToArray()); rig.Chatter.RequestAppointments(0); rig.Tick(1); var first = rig.Take();
            JObject Captured(string id, string name, string rank, string form) => new() { ["id"] = id, ["character_id"] = "new", ["person_name"] = name, ["rank"] = rank, ["name_with_rank"] = form, ["personality"] = "Calm" };
            first.Completion.SetResult(CrewTestGeneration.Read(first.Batch, Reply(new JArray(), new JArray(Captured("unit:1", "佐伯 健一郎", "大尉", "大尉 佐伯 健一郎"))))); rig.Pump(1);
            Require(rig.Chatter.RankBeforeVotes == 1 && rig.Chatter.RankPosition == "before", "The initial prefix was not counted");
            rig.Chatter.Refresh(targets); rig.Chatter.RequestAppointments(1); rig.Tick(2); var next = rig.Take();
            var rows = new JArray(Captured("unit:2", "ジェームズ・A・ハリントン", "大佐", "ハリントン大佐"), Captured("unit:3", "佐伯 光", "一等海尉", "佐伯一等海尉"),
                Captured("unit:4", "真壁 涼子", "三等海佐", "真壁三等海佐"), Captured("unit:5", "イアン・マクレガー", "少佐", "マクレガー少佐"));
            next.Completion.SetResult(CrewTestGeneration.Read(next.Batch, Reply(new JArray(), rows))); rig.Pump(2);
            Require(rig.Chatter.RankBeforeVotes == 1 && rig.Chatter.RankAfterVotes == 4 && rig.Chatter.RankInvalidVotes == 0 && rig.Chatter.RankPosition == "after" &&
                rig.Chatter.Person("unit:1")!.DisplayName(true, rig.Chatter.RankBeforeName) == "佐伯 健一郎 大尉" &&
                rig.Chatter.Person("unit:2")!.DisplayName(true, rig.Chatter.RankBeforeName) == "ジェームズ・A・ハリントン 大佐",
                "The captured suffixes were miscounted or shortened hints abbreviated the displayed full name");
        });
        check("Invalid or missing rank hints do not reject valid people, while essential appointment fields stay strict", () =>
        {
            var batch = CrewTestGeneration.Appointments("unit:1");
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue(true), new JValue(123), new JValue(""), new JValue("   "), new JValue("left"), new JValue("Cmdr. Fictional Cmdr."), new JValue("Cmdr."), new JValue(new string('x', 300)), new JArray(), new JObject() })
            {
                var row = AppointmentRow("unit:1"); row["name_with_rank"] = invalid;
                var result = CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(row)));
                Require(result.Appointments["unit:1"].Person.Name == "Fictional" && result.Appointments["unit:1"].RankBeforeName == null && result.Remarks.Count == 0,
                    "An auxiliary invalid vote rejected the person");
            }
            var missing = AppointmentRow("unit:1"); missing.Remove("name_with_rank");
            Require(CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(missing))).Appointments["unit:1"].RankBeforeName == null,
                "Absent display hints prevented appointment");
            foreach (var field in new[] { "id", "character_id", "person_name", "rank", "personality" })
            {
                var bad = AppointmentRow("unit:1"); bad.Remove(field); Throws<CrewException>(() => CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(bad))));
                bad = AppointmentRow("unit:1"); bad[field] = JValue.CreateNull(); Throws<CrewException>(() => CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(bad))));
            }
            var unknown = AppointmentRow("unit:99"); Throws<CrewException>(() => CrewTestGeneration.Read(batch, Reply(new JArray(), new JArray(unknown))));
        });
        check("Invalid rank votes never pause Chatter and valid cumulative votes alone determine the shared order", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; var targets = Enumerable.Range(1, 10).Select(i => Target(i)).ToArray();
            rig.Chatter.Refresh(targets.Take(1).ToArray()); rig.Chatter.RequestAppointments(0); rig.Tick(1); var first = rig.Take();
            var row = AppointmentRow("unit:1"); row["name_with_rank"] = JValue.CreateNull();
            first.Completion.SetResult(CrewTestGeneration.Read(first.Batch, Reply(new JArray(), new JArray(row)))); rig.Pump(1);
            var person = rig.Chatter.Person("unit:1")!;
            Require(!rig.Chatter.Paused && rig.Chatter.Error == null && person != null && rig.Chatter.History.Count == 0 &&
                rig.Chatter.RankBeforeVotes == 0 && rig.Chatter.RankAfterVotes == 0 && rig.Chatter.RankInvalidVotes == 1 && rig.Chatter.RankPosition == "before",
                "An invalid-only appointment stopped Chatter, lost its remark or did not use provisional before");
            rig.GamePaused = true;
            rig.Chatter.Refresh(targets.Take(5).ToArray()); rig.Chatter.RequestAppointments(1); rig.Tick(2); var four = rig.Take();
            var invalid = AppointmentRow("unit:4"); invalid["name_with_rank"] = JValue.CreateNull();
            four.Completion.SetResult(CrewTestGeneration.Read(four.Batch, Reply(new JArray(), new JArray(AppointmentRow("unit:2", position: "after"), AppointmentRow("unit:3", position: "after"), invalid, AppointmentRow("unit:5", position: "left"))))); rig.Pump(2);
            Require(rig.Chatter.RankPosition == "after" && rig.Chatter.RankAfterVotes == 2 && rig.Chatter.RankBeforeVotes == 0 && rig.Chatter.RankInvalidVotes == 3,
                "More invalid than valid votes prevented a valid majority");
            rig.Chatter.Refresh(targets.Take(8).ToArray()); rig.Chatter.RequestAppointments(2); rig.Tick(3); var three = rig.Take();
            three.Completion.SetResult(CrewTestGeneration.Read(three.Batch, Reply(new JArray(), new JArray(AppointmentRow("unit:6"), AppointmentRow("unit:7"), AppointmentRow("unit:8", position: ""))))); rig.Pump(3);
            Require(rig.Chatter.RankPosition == "after" && rig.Chatter.RankAfterVotes == 2 && rig.Chatter.RankBeforeVotes == 2 && rig.Chatter.RankInvalidVotes == 4,
                "A valid tie did not retain the previous shared order");
            rig.Chatter.Refresh(targets); rig.Chatter.RequestAppointments(3); rig.Tick(4); var last = rig.Take(); rig.CompleteWithVote(last, "invalid"); rig.Pump(4);
            var status = new CrewLocalApi(rig.Chatter, () => 4, () => { }).Status();
            Require(!rig.Chatter.Paused && rig.Chatter.Error == null && rig.Errors.Count == 0 && rig.Chatter.RankPosition == "after" &&
                rig.Chatter.RankAfterVotes == 2 && rig.Chatter.RankBeforeVotes == 2 && (int?)status["rank_position_votes"]?["invalid"] == 6 &&
                targets.All(x => rig.Chatter.Person(x.Id) != null) && ReferenceEquals(person, rig.Chatter.Person("unit:1")),
                "An invalid-only later batch reset the established order or lost accepted people");
        });
        check("Shared rank order follows cumulative appointments and reformats existing history without replacing people", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, 9).Select(i => Target(i)).ToArray();
            rig.Chatter.Refresh(targets.Take(1).ToArray()); rig.Chatter.RequestAppointments(0); rig.Tick(1);
            var first = rig.Take(); rig.CompleteWithVote(first, "after"); rig.Pump(1); var person = rig.Chatter.Person("unit:1")!;
            rig.Tick(2); var speech = rig.Take(); rig.Complete(speech, "unit:1"); rig.Pump(2);
            Require(rig.Chatter.History.Single().Submission.Person.DisplayName(true, rig.Chatter.RankBeforeName) == "Fictional Cmdr.", "Initial order lost");
            rig.GamePaused = true; rig.Chatter.Refresh(targets.Take(5).ToArray()); rig.Chatter.RequestAppointments(2); rig.Tick(3);
            var four = rig.Take(); rig.CompleteWithVote(four, "before"); rig.Pump(3);
            Require(rig.Chatter.RankBeforeVotes == 4 && rig.Chatter.RankAfterVotes == 1 && rig.Chatter.RankPosition == "before" &&
                ReferenceEquals(person, rig.Chatter.Person("unit:1")) && rig.Chatter.History.Single().Submission.Person.DisplayName(true, rig.Chatter.RankBeforeName) == "Cmdr. Fictional", "Later appointments did not reformat existing history");
            rig.Chatter.Refresh(targets.Take(8).ToArray()); rig.Chatter.RequestAppointments(3); rig.Tick(4);
            var three = rig.Take(); rig.CompleteWithVote(three, "after"); rig.Pump(4);
            Require(rig.Chatter.RankPosition == "before" && rig.Chatter.RankAfterVotes == 4, "A tie lost the existing order");
            rig.Chatter.Refresh(targets); rig.Chatter.RequestAppointments(4); rig.Tick(5);
            var last = rig.Take(); rig.CompleteWithVote(last, "after"); rig.Pump(5);
            rig.Language = "ja"; rig.SetEnabled("unit:1", false, 5); rig.SetEnabled("unit:1", true, 5);
            rig.Chatter.SetPaused(true, 5); rig.Chatter.SetPaused(false, 5); rig.Tick(6);
            Require(rig.Requests == 5 && rig.Chatter.RankBeforeVotes == 4 && rig.Chatter.RankAfterVotes == 5 && rig.Chatter.RankPosition == "after" && person.DisplayName(false, true) == "Fictional", "Language or participation changes recounted people");
            var status = new CrewLocalApi(rig.Chatter, () => 6, () => { }).Status();
            Require((string?)status["rank_position"] == "after" && (int?)status["rank_position_votes"]?["after"] == 5 &&
                ((JArray)status["targets"]!).All(x => x["person"]?["rank_position"] == null), "Per-person ordering leaked into status");
        });
        check("Rank votes count only appointments still eligible when the batch is accepted", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Target(2), Target(3) });
            Require(rig.Chatter.RankPosition == null, "A session without appointments already had a decided rank order");
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var call = rig.Take();
            rig.Chatter.Refresh(new[] { Target(2), Target(3) });
            var rows = new JArray(AppointmentRow("unit:1", position: "before"), AppointmentRow("unit:2", position: "after"), AppointmentRow("unit:3", position: "after"));
            call.Completion.SetResult(CrewTestGeneration.Read(call.Batch, Reply(new JArray(), rows))); rig.Pump(1);
            Require(rig.Chatter.Person("unit:1") == null && rig.Chatter.RankBeforeVotes == 0 && rig.Chatter.RankAfterVotes == 2 && rig.Chatter.RankPosition == "after",
                "A disabled in-flight appointment cast a vote or discarded the still-valid appointments");
            rig.Chatter.Refresh(new[] { Target(1), Target(2), Target(3) }); rig.Chatter.RequestAppointments(1); rig.Tick(2); var fresh = rig.Take(); rig.CompleteWithVote(fresh, "before"); rig.Pump(2);
            Require(rig.Chatter.Person("unit:1") != null && rig.Chatter.RankBeforeVotes == 1 && rig.Chatter.RankAfterVotes == 2 && rig.Chatter.RankPosition == "after",
                "Accepting the fresh appointment did not add exactly one vote to the existing total");
        });
        check("An initially tied valid rank vote retains the provisional before order", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Target(2) });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var call = rig.Take();
            call.Completion.SetResult(CrewTestGeneration.Read(call.Batch, Reply(new JArray(), new JArray(AppointmentRow("unit:2", position: "after"), AppointmentRow("unit:1", position: "before")))));
            rig.Pump(1);
            Require(rig.Chatter.RankBeforeVotes == 1 && rig.Chatter.RankAfterVotes == 1 && rig.Chatter.RankPosition == "before",
                "The first tied batch did not retain its provisional order");
        });
        check("Appointments alone receive detached mission context once per batch and retain ranked people across later speech", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true;
            rig.Chatter.Refresh(new[] { Target(1, 1, 2), Follower(2) }); rig.Chatter.RequestAppointments(0);
            rig.Tick(1); var first = rig.Take(); var initial = first.Batch.Snapshot;
            Require(rig.AppointmentCaptures == 1 && rig.Captures == 0 && (string?)initial["appointment_context"]?["mission_briefing"] == "Protect the convoy." &&
                initial["appointments"]!.Count() == 2 && (string?)initial["appointments"]![0]?["class_name"] == "Destroyer" &&
                (bool?)initial["appointments"]![0]?["formation_leader"] == true && (bool?)initial["appointments"]![1]?["formation_leader"] == false,
                "Appointment input omitted unit context, repeated a briefing, or captured periodic situation unnecessarily");
            rig.AppointmentContext["mission_briefing"] = "Changed";
            Require((string?)first.Batch.Snapshot["appointment_context"]?["mission_briefing"] == "Protect the convoy.", "A later source mutation changed a queued appointment");
            rig.Complete(first); rig.Pump(1); var person = rig.Chatter.Person("unit:1")!;
            rig.Settings = new CrewSettings("chatter", 1, showRanks: false); rig.GamePaused = false; rig.Tick(100); rig.Tick(101);
            var speech = rig.Take(); var next = speech.Batch.Snapshot;
            Require(rig.AppointmentCaptures == 1 && next["appointment_context"] == null && next["appointments"]!.Count() == 0 &&
                (string?)next["speakers"]![0]?["person"]?["name"] == person.Name && (string?)next["speakers"]![0]?["person"]?["rank"] == person.Rank &&
                next["speakers"]![0]?["person"]?["rank_position"] == null && ReferenceEquals(person, rig.Chatter.Person("unit:1")),
                "Later speech resent the briefing or display OFF reappointed or lost the rank");
            rig.Complete(speech, "unit:1"); rig.Pump(101);
            rig.Chatter.Suspend(); rig.SetEnabled("unit:1", true, 102); rig.Tick(103); var resumed = rig.Take();
            Require(resumed.Batch.Snapshot["appointments"]!.Count() == 0 && ReferenceEquals(person, rig.Chatter.Person("unit:1")), "Re-enabling reappointed a session person");
            rig.Complete(resumed); rig.Pump(103);
        });
        check("Chatter information defaults to the existing coverage and persists independently of adviser choices", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(CrewInformation.Items.All(i => store.Current.Information.Allows(i.Id)) && !File.Exists(path), "Default coverage changed or opening saved settings");
            var information = CrewInformation.Default.With("fuel", false).With("contacts", false).With("threat_alerts", false)
                .With("weather", false).With("air_operations", false);
            store.Save(new CrewSettings("chatter-only", 5, 12, CrewNotificationPosition.BottomCenter, false, 72, information));
            var adviser = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
            adviser.SaveInformation(AdvisorInformation.Default.With(InformationKind.BattleSensors, false));
            var read = new CrewSettingsStore(path).Current;
            Require(read.Information.Key == information.Key && read.ConnectionId == "chatter-only" && read.IntervalMinutes == 5 && read.DisplaySeconds == 12 &&
                read.NotificationPosition == CrewNotificationPosition.BottomCenter && !read.ShowHelpTab && read.ExchangeChancePercent == 72,
                "Reloading Chatter or changing adviser coverage lost Chatter preferences");
            Require(read.Information.Allows("sensors") && adviser.Current.Information.Allows(InformationKind.BattleFuel) &&
                !read.Information.Allows("weather") && adviser.Current.Information.Allows(InformationKind.BattleWeather), "The features shared information selections");
            adviser.SaveInformation(adviser.Current.Information.With(InformationKind.BattleWeather, false));
            store.Save(new CrewSettings(information: CrewInformation.Default));
            Require(new CrewSettingsStore(path).Current.Information.Allows("weather") &&
                !new AdvisorOptionsStore(Path.Combine(directory, "options.json")).Current.Information.Allows(InformationKind.BattleWeather),
                "Crew weather settings changed the adviser's weather selection");
            store.Save(read);
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JArray(), new JObject { ["fuel"] = "false" }, new JObject { ["not_a_category"] = false } })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["information_selection"] = invalid }.ToString()));
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => store.Save(new CrewSettings(information: CrewInformation.Default)));
            Require(store.Current.Information.Key == information.Key, "Failed storage applied information edits");
        }));
        check("Chatter excludes disabled facts from provider input while retaining identities, formation and unrelated categories", () =>
        {
            var categories = new[]
            {
                ("movement", new[] { "position", "heading_deg", "speed_knots", "altitude_ft", "depth_ft" }),
                ("damage", new[] { "damage" }), ("fuel", new[] { "fuel" }),
                ("weapons", new[] { "weapons" }), ("sensors", new[] { "sensors" }),
                ("threat_alerts", new[] { "active_threat_alerts" }), ("air_operations", new[] { "air_operations" })
            };
            var selections = new List<CrewInformation> { CrewInformation.Default };
            selections.AddRange(CrewInformation.Items.Select(i => CrewInformation.Default.With(i.Id, false)));
            selections.Add(CrewInformation.Items.Aggregate(CrewInformation.Default, (p, i) => p.With(i.Id, false)));
            foreach (var information in selections)
            {
                using var rig = new Rig(settings: new CrewSettings("chatter", information: information));
                rig.Situation = ChatterSituation();
                rig.Situation["units"]![0]!["air_operations"] = new JObject { ["launch_handling"] = true, ["recovery_handling"] = false };
                var original = rig.Situation.DeepClone();
                rig.Chatter.Refresh(new[] { Target(1, 1, 2) }); rig.PreparePeople(); rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0); rig.Tick(1);
                var call = rig.Take(); var data = call.Batch.Snapshot;
                var units = (JArray)data["units"]!;
                Require((string?)units[0]["id"] == "unit:1" && (string?)units[1]["id"] == "unit:2" &&
                    units.All(u => u["display_name"] != null && u["nation"] != null && u["kind"] != null && u["acting_independently"] != null) &&
                    data["speakers"]![0]!["member_unit_ids"]!.Count() == 2 && (string?)data["reply_language"] == "en" && data["captured_at_utc"] != null,
                    "Optional selections removed the speaker, formation or basic context");
                foreach (var category in categories)
                    foreach (var field in category.Item2)
                        for (var i = 0; i < units.Count; i++)
                            Require((units[i][field] != null) == (information.Allows(category.Item1) && original["units"]![i]![field] != null),
                                "Provider input leaked or unexpectedly omitted " + category.Item1 + "/" + field);
                Require((data["contacts"] != null) == information.Allows("contacts") &&
                    (!information.Allows("contacts") || JToken.DeepEquals(data["contacts"], original["contacts"])), "Contact selection changed own-unit facts or disclosed excluded contacts");
                Require((data["launched_weapons"] != null) == information.Allows("launched_weapons") &&
                    (!information.Allows("launched_weapons") || JToken.DeepEquals(data["launched_weapons"], original["launched_weapons"])),
                    "Weapon selection leaked excluded facts or changed other categories");
                Require((data["weather"] != null) == information.Allows("weather") &&
                    (!information.Allows("weather") || JToken.DeepEquals(data["weather"], original["weather"])),
                    "Weather selection leaked excluded conditions or changed the shared weather snapshot");
                Require(JToken.DeepEquals(rig.Situation, original), "Selecting input mutated the captured situation");
                rig.Complete(call); rig.Pump(1);
            }
        });
        check("Chatter information changes retain visible history but exclude old and late-arriving remarks from later input", () =>
        {
            using var rig = new Rig(new FixedRandom(0));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0); rig.Tick(1);
            var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            rig.Settings = new CrewSettings("chatter", 1, information: CrewInformation.Default.With("fuel", false));
            rig.Tick(100); var reduced = rig.Take();
            Require(reduced.Batch.Snapshot["recent_remarks"] is JArray { Count: 0 } && rig.Chatter.History.Count == 1,
                "Old facts were resent or the player's displayed history was cleared");
            rig.Complete(reduced, "unit:1"); rig.Pump(100);
            rig.Settings = new CrewSettings("chatter", 1);
            rig.Tick(200); var restored = rig.Take();
            Require(restored.Batch.Snapshot["recent_remarks"] is JArray { Count: 0 }, "Re-enabling information revived an earlier context");
            rig.Complete(restored, "unit:1"); rig.Pump(200);
            rig.Tick(300); var same = rig.Take();
            Require(same.Batch.Snapshot["recent_remarks"] is JArray { Count: 1 }, "Unchanged information lost useful recent remarks");
            rig.Settings = new CrewSettings("chatter", 1, information: CrewInformation.Default.With("contacts", false));
            rig.Complete(same, "unit:1"); rig.Pump(300);
            rig.Tick(400); var next = rig.Take();
            Require(next.Batch.Snapshot["recent_remarks"] is JArray { Count: 0 } && rig.Chatter.History.Count == 4 &&
                rig.Chatter.Person("unit:1") != null && rig.Chatter.Enabled("unit:1"), "A late response leaked old facts or lost history, crew or selection");
            rig.Complete(next); rig.Pump(400);
        });
        check("Chatter API separates appointments from participation and validates scopes, permission and appointment state", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Target(3) }); var denied = true; var checkedConnection = 0;
            var api = new CrewLocalApi(rig.Chatter, () => 0, () => { checkedConnection++; if (denied) throw new LocalApiException(403, "connection_not_allowed"); });
            JObject Scope(string id) => new() { ["scope_id"] = id };
            JObject Selection(params string[] ids) => new() { ["scope_id"] = api.ScopeId, ["enabled"] = true, ["target_ids"] = new JArray(ids) };
            Throws<LocalApiException>(() => api.Dispatch("POST", CrewLocalApi.Path + "/appointments", Scope("old")));
            Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Selection("unit:99")));
            Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Selection("unit:1")));
            Require(checkedConnection == 0 && !rig.Chatter.AppointmentRequested("unit:1"), "Selection started appointment or invalid scope checked credentials");
            Throws<LocalApiException>(() => api.Dispatch("POST", CrewLocalApi.Path + "/appointments", Scope(api.ScopeId)));
            Require(!rig.Chatter.AppointmentRequested("unit:1"), "Denied connection queued work");
            denied = false; var accepted = api.Dispatch("POST", CrewLocalApi.Path + "/appointments", Scope(api.ScopeId));
            Require(accepted.Status == 200 && rig.Chatter.AppointmentRequested("unit:1") && !rig.Chatter.Enabled("unit:1") && rig.Requests == 0, "Appointment bypassed scheduling or enrolled before success");
            rig.Tick(1); var call = rig.Take(); rig.Complete(call); rig.Pump(1);
            var off = Selection("unit:1"); off["enabled"] = false;
            api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", off);
            api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Selection("unit:1")); rig.Tick(2);
            Require(rig.Requests == 1 && rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1") != null, "Participation recreated a person");
            var stopped = Scope(api.ScopeId); api.Dispatch("POST", CrewLocalApi.Path + "/stop", stopped);
            Require(!rig.Chatter.HasEnabledTargets && !rig.Chatter.HasPendingAppointments, "Stop retained automatic work");
        });
        check("Exchange preference is sampled only for multi-speaker opportunities and never creates catch-up requests", () =>
        {
            foreach (var (percent, count, draw, expected) in new[]
            {
                (0, 2, 0.0, false), (100, 0, 0.0, false), (100, 1, 0.0, false),
                (100, 2, 0.999999, true), (33, 2, 0.329999, true),
                (33, 2, 0.33, false), (70, 4, 0.699999, true), (70, 4, 0.7, false)
            })
            {
                using var rig = new Rig(new FixedRandom(draw), new CrewSettings("chatter", 1, 10, exchangeChancePercent: percent));
                var targets = count == 0 ? new[] { Follower(1) } : Enumerable.Range(1, count).Select(i => Target(i)).ToArray();
                rig.Chatter.Refresh(targets); rig.PreparePeople(); rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1);
                if (count == 0) { Require(rig.Requests == rig.SetupRequests && rig.Captures == 0, "An ineligible follower generated speech"); continue; }
                var call = rig.Take();
                Require((bool?)call.Batch.Snapshot["prefer_exchange"] == expected && rig.Requests - rig.SetupRequests == 1,
                    "The wrong opportunity style was sent, or multiple requests were made");
                rig.Complete(call); rig.Pump(1); rig.Tick(2);
                Require(rig.Requests - rig.SetupRequests == 1 && rig.Chatter.History.Count == 0 && !rig.Chatter.Paused,
                    "A preferred exchange forced speech, retried silence or paused Chatter");
            }
        });
        check("Exchange preference accepts silence and a single speaker and preserves the submitted reply order", () =>
        {
            var person = new CrewPerson("Crew", "Lt.", "calm");
            var batch = new CrewBatch(new JObject(), new Dictionary<string, CrewPerson?> { ["unit:1"] = person, ["unit:2"] = person },
                Array.Empty<string>(), preferExchange: true);
            Require(CrewTestGeneration.Read(batch, Reply(new JArray())).Remarks.Count == 0 && CrewTestGeneration.Read(batch, Reply(new JArray(Row("unit:1")))).Remarks.Count == 1,
                "Exchange preference became a two-speaker requirement");
            var result = CrewTestGeneration.Read(batch, Reply(new JArray(Row("unit:2"), Row("unit:1"))));
            Require(result.Remarks.Select(r => r.Id).SequenceEqual(new[] { "unit:2", "unit:1" }), "The reply was moved ahead of the first remark");
        });
        check("Appointment and speech jobs keep detached inputs and reject incomplete or unknown results independently", () =>
        {
            var batch = CrewTestGeneration.Appointments("unit:2", "unit:3");
            var snapshot = batch.Snapshot; snapshot["appointments"] = new JArray();
            Require(batch.Snapshot["appointments"]!.Count() == 2, "A caller changed a queued appointment input");
            var names = new JArray(AppointmentRow("unit:3"), AppointmentRow("unit:2", "New"));
            var result = CrewTestGeneration.Read(batch, Reply(new JArray(), names));
            Require(result.Remarks.Count == 0 && result.Appointments["unit:2"].Person.Name == "New", "Silence prevented appointment");
            foreach (var invalid in new[] { new JArray(AppointmentRow("unit:2")), new JArray(AppointmentRow("unit:2"), AppointmentRow("unit:2")), new JArray(AppointmentRow("unit:2"), AppointmentRow("unit:99")) })
                Throws<CrewException>(() => CrewTestGeneration.Read(batch, Reply(new JArray(), invalid)));
            var person = new CrewPerson("Existing", "Cmdr.", "Calm");
            var speech = new CrewBatch(new JObject(), new Dictionary<string, CrewPerson?> { ["unit:1"] = person, ["unit:2"] = person }, Array.Empty<string>());
            Require(ReferenceEquals(CrewTestGeneration.Read(speech, Reply(new JArray(Row("unit:1")))).Remarks.Single().Person, person), "Speech changed an existing person");
            foreach (var invalid in new[] { new JArray(Row("unit:1"), Row("unit:2"), Row("unit:3")), new JArray(Row("unit:1"), Row("unit:1")), new JArray(Row("unit:1"), Row("unit:99")) })
                Throws<CrewException>(() => CrewTestGeneration.Read(speech, Reply(invalid)));
            Throws<CrewException>(() => CrewTestGeneration.Read(batch, new AiReply("plain text", Array.Empty<AiToolCall>(), null)));
        });
        check("Slow appointment completion remains valid while time-sensitive remarks retain their existing lifetime", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1) });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var job = rig.Take();
            rig.Complete(job); rig.Pump(301);
            Require(rig.Chatter.Person("unit:1") != null && rig.Chatter.Enabled("unit:1") && !rig.Chatter.HasPendingAppointments && rig.Chatter.History.Count == 0,
                "Speech freshness discarded a still-current appointment or generated speech during game pause");
        });
        check("Additional appointments preserve existing OFF choices and existing people's identities", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; var first = Target(1); var second = Target(2);
            rig.Chatter.Refresh(new[] { first, second }); rig.Chatter.RequestAppointments(0); rig.Tick(1);
            var initial = rig.Take(); rig.Complete(initial); rig.Pump(1); var person = rig.Chatter.Person("unit:1");
            rig.SetEnabled("unit:1", false, 1); rig.Chatter.Refresh(new[] { first, second, Target(3) }); rig.Tick(100);
            Require(rig.Requests == 1 && rig.Chatter.Person("unit:3") == null && !rig.Chatter.Enabled("unit:3"), "A new unit appointed itself");
            rig.Chatter.RequestAppointments(100); rig.Tick(101); var next = rig.Take();
            Require(next.Batch.Snapshot["appointments"] is JArray { Count: 1 } && (string?)next.Batch.Snapshot["appointments"]![0]!["id"] == "unit:3", "Existing people were regenerated");
            rig.Chatter.ApplyTargets(Array.Empty<string>(), 101);
            Require(!next.Token.IsCancellationRequested, "Participation OFF cancelled an independent appointment");
            rig.Complete(next); rig.Pump(101);
            Require(!rig.Chatter.Enabled("unit:1") && !rig.Chatter.Enabled("unit:2") && rig.Chatter.Enabled("unit:3") && ReferenceEquals(person, rig.Chatter.Person("unit:1")), "Additional appointment reset an existing OFF choice or person");
            rig.Chatter.RequestAppointments(200); rig.Tick(201); Require(rig.Requests == 2, "Already appointed units sent another appointment request");
        });
        check("Automatic appointments require successful initial appointment and continue with every existing participant OFF", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1), Follower(2) }); rig.Tick(0); rig.Tick(1000);
            Require(!rig.Chatter.Busy && rig.Requests == 0, "The option started initial appointment without the user");
            rig.Chatter.RequestAppointments(1000); rig.Tick(1001); var initial = rig.Take(); rig.Complete(initial); rig.Pump(1001);
            var existing = rig.Chatter.Person("unit:1"); rig.Chatter.ApplyTargets(Array.Empty<string>(), 1001);
            rig.Tick(1002);
            Require(!rig.Chatter.Busy && rig.Requests == 1, "An empty automatic check sent an API request");
            rig.Chatter.Refresh(new[] { Target(1), Follower(2), Target(3), Follower(4) }); rig.Tick(1061);
            Require(!rig.Chatter.Busy, "New units bypassed the configured opportunity interval");
            rig.Tick(1062); var added = rig.Take(); var input = added.Batch.Snapshot;
            Require(input["appointments"]!.Select(t => (string)t["id"]!).OrderBy(id => id).SequenceEqual(new[] { "unit:3", "unit:4" }) &&
                input["speakers"] is JArray { Count: 0 }, "Automatic selection omitted a follower or regenerated existing people");
            rig.Complete(added); rig.Pump(1062);
            Require(ReferenceEquals(existing, rig.Chatter.Person("unit:1")) && !rig.Chatter.Enabled("unit:1") && !rig.Chatter.Enabled("unit:2") &&
                rig.Chatter.Enabled("unit:3") && rig.Chatter.Enabled("unit:4"), "Automatic appointment changed old people or old OFF choices");
        });
        check("Enabling appointments includes the unappointed backlog and disabling rejects pending automatic results without blocking manual work", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0);
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(500);
            Require(!rig.Chatter.Busy && rig.Requests == 1, "Default OFF appointed an additional unit");
            rig.Settings = new CrewSettings("chatter", 1, autoAppointments: true); rig.Tick(500); rig.Tick(559);
            Require(!rig.Chatter.Busy, "Enabling sent immediately instead of waiting for the next opportunity");
            rig.Tick(560); var automatic = rig.Take();
            rig.Settings = new CrewSettings("chatter", 1); rig.Tick(560); rig.Complete(automatic); rig.Pump(560);
            Require(rig.Chatter.Person("unit:2") == null && !rig.Chatter.HasPendingAppointments && !rig.Chatter.Paused,
                "Disabling retained automatic work or paused unrelated Chatter");
            rig.GamePaused = true; rig.Chatter.RequestAppointments(560); rig.Tick(561); var manual = rig.Take(); rig.Complete(manual); rig.Pump(561);
            Require(rig.Chatter.Person("unit:2") != null && rig.Chatter.Enabled("unit:2"), "Disabling automation blocked explicit paused-game appointment");
        });
        check("Game pause freezes automatic opportunities while sent appointments can finish normally", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0);
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(20); rig.GamePaused = true; rig.Tick(20); rig.Tick(500);
            Require(!rig.Chatter.Busy && rig.Requests == 1, "A paused game started automatic appointment");
            rig.GamePaused = false; rig.Tick(500); rig.Tick(539);
            Require(!rig.Chatter.Busy, "Game pause consumed the remaining automatic countdown");
            rig.Tick(540); var automatic = rig.Take(); rig.GamePaused = true; rig.Tick(541); rig.Complete(automatic); rig.Pump(600);
            Require(rig.Chatter.Person("unit:2") != null && rig.Chatter.Enabled("unit:2") && rig.Requests == 2 && !rig.Chatter.Paused,
                "Game pause discarded a sent appointment or started another generation");
        });
        check("Manual pause stops automatic checks until explicit resume", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0);
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Chatter.SetPaused(true, 20); rig.Tick(1000);
            Require(!rig.Chatter.Busy && rig.Requests == 1 && rig.Chatter.Person("unit:2") == null, "Manual pause permitted automatic work");
            rig.Chatter.SetPaused(false, 1000); rig.Tick(1000); rig.Tick(1059);
            Require(!rig.Chatter.Busy, "Resume bypassed the automatic interval with no speakers");
            rig.Tick(1060); var resumed = rig.Take(); rig.Complete(resumed); rig.Pump(1060);
            Require(rig.Chatter.Person("unit:2") != null, "Automatic work did not resume");
        });
        check("Large automatic batches stop queued automatic work when disabled and preserve explicit appointment requests", () =>
        {
            foreach (var explicitRemainder in new[] { false, true })
            {
                using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
                rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0);
                var targets = Enumerable.Range(1, CrewBatch.MaximumAppointments + 3).Select(i => Target(i)).ToArray();
                rig.Chatter.Refresh(targets); rig.Tick(60); var first = rig.Take();
                Require(first.Batch.Snapshot["appointments"]!.Count() == CrewBatch.MaximumAppointments, "Automatic batch exceeded the existing batch boundary");
                rig.Complete(first); rig.Pump(60);
                if (explicitRemainder) rig.Chatter.RequestAppointments(60);
                rig.Settings = new CrewSettings("chatter", 1); rig.GamePaused = true; rig.Tick(61);
                if (explicitRemainder)
                {
                    var last = rig.Take(); Require(last.Batch.Snapshot["appointments"]!.Count() == 2, "Explicit remainder was cancelled or enlarged");
                    rig.Complete(last); rig.Pump(61);
                    Require(targets.All(t => rig.Chatter.Person(t.Id) != null), "An explicit request was lost when automatic appointments were disabled");
                }
                else Require(!rig.Chatter.Busy && !rig.Chatter.HasPendingAppointments && rig.Requests == 2 &&
                    targets.Count(t => rig.Chatter.Person(t.Id) == null) == 2, "Disabling scheduled another automatic batch");
            }
        });
        check("Automatic appointment shares the next Chatter opportunity even after a silent response", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var silent = rig.Take(); rig.Complete(silent); rig.Pump(1);
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(60);
            Require(!rig.Chatter.Busy, "Silence or a new unit shortened the interval");
            rig.Tick(61); var next = rig.Take();
            Require(next.Batch.Snapshot["appointments"] is JArray { Count: 1 } && next.Batch.Snapshot["speakers"] is JArray { Count: 0 },
                "Silence prevented automatic appointment or forced speech into appointment work");
            rig.Complete(next); rig.Pump(71); rig.Tick(130);
            Require(!rig.Chatter.Busy, "The next cycle ignored automatic generation completion time");
            rig.Tick(131); var speech = rig.Take(); rig.Complete(speech); rig.Pump(131);
        });
        check("Automatic response failure pauses Chatter without changing existing participation and does not retry itself", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0); var existing = rig.Chatter.Person("unit:1");
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(60); var failed = rig.Take();
            failed.Completion.SetException(new CrewException("invalid_response")); rig.Pump(60); rig.Tick(1000);
            Require(rig.Chatter.Paused && rig.Chatter.Error == "invalid_response" && rig.Requests == 2 && !rig.Chatter.Enabled("unit:1") &&
                ReferenceEquals(existing, rig.Chatter.Person("unit:1")) && rig.Chatter.Person("unit:2") == null,
                "Automatic failure changed old state or kept requesting without resume");
        });
        check("Automatic results for removed units are discarded and later arrivals wait for the next opportunity", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("chatter", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.Tick(0);
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(60); var removed = rig.Take();
            rig.Chatter.Refresh(new[] { Target(1), Target(3) }); rig.Complete(removed); rig.Pump(60); rig.Tick(119);
            Require(rig.Chatter.Person("unit:2") == null && rig.Chatter.Person("unit:3") == null && !rig.Chatter.Paused && !rig.Chatter.Busy,
                "A stale response was rebound to a later unit or bypassed the next opportunity");
            rig.Tick(120); var next = rig.Take(); rig.Complete(next); rig.Pump(120);
            Require(rig.Chatter.Person("unit:3") != null && rig.Chatter.Person("unit:2") == null, "The next arrival was not appointed independently");
        });
        check("Repeated game pauses preserve the remaining speech countdown without catch-up requests", () =>
        {
            foreach (var draw in new[] { 0.0, 0.5, 0.999999 })
            {
                using var rig = new Rig(new FixedRandom(draw));
                rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
                rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
                rig.Tick(21); rig.GamePaused = true; rig.Tick(21); rig.Tick(500);
                Require(rig.Requests - rig.SetupRequests == 1 && !rig.Chatter.Paused, "Game pause started a generation or changed the manual pause state");
                rig.GamePaused = false; rig.Tick(500); rig.Tick(520);
                Require(rig.Requests - rig.SetupRequests == 1, "Resuming caught up requests or lost the remaining countdown");
                rig.GamePaused = true; rig.Tick(520); rig.Tick(900);
                rig.GamePaused = false; rig.Tick(900); rig.Tick(919);
                Require(rig.Requests - rig.SetupRequests == 1, "A second pause consumed or reset the remaining time");
                rig.Tick(920); var next = rig.Take();
                Require(rig.Requests - rig.SetupRequests == 2 && rig.Captures == 2, "The remaining active real-time interval did not produce one generation");
                rig.Complete(next); rig.Pump(920);
            }
        });
        check("In-flight speech completes during game pause and its next countdown starts after game resume", () =>
        {
            using var rig = new Rig(new FixedRandom(0.5));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.GamePaused = true; rig.Tick(2);
            Require(!first.Token.IsCancellationRequested, "Game pause cancelled an already sent request");
            rig.Complete(first, "unit:1"); rig.Pump(100);
            Require(rig.Chatter.History.Count == 1 && rig.Chatter.Person("unit:1") != null && rig.Requests - rig.SetupRequests == 1,
                "Game pause discarded a valid response or started another generation");
            rig.GamePaused = false; rig.Tick(100); rig.Tick(159);
            Require(rig.Requests - rig.SetupRequests == 1, "Waiting for the response while paused consumed the next countdown");
            rig.Tick(160); var next = rig.Take(); rig.Complete(next); rig.Pump(160);
        });
        check("Game pause keeps response freshness and queued notification expiry on real time", () =>
        {
            using (var rig = new Rig(new FixedRandom(0.5)))
            {
                rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
                rig.Tick(1); var first = rig.Take(); rig.GamePaused = true; rig.Tick(2);
                rig.Complete(first, "unit:1"); rig.Pump(182);
                Require(rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") != null && rig.Requests - rig.SetupRequests == 1,
                    "Game pause extended the lifetime of a stale response");
            }
            using (var rig = new Rig(new FixedRandom(0.5)))
            {
                rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
                rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
                rig.GamePaused = true; rig.Tick(2); rig.Tick(200);
                Require(rig.Chatter.History.Count == 1 && rig.Chatter.NextNotification(200) == null && rig.Requests - rig.SetupRequests == 1,
                    "Game pause extended a pending popup's lifetime or generated new speech");
            }
        });
        check("Explicit appointments run during game pause and enroll all units only after successful generation", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Follower(2) });
            Throws<ArgumentException>(() => rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0)); rig.Tick(10);
            Require(rig.Requests == 0 && !rig.Chatter.HasEnabledTargets, "An unappointed checkbox started work");
            rig.Chatter.RequestAppointments(10); rig.Tick(11); var call = rig.Take();
            Require(call.Batch.Snapshot["appointments"] is JArray { Count: 2 } && call.Batch.Snapshot["speakers"] is JArray { Count: 0 } && rig.Captures == 0 && !rig.Chatter.HasEnabledTargets, "Appointment selection used participation or captured speech data");
            rig.Complete(call); rig.Pump(11); rig.Tick(1000);
            Require(rig.Requests == 1 && rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:2") && rig.Chatter.History.Count == 0, "Paused appointments did not enroll everyone or forced speech");
            rig.GamePaused = false; rig.Tick(1000); rig.Tick(1001); var speech = rig.Take();
            Require(speech.Batch.Snapshot["appointments"] is JArray { Count: 0 } && speech.Batch.Snapshot["speakers"] is JArray { Count: 1 }, "Speech ignored formation eligibility or reappointed people");
            rig.Complete(speech, "unit:1"); rig.Pump(1001);
        });
        check("Silence is successful, does not trigger catch-up requests, and candidates rotate without enabling other targets", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, 6).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets); rig.PreparePeople();
            foreach (var t in targets.Take(5)) rig.SetEnabled(t.Id, true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Tick(20);
            Require(rig.Captures == 1, "A second request overlapped the first");
            var ids = ((JArray)first.Batch.Snapshot["speakers"]!).Select(x => (string)x["id"]!).ToArray();
            Require(ids.Length == 4, "The batch bound was ignored");
            rig.Complete(first); rig.Pump(20); rig.Tick(79.999);
            Require(rig.Captures == 1 && rig.Chatter.History.Count == 0 && rig.Chatter.Error == null && targets.Take(5).All(t => rig.Chatter.Person(t.Id) != null),
                "Silence caused an error, lost appointments, or triggered an immediate speech request");
            rig.Tick(80); var second = rig.Take();
            var last = ((JArray)second.Batch.Snapshot["speakers"]!).Select(x => (string)x["id"]!).ToArray();
            var omitted = targets.Take(5).Single(t => !ids.Contains(t.Id)).Id;
            Require(last.Length == 4 && last.Contains(omitted) && !last.Contains("unit:6") && !rig.Chatter.Enabled("unit:6"),
                "Candidate selection starved a previously omitted unit or included a disabled one");
            rig.Complete(second, last[0]); rig.Pump(80);
            Require(rig.Chatter.History.Count == 1 && last.All(id => rig.Chatter.Person(id) != null), "A silent candidate lost its appointment or was forced to speak");
        });
        check("Disabling and re-enabling a target rejects its old response and pending notification", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.SetEnabled("unit:1", false, 1); rig.SetEnabled("unit:1", true, 1);
            rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") != null && rig.Chatter.RankBeforeVotes == 1 && rig.Chatter.RankAfterVotes == 0, "A stale response lost an existing person or recounted its rank vote");
            rig.Tick(2); var fresh = rig.Take(); rig.Complete(fresh, "unit:1"); rig.Pump(2);
            Require(rig.Chatter.History.Count == 1, "A fresh enabled response was lost");
            rig.SetEnabled("unit:1", false, 2); rig.SetEnabled("unit:1", true, 2);
            Require(rig.Chatter.NextNotification(2) == null, "A previous activation's notification survived toggling");
        });
        check("Global suspension cancels in-flight work, turns targets off, preserves session people and rejects late results", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            var person = rig.Chatter.Person("unit:1"); rig.Tick(77); var late = rig.Take(); rig.Chatter.Suspend();
            Require(late.Token.IsCancellationRequested && !rig.Chatter.Enabled("unit:1") && rig.Chatter.NextNotification(77) == null, "Suspension retained work or pending notifications");
            rig.Complete(late, "unit:1"); rig.SetEnabled("unit:1", true, 77); rig.Tick(78); var fresh = rig.Take();
            rig.Complete(fresh, "unit:1"); rig.Pump(78);
            Require(rig.Chatter.History.Count == 2 && rig.Chatter.Person("unit:1") == person, "A late result survived suspension or replaced the existing appointment");
        });
        check("Formation changes invalidate a captured remark while a returning reporter retains its appointed person", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.Chatter.Refresh(new[] { Target(1, 1, 3) }); rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Chatter.History.Count == 0, "A remark about the former formation was applied");
            rig.Tick(77); var current = rig.Take(); rig.Complete(current, "unit:1"); rig.Pump(77); var person = rig.Chatter.Person("unit:1");
            rig.Chatter.Refresh(Array.Empty<CrewTarget>()); rig.Chatter.Refresh(new[] { Target(1) });
            Require(rig.Chatter.Person("unit:1") == person && person != null && !rig.Chatter.Enabled("unit:1"), "Returning to eligibility lost the person or silently resumed speaking");
        });
        check("All units including followers are appointed while current formation controls speech", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2, 3), Follower(2), Follower(3) });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var call = rig.Take();
            Require(call.Batch.Snapshot["appointments"] is JArray { Count: 3 } && call.Batch.Snapshot["speakers"] is JArray { Count: 0 }, "Initial appointment depended on speech eligibility");
            rig.Complete(call); rig.Pump(1); var person = rig.Chatter.Person("unit:2");
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 1, 2, 3), Follower(3) }); rig.Tick(2); var speech = rig.Take();
            Require(speech.Batch.Snapshot["speakers"] is JArray { Count: 1 } && (string?)speech.Batch.Snapshot["speakers"]![0]!["id"] == "unit:2" && speech.Batch.Snapshot["appointments"] is JArray { Count: 0 }, "Promotion required reappointment or retained an old leader");
            rig.Complete(speech, "unit:2"); rig.Pump(2);
            Require(ReferenceEquals(person, rig.Chatter.Person("unit:2")), "Promotion replaced the crew");
        });
        check("A follower can be appointed and opted out before promotion without losing its person", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Follower(2) }); rig.Chatter.RequestAppointments(0); rig.Tick(1);
            var call = rig.Take(); rig.Complete(call); rig.Pump(1); var person = rig.Chatter.Person("unit:2");
            rig.SetEnabled("unit:2", false, 1); rig.Chatter.Refresh(new[] { Target(2) }); rig.Tick(100);
            Require(rig.Requests == 1 && !rig.Chatter.Enabled("unit:2") && rig.Chatter.Person("unit:2") == person, "Promotion changed an explicit OFF selection");
            rig.SetEnabled("unit:2", true, 100); rig.Tick(101); var speech = rig.Take();
            Require(speech.Batch.Snapshot["appointments"] is JArray { Count: 0 }, "Re-enabling recreated a person");
            rig.Complete(speech); rig.Pump(101);
        });
        check("Formation changes during appointment retain identities while removed units discard late appointments", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1, 1, 2), Follower(2), Target(3) });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var call = rig.Take();
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 1, 2) }); rig.Complete(call); rig.Pump(1);
            Require(rig.Chatter.Person("unit:1") != null && rig.Chatter.Person("unit:2") != null && rig.Chatter.Person("unit:3") == null && rig.Chatter.History.Count == 0 && rig.Chatter.RankBeforeVotes == 2, "Formation change lost people or a removed unit cast a vote");
        });
        check("Target edits, bulk selection and discard remain UI-only until a complete selection is applied", () =>
        {
            using var rig = new Rig(); var targets = new[] { Target(1), Target(2), Target(3) }; rig.Chatter.Refresh(targets); rig.PreparePeople();
            var draft = new CrewTargetSelection(); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            draft.SetAll(true); rig.Tick(100);
            Require(draft.Dirty && draft.Selected.Count == 3 && rig.Requests - rig.SetupRequests == 0 && !rig.Chatter.HasEnabledTargets, "Editing checkboxes changed the running selection");
            draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            Require(draft.Dirty && draft.Selected.Count == 3, "A background refresh erased pending edits");
            draft.Discard(); Require(!draft.Dirty && draft.Selected.Count == 0, "Discard retained staged targets");
            draft.SetAll(true); rig.Chatter.ApplyTargets(draft.Selected, 100); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            Require(!draft.Dirty && rig.Chatter.HasEnabledTargets && rig.Requests - rig.SetupRequests == 0, "Apply did not commit atomically or sent outside the scheduler");
            rig.Chatter.Refresh(targets.Concat(new[] { Target(4) }).ToArray()); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            Require(!draft.Checked("unit:4") && !rig.Chatter.Enabled("unit:4"), "A previous bulk selection automatically enabled a new arrival");
            draft.SetAll(true); Require(!draft.Selected.Contains("unit:4"), "Bulk Chatter selection included an unappointed unit");
            draft.Set("unit:2", false); rig.Chatter.ApplyTargets(new[] { "unit:2", "unit:3" }, 100); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            Require(draft.Selected.SequenceEqual(new[] { "unit:3" }), "A draft restored a background-disabled target");
            draft.Discard(); draft.Set("unit:3", false); rig.Chatter.Refresh(targets.Take(2).ToArray()); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled, id => rig.Chatter.Person(id) != null);
            Require(!draft.Dirty, "A vanished target left unresolvable pending edits");
            Throws<ArgumentException>(() => rig.Chatter.ApplyTargets(new[] { "unit:1", "unit:99" }, 100));
            Require(!rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:2"), "An invalid complete selection partly applied");
        });
        check("Pause retains pending explicit appointments and blocks communication until resume", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Target(2) });
            rig.Chatter.SetPaused(true, 0); rig.Chatter.RequestAppointments(0); rig.Tick(1000);
            Require(rig.Requests == 0 && rig.Chatter.HasPendingAppointments && !rig.Chatter.HasEnabledTargets, "A paused appointment communicated or enrolled early");
            rig.Chatter.SetPaused(false, 1000); rig.Tick(1001); var first = rig.Take(); rig.Complete(first); rig.Pump(1001);
            var person = rig.Chatter.Person("unit:1"); rig.Chatter.SetPaused(true, 1002); rig.Tick(2000);
            Require(rig.Requests == 1 && rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1") == person, "Pause lost selection or continued communicating");
        });
        check("Pausing cancels an in-flight appointment and its late result cannot enroll a unit", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.Chatter.RequestAppointments(0); rig.Tick(1); var old = rig.Take();
            rig.Chatter.SetPaused(true, 1); rig.Complete(old); rig.Tick(100);
            Require(old.Token.IsCancellationRequested && !rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1") == null && rig.Chatter.HasPendingAppointments, "Cancelled appointment enrolled a person or lost the pending user request");
            rig.Chatter.SetPaused(false, 100); rig.Tick(101); var fresh = rig.Take(); rig.Complete(fresh); rig.Pump(101);
            Require(rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1") != null, "Resume did not finish explicit appointment");
        });
        check("Large explicit appointment batches enroll successful units and never capture speech data", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; var targets = Enumerable.Range(1, CrewBatch.MaximumAppointments + 1).Select(i => Target(i)).ToArray();
            rig.Chatter.Refresh(targets); rig.Chatter.RequestAppointments(0); rig.Tick(1); var first = rig.Take(); rig.Complete(first); rig.Pump(1);
            Require(targets.Count(t => rig.Chatter.Enabled(t.Id)) == CrewBatch.MaximumAppointments && rig.Chatter.HasPendingAppointments, "A partial batch enrolled missing people or lost remaining appointments");
            rig.Tick(2); var second = rig.Take(); var data = second.Batch.Snapshot;
            Require(rig.Captures == 0 && data["units"] == null && data["speakers"] is JArray { Count: 0 } && data["appointments"] is JArray { Count: 1 }, "Appointment-only work forced a speech request");
            rig.Complete(second); rig.Pump(2);
            Require(targets.All(t => rig.Chatter.Person(t.Id) != null && rig.Chatter.Enabled(t.Id)) && !rig.Chatter.HasPendingAppointments, "Some appointments failed to enroll");
        });
        check("Mission usage survives history trimming, selection changes, pause and global suspension but resets for a new mission", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople(); rig.SetEnabled("unit:1", true, 0);
            for (var i = 0; i < 201; i++)
            {
                var now = 1 + i * 80; rig.Tick(now); var request = rig.Take(); rig.Complete(request, "unit:1"); rig.Pump(now);
            }
            var usage = rig.Chatter.Usage.Snapshot;
            Require(rig.Chatter.History.Count == 200 && usage.ApiRequests == 201 + rig.SetupRequests && usage.KnownTokens == (201 + rig.SetupRequests) * 12 && usage.UnknownRequests == 0 && usage.LastTokens == 12,
                "Usage followed retained history instead of all completed requests");
            rig.Chatter.SetPaused(true, 20000); rig.Chatter.SetPaused(false, 20000); rig.SetEnabled("unit:1", false, 20000);
            rig.SetEnabled("unit:1", true, 20000); rig.Chatter.Suspend();
            var api = new CrewLocalApi(rig.Chatter, () => 20000, () => { }); var status = api.Status();
            Require(ReferenceEquals(rig.Chatter.Usage.Snapshot, usage) && (int?)status["usage"]!["api_requests"] == 201 + rig.SetupRequests && (long?)status["usage"]!["known_total_tokens"] == (201 + rig.SetupRequests) * 12,
                "A lifecycle operation reset usage or the developer API reported different totals");
            using var nextMission = new CrewChatter(_ => { });
            Require(nextMission.Usage.Snapshot.ApiRequests == 0 && nextMission.Usage.Snapshot.KnownTokens == 0 && nextMission.Usage.Snapshot.LastTokens == null,
                "A new mission inherited the previous mission's usage");
        });
        check("Cancelled appointments retain consumed usage without enrolling a person", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.Chatter.RequestAppointments(0);
            rig.Tick(1); var pending = rig.Take(); rig.Chatter.SetPaused(true, 1); rig.Complete(pending);
            Require(SpinWait.SpinUntil(() => rig.Chatter.Usage.Snapshot.ApiRequests == 1, 3000), "Late usage was lost");
            rig.Tick(100);
            Require(rig.Chatter.Paused && rig.Chatter.Usage.Snapshot.KnownTokens == 12 && rig.Chatter.Person("unit:1") == null && !rig.Chatter.Enabled("unit:1"), "Late result enrolled or lost usage");
        });
        check("A failed generation pauses all Chatter without changing selection, people or history and resumes only explicitly", () =>
        {
            foreach (var failure in new Exception[] { new CrewException("invalid_response"), new AiConnectionException("api_error", 503) })
            {
                using var rig = new Rig(); var targets = Enumerable.Range(1, 6).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets); rig.PreparePeople();
                rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1); var initial = rig.Take();
                var first = (string)initial.Batch.Snapshot["speakers"]![0]!["id"]!; rig.Complete(initial, first); rig.Pump(1);
                var people = targets.ToDictionary(t => t.Id, t => rig.Chatter.Person(t.Id));
                rig.Tick(77); var failed = rig.Take(); failed.Completion.SetException(failure); rig.Pump(77);
                Require(rig.Chatter.Paused && rig.Chatter.Error != null && rig.Errors.Count == 1 && targets.All(t => rig.Chatter.Enabled(t.Id)) &&
                    targets.All(t => rig.Chatter.Person(t.Id) == people[t.Id]) && rig.Chatter.History.Count == 1 && rig.Chatter.NextNotification(77) == null,
                    "An error lost selection, people or history, or did not pause the whole session");
                rig.Tick(1000); Require(rig.Requests - rig.SetupRequests == 2 && !rig.Chatter.Busy && rig.Chatter.Usage.Snapshot.ApiRequests == 2 + rig.SetupRequests,
                    "An error automatically retried or lost request accounting");
                var selected = rig.Chatter.Targets.Select(t => t.Id).ToArray(); rig.Chatter.ApplyTargets(selected.Take(5).ToArray(), 1000); rig.Tick(1000);
                Require(rig.Chatter.Paused && rig.Requests - rig.SetupRequests == 2, "Editing selection silently resumed an error pause");
                rig.Chatter.SetPaused(false, 1000); Require(rig.Chatter.Error == null && !rig.Chatter.Paused, "Explicit resume retained an error or paused state");
                rig.Tick(1001); var resumed = rig.Take();
                Require(resumed.Batch.Snapshot["appointments"] is JArray { Count: 0 }, "Resume reappointed existing people");
                rig.Complete(resumed); rig.Pump(1001); Require(!rig.Chatter.Paused && rig.Chatter.Error == null, "A successful resumed request retained an error");
            }
        });
        check("Failed explicit appointments pause without enrolling units and retry only on explicit resume", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1) }); rig.Chatter.RequestAppointments(0);
            rig.Tick(1); var failed = rig.Take(); failed.Completion.SetException(new CrewException("invalid_response")); rig.Pump(1);
            Require(rig.Chatter.Paused && rig.Chatter.Error == "invalid_response" && !rig.Chatter.HasEnabledTargets && rig.Chatter.Person("unit:1") == null && rig.Chatter.HasPendingAppointments, "Failed appointment enrolled a unit or discarded the user request");
            rig.Tick(1000); Require(rig.Requests == 1 && !rig.Chatter.Busy, "A failed appointment automatically retried");
            rig.Chatter.SetPaused(false, 1000); rig.Tick(1001); var next = rig.Take(); rig.Complete(next); rig.Pump(1001);
            Require(rig.Chatter.Person("unit:1") != null && rig.Chatter.Enabled("unit:1") && !rig.Chatter.Paused, "Explicit resume did not finish appointment");
        });
        check("A missing appointment connection pauses without enrolling any units or sending a request", () =>
        {
            using var chatter = new CrewChatter(_ => { }); chatter.Refresh(new[] { Target(1), Target(2) }); chatter.RequestAppointments(0);
            chatter.Tick(1, false, new CrewSettings(), "en", _ => throw new InvalidOperationException("Unexpected capture"), _ => throw new InvalidOperationException("Unexpected appointment capture"), (_, _, _, _) => throw new InvalidOperationException("Unexpected request"));
            Require(chatter.Paused && chatter.Error == "connection_required" && !chatter.HasEnabledTargets && chatter.HasPendingAppointments && chatter.Usage.Snapshot.ApiRequests == 0, "Missing configuration enrolled units, lost the user request, or communicated");
        });

        check("A manual appointment request can adopt an already running automatic appointment without another request", () =>
        {
            using var rig = new Rig(settings: new CrewSettings("crew", 1, autoAppointments: true));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(59); var automatic = rig.Take();
            rig.Chatter.RequestAppointments(59); rig.Settings = new CrewSettings("crew", 1, autoAppointments: false);
            rig.Complete(automatic); rig.Pump(59);
            Require(rig.Chatter.Person("unit:2") != null && rig.Chatter.Enabled("unit:2") && !rig.Chatter.HasPendingAppointments && rig.Requests == rig.SetupRequests + 1,
                "Manual promotion discarded useful results or scheduled duplicate appointments");
        });
        check("Person edits preserve participation and reach future Chatter without appointment requests or portrait data", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.PreparePeople();
            var other = rig.Chatter.Person("unit:2"); var original = rig.Chatter.Person("unit:1")!;
            var personality = "Speak gently.\n" + new string('x', 241);
            var edited = new CrewPerson("Edited Person", "", personality, "11111111111111111111111111111111.png");
            rig.Chatter.UpdatePerson("unit:1", original, edited);
            Require(!rig.Chatter.Enabled("unit:1") && ReferenceEquals(other, rig.Chatter.Person("unit:2")) && rig.Requests == rig.SetupRequests,
                "Editing enrolled a unit, replaced someone else or sent an AI request");
            Require(edited.DisplayName(true, true) == edited.Name && edited.Personality.Length == 255, "Empty ranks or long personality were changed");
            rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0); rig.Tick(1); var call = rig.Take(); var data = call.Batch.Snapshot;
            var person = data["speakers"]![0]!["person"]!;
            Require((string?)person["personality"] == personality && (string?)person["name"] == edited.Name &&
                ((JArray)data["appointments"]!).Count == 0 && !person.ToString().Contains(edited.PortraitId!), "Future Chatter lost the edited persona or sent local image references");
            rig.Complete(call); rig.Pump(1);
        });
        check("Editing retains historical people but invalidates their pending notifications and current popup eligibility", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            rig.SetEnabled("unit:1", true, 0); rig.Tick(1); var call = rig.Take(); rig.Complete(call, "unit:1"); rig.Pump(1);
            var old = rig.Chatter.Person("unit:1")!; var remark = rig.Chatter.History.Single();
            Require(rig.Chatter.IsCurrent(remark), "New remark was not eligible");
            rig.Chatter.UpdatePerson("unit:1", old, new CrewPerson("Edited", "Capt.", "Expressive"));
            Require(ReferenceEquals(remark.Submission.Person, old) && !rig.Chatter.IsCurrent(remark) && rig.Chatter.NextNotification(2) == null,
                "An edit rewrote history or retained a stale notification");
            rig.Tick(61); var next = rig.Take();
            Require(((JArray)next.Batch.Snapshot["recent_remarks"]!).Count == 0, "Old-person remarks were reused as the edited persona's recent context");
            rig.Complete(next); rig.Pump(61);
        });
        check("Editing during generation rejects only that person's stale remarks", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.PreparePeople();
            rig.Chatter.ApplyTargets(new[] { "unit:1", "unit:2" }, 0); rig.Tick(1); var call = rig.Take();
            rig.Chatter.UpdatePerson("unit:1", rig.Chatter.Person("unit:1")!, new CrewPerson("Edited", "Lt.", "Calm"));
            rig.Complete(call, "unit:1", "unit:2"); rig.Pump(2);
            Require(rig.Chatter.History.Count == 1 && rig.Chatter.History[0].Target.Id == "unit:2" && !rig.Chatter.Paused,
                "Stale remarks were relabeled or another person's valid remark was discarded");
        });
        check("Individual reappointment keeps the old person until success, preserves participation and resets the portrait", () =>
        {
            using var rig = new Rig(); rig.GamePaused = true; rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.PreparePeople();
            var other = rig.Chatter.Person("unit:2");
            var original = new CrewPerson("Old Person", "Lt.", "Quiet", "22222222222222222222222222222222.png");
            rig.Chatter.UpdatePerson("unit:1", rig.Chatter.Person("unit:1")!, original);
            rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var call = rig.Take(); var data = call.Batch.Snapshot;
            Require(ReferenceEquals(original, rig.Chatter.Person("unit:1")) && !rig.Chatter.Enabled("unit:1") &&
                ((JArray)data["appointments"]!).Count == 1 && (string?)data["appointments"]![0]!["id"] == "unit:1" &&
                ((JArray)data["speakers"]!).Count == 0 && data["appointment_context"] != null,
                "Replacement started with the wrong person, unit or request context");
            call.Completion.SetResult(CrewTestGeneration.Read(call.Batch, Reply(new JArray(), new JArray(AppointmentRow("unit:1", "Replacement"))))); rig.Pump(1);
            Require(rig.Chatter.Person("unit:1")!.Name == "Replacement" && rig.Chatter.Person("unit:1")!.PortraitId == null &&
                !rig.Chatter.Enabled("unit:1") && ReferenceEquals(other, rig.Chatter.Person("unit:2")) && !rig.Chatter.AppointmentRequested("unit:1"),
                "Reappointment changed participation, another person, or inherited the previous face");
        });
        check("Participation changes during reappointment are preserved when the replacement arrives", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.PreparePeople();
            rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var call = rig.Take();
            rig.Chatter.ApplyTargets(new[] { "unit:1" }, 1);
            rig.Complete(call); rig.Pump(1);
            Require(rig.Chatter.Enabled("unit:1") && !rig.Chatter.Enabled("unit:2") && !rig.Chatter.AppointmentRequested("unit:1"),
                "Replacement overwrote the latest participation choice");
        });
        check("A failed reappointment retains the old person and selection while pausing Chatter", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            var person = rig.Chatter.Person("unit:1"); rig.SetEnabled("unit:1", true, 0);
            rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var call = rig.Take();
            call.Completion.SetException(new CrewException("invalid_response")); rig.Pump(1);
            Require(rig.Chatter.Paused && rig.Chatter.Error == "invalid_response" && ReferenceEquals(person, rig.Chatter.Person("unit:1")) &&
                rig.Chatter.Enabled("unit:1"), "Failed replacement removed the old person or selection");
            var edited = new CrewPerson("Edited after failure", "", "Calm");
            rig.Chatter.UpdatePerson("unit:1", person!, edited);
            Require(ReferenceEquals(edited, rig.Chatter.Person("unit:1")) && !rig.Chatter.AppointmentRequested("unit:1") &&
                rig.Chatter.Paused && rig.Chatter.Enabled("unit:1"), "A paused replacement prevented editing or unexpectedly resumed communication");
        });
        check("A newer direct edit prevents an outstanding replacement from overwriting it", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            var old = rig.Chatter.Person("unit:1")!;
            rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var call = rig.Take();
            var edited = new CrewPerson("Keep Edit", "", "Quiet"); rig.Chatter.UpdatePerson("unit:1", old, edited);
            rig.Complete(call); rig.Pump(1);
            Require(ReferenceEquals(edited, rig.Chatter.Person("unit:1")) && !rig.Chatter.Paused, "An obsolete replacement overwrote a newer edit");
            Throws<CrewException>(() => rig.Chatter.UpdatePerson("unit:1", old, new CrewPerson("Stale", "", "")));
        });
        check("Removed units do not receive completed replacement appointments", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            var old = rig.Chatter.Person("unit:1"); rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var call = rig.Take();
            rig.Chatter.Refresh(Array.Empty<CrewTarget>()); rig.Complete(call); rig.Pump(1);
            Require(ReferenceEquals(old, rig.Chatter.Person("unit:1")) && !rig.Chatter.Paused, "Replacement was applied to a removed unit");
        });
        check("Person drafts allow free multiline personality and reject overflow without truncating or changing the original", () =>
        {
            var original = new CrewPerson("Original", "Cmdr.", "Calm", "22222222222222222222222222222222.png"); var draft = new CrewPersonDraft(original);
            draft.Name = "New"; draft.Rank = ""; draft.Personality = "Talk softly.\n" + new string('a', 242);
            var built = draft.Build(); Require(built.Personality.Length == 255 && built.Rank == "" && original.Name == "Original", "Editing changed the source or trimmed a valid personality");
            draft.Personality += "x"; Throws<CrewException>(() => draft.Build());
            Require(draft.Personality.Length == 256 && original.Personality == "Calm", "Invalid input was truncated or applied");
            draft.SelectPortrait("11111111111111111111111111111111.png");
            Require(draft.PortraitId != original.PortraitId && original.PortraitId == "22222222222222222222222222222222.png", "Selecting an image changed the original person");
            draft.SelectPortrait(null); Require(draft.PortraitId == null && original.PortraitId != null, "Resetting the image changed the original person");
        });
        check("Portrait fitting preserves aspect ratio, bounds both orientations and never enlarges small images", () =>
        {
            var size = SeaPowerAIAdvisor.Shared.PortraitData.FitSize(1200, 800, CrewPortraitLibrary.MaximumDimension);
            Require(size == (256, 171), "A landscape portrait was cropped or fitted incorrectly");
            size = SeaPowerAIAdvisor.Shared.PortraitData.FitSize(800, 1200, CrewPortraitLibrary.MaximumDimension);
            Require(size == (171, 256), "A tall portrait was cropped or fitted incorrectly");
            Require(SeaPowerAIAdvisor.Shared.PortraitData.FitSize(4096, 4096, CrewPortraitLibrary.MaximumDimension) == (256, 256), "A square portrait exceeded the Crew limit");
            Require(SeaPowerAIAdvisor.Shared.PortraitData.FitSize(96, 192, CrewPortraitLibrary.MaximumDimension) == (96, 192), "A small portrait was enlarged");
            Require(SeaPowerAIAdvisor.Shared.PortraitData.FitSize(256, 256, CrewPortraitLibrary.MaximumDimension) == (256, 256), "A boundary image was unnecessarily resized");
            Require(SeaPowerAIAdvisor.Shared.PortraitData.FitSize(1, 4096, CrewPortraitLibrary.MaximumDimension) == (1, 256), "An extreme aspect ratio produced a zero-sized image");
        });
        check("Portrait inspection reads PNG and JPEG dimensions in the correct orientation before fitting", () =>
        {
            var png = SeaPowerAIAdvisor.Shared.PortraitData.Inspect(PortraitHeader(800, 1200));
            var jpeg = SeaPowerAIAdvisor.Shared.PortraitData.Inspect(new byte[]
                { 0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 3, 0x20, 4, 0xb0, 1, 1, 0x11, 0 });
            Require(png == (".png", 800, 1200) && jpeg == (".jpg", 1200, 800), "Dimensions were swapped or lost for a supported input format");
        });
        check("Portrait changes keep historical image IDs and never remove library assets", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var first = library.Add(PortraitHeader(256, 128)); var second = library.Add(PortraitHeader(128, 256));
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
            var original = new CrewPerson("Original", "Capt.", "Calm", first);
            rig.Chatter.UpdatePerson("unit:1", rig.Chatter.Person("unit:1")!, original);
            rig.SetEnabled("unit:1", true, 0); rig.Tick(1); rig.Complete(rig.Take(), "unit:1"); rig.Pump(1);
            var remark = rig.Chatter.History.Single(); var message = remark.Submission.Text;
            var draft = new CrewPersonDraft(original); draft.SelectPortrait(second);
            Require(rig.Chatter.Person("unit:1")!.PortraitId == first, "An unsaved selection changed the person");
            rig.Chatter.UpdatePerson("unit:1", original, draft.Build());
            Require(rig.Chatter.Person("unit:1")!.PortraitId == second && remark.Submission.Person.PortraitId == first &&
                remark.Submission.Text == message && library.Contains(first) && library.Contains(second), "An edit relabeled history or removed an image");
            rig.Chatter.UpdatePerson("unit:1", rig.Chatter.Person("unit:1")!, new CrewPerson("Common", "", ""));
            Require(library.Contains(first) && library.Contains(second), "Using the common icon deleted library assets");
            library.Delete(first);
            Require(remark.Submission.Person.PortraitId == first && library.Read(first) == null && library.Contains(second), "Deleting an image rewrote history or another image");
        }));
        check("Reappointment and session disposal release people without deleting their library images", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var id = library.Add(PortraitHeader(256, 256));
            using (var rig = new Rig())
            {
                rig.Chatter.Refresh(new[] { Target(1) }); rig.PreparePeople();
                rig.Chatter.UpdatePerson("unit:1", rig.Chatter.Person("unit:1")!, new CrewPerson("Original", "", "", id));
                rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); rig.Complete(rig.Take()); rig.Pump(1);
                Require(rig.Chatter.Person("unit:1")!.PortraitId == null && library.Contains(id), "Reappointment removed a persistent image");
            }
            Require(new CrewPortraitLibrary(directory).Read(id) != null, "Session disposal removed an image needed after restart");
        }));

    }
    internal static byte[] PortraitHeader(int width, int height)
    {
        var bytes = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        void Number(int offset, int value)
        { for (var i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (24 - i * 8)); }
        Number(16, width); Number(20, height); return bytes;
    }
    private static JObject Row(string id) => new() { ["id"] = id, ["text"] = "Maintaining station." };
    private static JObject ChatterSituation() => JObject.Parse("""
        {"captured_at_utc":"1985-06-26T20:57:00Z","weather":{"wind_force_beaufort":5,"precipitation":["rain"],"fog":false,"lightning":false},"units":[
          {"id":"unit:1","display_name":"Helicopter","nation":"US","kind":"helicopter","acting_independently":false,
           "position":{"latitude_deg":26.5,"longitude_deg":127.3},"heading_deg":90,"speed_knots":70,"altitude_ft":200,
           "damage":{"disabled":false},"fuel":{"remaining_mass_kg":600},"weapons":{"ammunition":[{"remaining_count":12}]},"sensors":[{"on":true}],"active_threat_alerts":["missile"]},
          {"id":"unit:2","display_name":"Submarine","nation":"US","kind":"submarine","acting_independently":true,
           "position":{"latitude_deg":26.6,"longitude_deg":127.4},"heading_deg":180,"speed_knots":10,"depth_ft":400,
           "damage":{"disabled":false,"fire_severity":"severe"},"fuel":{"status":"not_displayed_for_unit_type"},"weapons":{"ammunition":[{"remaining_count":4}]},"sensors":[{"on":false}],"active_threat_alerts":["torpedo","radar_illumination","jamming"]}
        ],"contacts":[{"track_number":7017,"posture":"enemy","position":{"latitude_deg":26.8,"longitude_deg":126.5}}],
        "launched_weapons":[{"posture":"enemy","kind":"missile","count":2,"launch_platform":null,"tracked_target":{"id":"unit:1","display_name":"Helicopter"}}]}
        """);
    private static JObject AppointmentRow(string id, string name = "Fictional", string position = "before") => new() { ["id"] = id, ["character_id"] = "new", ["person_name"] = name, ["rank"] = "Cmdr.", ["name_with_rank"] = position switch { "before" => "Cmdr. " + name, "after" => name + " Cmdr.", _ => name }, ["personality"] = "Calm" };
    private static AiReply Reply(JArray rows, JArray? appointments = null) => new("", new[] { new AiToolCall("chatter", CrewBatch.ToolName, new JObject { ["appointments"] = appointments ?? new JArray(), ["remarks"] = rows }) }, 12);
    private static CrewTarget Target(int id, params int[] members) => new("unit:" + id, "Unit " + id, "US", "captain", "vessel", "Destroyer", members.Length > 1, members.Length == 0 ? new[] { id } : members, true);
    private static CrewTarget Follower(int id) => new("unit:" + id, "Unit " + id, "US", "captain", "vessel", "Destroyer", false, new[] { id }, false);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected rejection: " + typeof(T).Name); }
    private static void Temp(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { test(directory); } finally { Directory.Delete(directory, true); }
    }
    private sealed class Call
    {
        internal readonly CrewBatch Batch;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<CrewGeneration> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Call(CrewBatch batch, CancellationToken token) { Batch = batch; Token = token; }
    }
    private sealed class FixedRandom(double value) : Random
    { public override double NextDouble() => value; }
    private sealed class Rig : IDisposable
    {
        internal readonly List<Exception> Errors = new();
        internal readonly CrewChatter Chatter;
        internal int Captures, AppointmentCaptures, Requests;
        private readonly int mainThread = Environment.CurrentManagedThreadId;
        private readonly ConcurrentQueue<Call> calls = new();
        internal CrewSettings Settings;
        internal bool GamePaused;
        internal string Language = "en";
        internal JObject Situation = new();
        internal JObject AppointmentContext = new() { ["captured_at_utc"] = "1985-06-26T20:57:00.0000000", ["mission_briefing"] = "Protect the convoy." };
        internal Rig(Random? random = null, CrewSettings? settings = null)
        { Chatter = new CrewChatter(Errors.Add, random ?? new Random(1)); Settings = settings ?? new CrewSettings("chatter", 1, 10); }
        internal void Tick(double now) => Chatter.Tick(now, GamePaused, Settings, Language, _ =>
        { Require(Environment.CurrentManagedThreadId == mainThread, "Capture ran on a worker thread"); Captures++; return Situation; },
            _ => { Require(Environment.CurrentManagedThreadId == mainThread, "Appointment capture ran on a worker thread"); AppointmentCaptures++; return AppointmentContext; },
            async (batch, _, token, usage) =>
            {
                Interlocked.Increment(ref Requests); var call = new Call(batch, token); calls.Enqueue(call); long? tokens = null;
                try { var result = await call.Completion.Task; tokens = 12; return result; }
                finally { usage(tokens); }
            });
        internal Call Take()
        {
            Call? call = null; Require(SpinWait.SpinUntil(() => calls.TryDequeue(out call), 3000), "Chatter generation did not start"); return call!;
        }
        internal int SetupRequests;
        internal void PreparePeople()
        {
            Chatter.RequestAppointments(-2); Tick(-1); var appointment = Take(); Complete(appointment); Pump(-1);
            Chatter.ApplyTargets(Array.Empty<string>(), 0); SetupRequests++;
        }
        internal void SetEnabled(string id, bool enabled, double now)
        {
            var selected = new HashSet<string>(Chatter.Targets.Where(t => Chatter.Enabled(t.Id)).Select(t => t.Id));
            if (enabled) selected.Add(id); else selected.Remove(id); Chatter.ApplyTargets(selected, now);
        }
        internal void Complete(Call call, params string[] ids) => CompleteWithVote(call, "before", ids);
        internal void CompleteWithVote(Call call, string position, params string[] ids)
        {
            var appointments = new JArray(((JArray)call.Batch.Snapshot["appointments"]!).Select(t => AppointmentRow((string)t["id"]!, position: position)));
            call.Completion.SetResult(CrewTestGeneration.Read(call.Batch, Reply(new JArray(ids.Select(Row)), appointments)));
        }
        internal void Pump(double now) => TickUntil(now, () => !Chatter.Busy);
        internal void TickUntil(double now, Func<bool> done) => Require(SpinWait.SpinUntil(() => { Tick(now); return done(); }, 3000), "Chatter did not complete");
        public void Dispose() => Chatter.Dispose();
    }
}
