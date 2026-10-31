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
            Require(new CrewSettings().ExchangeChancePercent == 33 && CrewSettings.Parse("{}").ExchangeChancePercent == 33,
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
            Require(read.Information.Allows("sensors") && adviser.Current.Information.Allows(InformationKind.BattleFuel), "The features shared information selections");
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
                rig.Chatter.Refresh(new[] { Target(1, 1, 2) }); rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0); rig.Tick(1);
                var call = rig.Take(); var data = call.Batch.Input("").Data;
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
            rig.Chatter.Refresh(new[] { Target(1) }); rig.Chatter.ApplyTargets(new[] { "unit:1" }, 0); rig.Tick(1);
            var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            rig.Settings = new CrewSettings("chatter", 1, information: CrewInformation.Default.With("fuel", false));
            rig.Tick(100); var reduced = rig.Take();
            Require(reduced.Batch.Input("").Data["recent_remarks"] is JArray { Count: 0 } && rig.Chatter.History.Count == 1,
                "Old facts were resent or the player's displayed history was cleared");
            rig.Complete(reduced, "unit:1"); rig.Pump(100);
            rig.Settings = new CrewSettings("chatter", 1);
            rig.Tick(200); var restored = rig.Take();
            Require(restored.Batch.Input("").Data["recent_remarks"] is JArray { Count: 0 }, "Re-enabling information revived an earlier context");
            rig.Complete(restored, "unit:1"); rig.Pump(200);
            rig.Tick(300); var same = rig.Take();
            Require(same.Batch.Input("").Data["recent_remarks"] is JArray { Count: 1 }, "Unchanged information lost useful recent remarks");
            rig.Settings = new CrewSettings("chatter", 1, information: CrewInformation.Default.With("contacts", false));
            rig.Complete(same, "unit:1"); rig.Pump(300);
            rig.Tick(400); var next = rig.Take();
            Require(next.Batch.Input("").Data["recent_remarks"] is JArray { Count: 0 } && rig.Chatter.History.Count == 4 &&
                rig.Chatter.Person("unit:1") != null && rig.Chatter.Enabled("unit:1"), "A late response leaked old facts or lost history, crew or selection");
            rig.Complete(next); rig.Pump(400);
        });
        check("Chatter API changes targets only after scope, complete target set and connection permission validation", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2), Target(3) }); var denied = false; var checkedConnection = 0;
            var api = new CrewLocalApi(rig.Chatter, () => 0, () => { checkedConnection++; if (denied) throw new LocalApiException(403, "connection_not_allowed"); });
            JObject Request(string scope, params string[] ids) => new() { ["scope_id"] = scope, ["enabled"] = true, ["target_ids"] = new JArray(ids) };
            Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Request("previous battle", "unit:1")));
            Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Request(api.ScopeId, "unit:1", "unit:2")));
            Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Request(api.ScopeId, "unit:1", "unit:1")));
            Require(!rig.Chatter.Enabled("unit:1") && checkedConnection == 0, "An invalid batch partially applied or used a connection");
            denied = true; Throws<LocalApiException>(() => api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Request(api.ScopeId, "unit:1")));
            Require(!rig.Chatter.Enabled("unit:1") && rig.Captures == 0, "Denied connection enabled automatic speaking");
            denied = false; var result = api.Dispatch("PATCH", CrewLocalApi.Path + "/targets", Request(api.ScopeId, "unit:1", "unit:3"));
            Require(result.Status == 200 && rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:3") && rig.Captures == 0, "Target changes bypassed normal tick scheduling");
            var view = api.Status(); ((JObject)view["targets"]![0]!)["display_name"] = "mutated";
            Require(rig.Chatter.Targets.All(t => t.Name != "mutated"), "A status response exposed mutable session state");
            api.Dispatch("POST", CrewLocalApi.Path + "/pause", new JObject { ["scope_id"] = api.ScopeId, ["paused"] = true });
            Require(rig.Chatter.Paused && rig.Chatter.Enabled("unit:1"), "API pause disabled selection");
            denied = true;
            Throws<LocalApiException>(() => api.Dispatch("POST", CrewLocalApi.Path + "/pause", new JObject { ["scope_id"] = api.ScopeId, ["paused"] = false }));
            Require(rig.Chatter.Paused && rig.Requests == 0, "A forbidden connection resumed speaking");
            denied = false; api.Dispatch("POST", CrewLocalApi.Path + "/pause", new JObject { ["scope_id"] = api.ScopeId, ["paused"] = false });
            api.Dispatch("POST", CrewLocalApi.Path + "/stop", new JObject { ["scope_id"] = api.ScopeId });
            Require(!rig.Chatter.Enabled("unit:1") && !rig.Chatter.Enabled("unit:3"), "Chatter stop retained active targets");
            using var newSession = new CrewChatter(_ => { }); var nextApi = new CrewLocalApi(newSession, () => 0, () => { });
            Require(nextApi.ScopeId != api.ScopeId, "Separate battle lifetimes reused the API scope");
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
                rig.Chatter.Refresh(targets); rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1);
                var call = rig.Take();
                Require((bool?)call.Batch.Input("").Data["prefer_exchange"] == expected && rig.Requests == 1,
                    "The wrong opportunity style was sent, or multiple requests were made");
                rig.Complete(call); rig.Pump(1); rig.Tick(2);
                Require(rig.Requests == 1 && rig.Chatter.History.Count == 0 && !rig.Chatter.Paused,
                    "A preferred exchange forced speech, retried silence or paused Chatter");
            }
        });
        check("Exchange preference accepts silence and a single speaker and preserves the submitted reply order", () =>
        {
            var person = new CrewPerson("Crew", "calm");
            var batch = new CrewBatch(new JObject(), new Dictionary<string, CrewPerson?> { ["unit:1"] = person, ["unit:2"] = person },
                Array.Empty<string>(), preferExchange: true);
            Require(batch.Read(Reply(new JArray())).Remarks.Count == 0 && batch.Read(Reply(new JArray(Row("unit:1")))).Remarks.Count == 1,
                "Exchange preference became a two-speaker requirement");
            var result = batch.Read(Reply(new JArray(Row("unit:2"), Row("unit:1"))));
            Require(result.Remarks.Select(r => r.Id).SequenceEqual(new[] { "unit:2", "unit:1" }), "The reply was moved ahead of the first remark");
        });
        check("A generation appoints silent crew independently, preserves people and rejects incomplete or unknown results", () =>
        {
            var person = new CrewPerson("Existing", "Calm"); var input = new JObject { ["units"] = new JArray("snapshot") };
            var batch = new CrewBatch(input, new Dictionary<string, CrewPerson?> { ["unit:1"] = person, ["unit:2"] = null, ["unit:3"] = null }, new[] { "unit:2", "unit:3" });
            input["units"] = new JArray("changed"); var providerInput = batch.Input("connection instruction");
            Require((string?)providerInput.Data["units"]![0] == "snapshot" && providerInput.Tools.Count == 1, "Live input or game tools leaked into a batch");
            var names = new JArray(AppointmentRow("unit:3"), AppointmentRow("unit:2", "New"));
            var result = batch.Read(Reply(new JArray(Row("unit:2"), Row("unit:1")), names));
            Require(result.Remarks.Count == 2 && result.Remarks.Single(x => x.Id == "unit:1").Person == person && result.Appointments["unit:2"].Name == "New", "Identity or appointment was corrupted");
            var silent = batch.Read(Reply(new JArray(), names));
            Require(silent.Remarks.Count == 0 && silent.Appointments.Count == 2, "Silence prevented appointment");
            foreach (var invalid in new[] { new JArray(AppointmentRow("unit:2")), new JArray(AppointmentRow("unit:2"), AppointmentRow("unit:2")), new JArray(AppointmentRow("unit:2"), AppointmentRow("unit:99")) })
                Throws<CrewException>(() => batch.Read(Reply(new JArray(), invalid)));
            foreach (var invalid in new[] { new JArray(Row("unit:1"), Row("unit:2"), Row("unit:3")), new JArray(Row("unit:1"), Row("unit:1")), new JArray(Row("unit:1"), Row("unit:99")) })
                Throws<CrewException>(() => batch.Read(Reply(invalid, names)));
            Throws<CrewException>(() => batch.Read(new AiReply("plain text", Array.Empty<AiToolCall>(), null)));
        });
        check("Enabled candidates share a bounded random real-time interval and receive recent remarks as context", () =>
        {
            using var rig = new Rig(); var targets = new[] { Target(1), Target(2), Target(3) }; rig.Chatter.Refresh(targets);
            rig.Tick(100); Require(rig.Captures == 0 && !rig.Chatter.Busy, "Eligible units automatically started sending");
            foreach (var t in targets.Take(2)) rig.SetEnabled(t.Id, true, 100);
            rig.Tick(100.5); Require(rig.Captures == 0, "The initial coalescing interval was skipped");
            rig.Tick(101); var call = rig.Take();
            Require(call.Batch.Input("").Data["speakers"] is JArray { Count: 2 } && rig.Captures == 1, "One enabled group produced separate captures or requests");
            rig.Complete(call, "unit:1", "unit:2"); rig.Pump(101);
            Require(rig.Chatter.History.Count == 2 && rig.Chatter.Person("unit:1") != null, "One batched result did not appoint both speakers");
            rig.SetEnabled("unit:3", true, 102); rig.Tick(103); var appointment = rig.Take();
            Require(rig.Captures == 1 && appointment.Batch.Input("").Data["speakers"] is JArray { Count: 0 }, "Adding a candidate accelerated the whole-Crew interval");
            rig.Complete(appointment); rig.Pump(103); Require(rig.Chatter.Person("unit:3") != null, "A quiet new member was not appointed");
            rig.Tick(145.9); Require(rig.Captures == 1, "Remarks ignored the earliest whole-Crew opportunity");
            rig.Tick(176); var next = rig.Take(); Require(rig.Captures == 2, "Elapsed real time did not trigger the next remark");
            Require(((JArray)next.Batch.Input("").Data["speakers"]!).Where(r => (string?)r["id"] != "unit:3").All(r => r["person"] is JObject), "A subsequent request lost the appointment");
            Require(next.Batch.Input("").Data["recent_remarks"] is JArray { Count: 2 }, "Recent speech was lost between opportunities");
            rig.Complete(next, "unit:1", "unit:2"); rig.Pump(176);
            rig.Tick(252); var third = rig.Take();
            Require(third.Batch.Input("").Data["recent_remarks"] is JArray { Count: 3 }, "Conversation context grew beyond its bounded recent history");
            rig.Complete(third); rig.Pump(252);
        });
        check("Repeated game pauses preserve the remaining speech countdown without catch-up requests", () =>
        {
            using var rig = new Rig(new FixedRandom(0.5));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            rig.Tick(21); rig.GamePaused = true; rig.Tick(21); rig.Tick(500);
            Require(rig.Requests == 1 && !rig.Chatter.Paused, "Game pause started a generation or changed the manual pause state");
            rig.GamePaused = false; rig.Tick(500); rig.Tick(520);
            Require(rig.Requests == 1, "Resuming caught up requests or lost the remaining countdown");
            rig.GamePaused = true; rig.Tick(520); rig.Tick(900);
            rig.GamePaused = false; rig.Tick(900); rig.Tick(919);
            Require(rig.Requests == 1, "A second pause consumed or reset the remaining time");
            rig.Tick(920); var next = rig.Take();
            Require(rig.Requests == 2 && rig.Captures == 2, "The remaining active real-time interval did not produce one generation");
            rig.Complete(next); rig.Pump(920);
        });
        check("In-flight speech completes during game pause and its next countdown starts after game resume", () =>
        {
            using var rig = new Rig(new FixedRandom(0.5));
            rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.GamePaused = true; rig.Tick(2);
            Require(!first.Token.IsCancellationRequested, "Game pause cancelled an already sent request");
            rig.Complete(first, "unit:1"); rig.Pump(100);
            Require(rig.Chatter.History.Count == 1 && rig.Chatter.Person("unit:1") != null && rig.Requests == 1,
                "Game pause discarded a valid response or started another generation");
            rig.GamePaused = false; rig.Tick(100); rig.Tick(159);
            Require(rig.Requests == 1, "Waiting for the response while paused consumed the next countdown");
            rig.Tick(160); var next = rig.Take(); rig.Complete(next); rig.Pump(160);
        });
        check("Game pause keeps response freshness and queued notification expiry on real time", () =>
        {
            using (var rig = new Rig(new FixedRandom(0.5)))
            {
                rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
                rig.Tick(1); var first = rig.Take(); rig.GamePaused = true; rig.Tick(2);
                rig.Complete(first, "unit:1"); rig.Pump(182);
                Require(rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") == null && rig.Requests == 1,
                    "Game pause extended the lifetime of a stale response");
            }
            using (var rig = new Rig(new FixedRandom(0.5)))
            {
                rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
                rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
                rig.GamePaused = true; rig.Tick(2); rig.Tick(200);
                Require(rig.Chatter.History.Count == 1 && rig.Chatter.NextNotification(200) == null && rig.Requests == 1,
                    "Game pause extended a pending popup's lifetime or generated new speech");
            }
        });
        check("Explicit appointments still run during game pause while initial speech waits for resume", () =>
        {
            using var rig = new Rig(new FixedRandom(0.5)); rig.GamePaused = true; rig.Tick(0);
            rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var appointment = rig.Take(); var input = appointment.Batch.Input("").Data;
            Require(input["speakers"] is JArray { Count: 0 } && input["appointments"] is JArray { Count: 1 } && rig.Captures == 0,
                "A paused appointment generated speech or captured a situation");
            rig.Complete(appointment); rig.Pump(1); rig.Tick(1000);
            Require(rig.Requests == 1 && rig.Chatter.Person("unit:1") != null && rig.Chatter.History.Count == 0,
                "Game pause prevented appointment or consumed the first speech countdown");
            rig.GamePaused = false; rig.Tick(1000);
            Require(rig.Requests == 1, "Speech started without waiting the remaining initial interval");
            rig.Tick(1001); var speech = rig.Take();
            Require(speech.Batch.Input("").Data["appointments"] is JArray { Count: 0 } && rig.Captures == 1,
                "Game resume reappointed the crew or omitted speech");
            rig.Complete(speech, "unit:1"); rig.Pump(1001);
        });
        check("Silence is successful, does not trigger catch-up requests, and candidates rotate without enabling other targets", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, 6).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets);
            foreach (var t in targets.Take(5)) rig.SetEnabled(t.Id, true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Tick(20);
            Require(rig.Captures == 1, "A second request overlapped the first");
            var ids = ((JArray)first.Batch.Input("").Data["speakers"]!).Select(x => (string)x["id"]!).ToArray();
            Require(ids.Length == 4, "The batch bound was ignored");
            rig.Complete(first); rig.Pump(20); rig.Tick(64.9);
            Require(rig.Captures == 1 && rig.Chatter.History.Count == 0 && rig.Chatter.Error == null && targets.Take(5).All(t => rig.Chatter.Person(t.Id) != null),
                "Silence caused an error, lost appointments, or triggered an immediate speech request");
            rig.Tick(96); var second = rig.Take();
            var last = ((JArray)second.Batch.Input("").Data["speakers"]!).Select(x => (string)x["id"]!).ToArray();
            var omitted = targets.Take(5).Single(t => !ids.Contains(t.Id)).Id;
            Require(last.Length == 4 && last.Contains(omitted) && !last.Contains("unit:6") && !rig.Chatter.Enabled("unit:6"),
                "Candidate selection starved a previously omitted unit or included a disabled one");
            rig.Complete(second, last[0]); rig.Pump(96);
            Require(rig.Chatter.History.Count == 1 && last.All(id => rig.Chatter.Person(id) != null), "A silent candidate lost its appointment or was forced to speak");
        });
        check("Disabling and re-enabling a target rejects its old response and pending notification", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.SetEnabled("unit:1", false, 1); rig.SetEnabled("unit:1", true, 1);
            rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") == null, "A stale response appointed a disabled target");
            rig.Tick(2); var fresh = rig.Take(); rig.Complete(fresh, "unit:1"); rig.Pump(2);
            Require(rig.Chatter.History.Count == 1, "A fresh enabled response was lost");
            rig.SetEnabled("unit:1", false, 2); rig.SetEnabled("unit:1", true, 2);
            Require(rig.Chatter.NextNotification(2) == null, "A previous activation's notification survived toggling");
        });
        check("Global suspension cancels in-flight work, turns targets off, preserves session people and rejects late results", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            var person = rig.Chatter.Person("unit:1"); rig.Tick(77); var late = rig.Take(); rig.Chatter.Suspend();
            Require(late.Token.IsCancellationRequested && !rig.Chatter.Enabled("unit:1") && rig.Chatter.NextNotification(77) == null, "Suspension retained work or pending notifications");
            rig.Complete(late, "unit:1"); rig.SetEnabled("unit:1", true, 77); rig.Tick(78); var fresh = rig.Take();
            rig.Complete(fresh, "unit:1"); rig.Pump(78);
            Require(rig.Chatter.History.Count == 2 && rig.Chatter.Person("unit:1") == person, "A late result survived suspension or replaced the existing appointment");
        });
        check("Formation changes invalidate a captured remark while a returning reporter retains its appointed person", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.Chatter.Refresh(new[] { Target(1, 1, 3) }); rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Chatter.History.Count == 0, "A remark about the former formation was applied");
            rig.Tick(77); var current = rig.Take(); rig.Complete(current, "unit:1"); rig.Pump(77); var person = rig.Chatter.Person("unit:1");
            rig.Chatter.Refresh(Array.Empty<CrewTarget>()); rig.Chatter.Refresh(new[] { Target(1) });
            Require(rig.Chatter.Person("unit:1") == person && person != null && !rig.Chatter.Enabled("unit:1"), "Returning to eligibility lost the person or silently resumed speaking");
        });
        check("All selected units are appointed but only current leaders and standalone units can remark", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2, 3), Follower(2), Follower(3) });
            rig.Chatter.ApplyTargets(new[] { "unit:1", "unit:2", "unit:3" }, 0);
            rig.Tick(1); var initial = rig.Take(); var data = initial.Batch.Input("").Data;
            Require(data["appointments"] is JArray { Count: 3 } && data["speakers"] is JArray { Count: 1 } &&
                (string?)data["speakers"]![0]!["id"] == "unit:1", "Followers were omitted from appointment or selected to speak");
            rig.Complete(initial); rig.Pump(1); var person = rig.Chatter.Person("unit:2");
            Require(person != null && rig.Chatter.Targets.All(t => rig.Chatter.Person(t.Id) != null), "Silent followers did not receive crew");
            var draft = new CrewTargetSelection(); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 1, 2, 3), Follower(3) });
            draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(draft.Selected.Count == 3 && !draft.Dirty && rig.Chatter.Enabled("unit:2") && rig.Chatter.Person("unit:2") == person,
                "Changing leader reset the confirmed selection, pending edits or crew identity");
            rig.Tick(77); var next = rig.Take(); data = next.Batch.Input("").Data;
            Require(data["appointments"] is JArray { Count: 0 } && data["speakers"] is JArray { Count: 1 } &&
                (string?)data["speakers"]![0]!["id"] == "unit:2", "A leader change required reappointment or retained the previous speaker");
            rig.Complete(next, "unit:2"); rig.Pump(77);
            Require(rig.Chatter.History.Single().Submission.Person == person, "The new leader spoke as a different person");
            var api = new CrewLocalApi(rig.Chatter, () => 77, () => { }); var status = api.Status();
            Require(status["targets"] is JArray { Count: 3 } && (bool)status["targets"]![1]!["can_speak"]! &&
                !(bool)status["targets"]![0]!["can_speak"]!, "The API hid followers or reported stale eligibility");
        });
        check("An enabled follower is appointed without speech requests and resumes automatically after becoming eligible", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2), Follower(2) }); rig.SetEnabled("unit:2", true, 0);
            rig.Tick(1); var appointment = rig.Take(); var data = appointment.Batch.Input("").Data;
            Require(rig.Captures == 0 && data["speakers"] is JArray { Count: 0 } && data["appointments"] is JArray { Count: 1 },
                "A follower-only appointment captured battle data or requested speech");
            rig.Complete(appointment); rig.Pump(1); var person = rig.Chatter.Person("unit:2");
            rig.Tick(1000); Require(rig.Requests == 1 && rig.Chatter.Enabled("unit:2"), "A follower kept sending or lost its ON setting");
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 1, 2) }); rig.Tick(1000); rig.Tick(1001); var remark = rig.Take();
            Require(remark.Batch.Input("").Data["appointments"] is JArray { Count: 0 }, "Promotion regenerated an existing appointment");
            rig.Complete(remark, "unit:2"); rig.Pump(1001);
            Require(rig.Chatter.History.Single().Submission.Person == person, "Promotion did not resume with the same person");
            rig.Chatter.Refresh(new[] { Target(1, 1, 2), Follower(2) });
            Require(rig.Chatter.NextNotification(1001) == null && rig.Chatter.Enabled("unit:2"), "A demoted unit retained a queued remark or lost its ON setting");
            rig.SetEnabled("unit:2", false, 1001); rig.Chatter.Refresh(new[] { Follower(1), Target(2) }); rig.Tick(2000);
            Require(!rig.Chatter.Enabled("unit:2") && rig.Requests == 2 && rig.Chatter.Person("unit:2") == person,
                "A disabled unit resumed on promotion or lost its person");
        });
        check("Formation changes during generation preserve appointments but discard speeches about the former formation", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1, 1, 2), Follower(2) });
            rig.Chatter.ApplyTargets(new[] { "unit:1", "unit:2" }, 0); rig.Tick(1); var old = rig.Take();
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 1, 2) }); rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") != null && rig.Chatter.Person("unit:2") != null &&
                rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:2"), "A leader change lost appointments/settings or applied stale speech");
            rig.Tick(77); var current = rig.Take(); rig.Complete(current, "unit:2"); rig.Pump(77);
            rig.Chatter.Refresh(new[] { Follower(1), Target(2, 2) });
            Require(rig.Chatter.NextNotification(77) == null, "A queued remark about a former membership was displayed");
        });
        check("Target edits, bulk selection and discard remain UI-only until a complete selection is applied", () =>
        {
            using var rig = new Rig(); var targets = new[] { Target(1), Target(2), Target(3) }; rig.Chatter.Refresh(targets);
            var draft = new CrewTargetSelection(); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            draft.SetAll(true); rig.Tick(100);
            Require(draft.Dirty && draft.Selected.Count == 3 && rig.Requests == 0 && !rig.Chatter.HasEnabledTargets, "Editing checkboxes changed the running selection");
            draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(draft.Dirty && draft.Selected.Count == 3, "A background refresh erased pending edits");
            draft.Discard(); Require(!draft.Dirty && draft.Selected.Count == 0, "Discard retained staged targets");
            draft.SetAll(true); rig.Chatter.ApplyTargets(draft.Selected, 100); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(!draft.Dirty && rig.Chatter.HasEnabledTargets && rig.Requests == 0, "Apply did not commit atomically or sent outside the scheduler");
            rig.Chatter.Refresh(targets.Concat(new[] { Target(4) }).ToArray()); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(!draft.Checked("unit:4") && !rig.Chatter.Enabled("unit:4"), "A previous bulk selection automatically enabled a new arrival");
            draft.Set("unit:2", false); rig.Chatter.ApplyTargets(new[] { "unit:2", "unit:3" }, 100); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(draft.Selected.SequenceEqual(new[] { "unit:3" }), "A draft restored a background-disabled target");
            draft.Discard(); draft.Set("unit:3", false); rig.Chatter.Refresh(targets.Take(2).ToArray()); draft.Sync(rig.Chatter.Targets, rig.Chatter.Enabled);
            Require(!draft.Dirty, "A vanished target left unresolvable pending edits");
            Throws<ArgumentException>(() => rig.Chatter.ApplyTargets(new[] { "unit:1", "unit:99" }, 100));
            Require(!rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:2"), "An invalid complete selection partly applied");
        });
        check("Pausing retains selection and blocks both appointments and remarks until resume", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, 5).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets);
            rig.Chatter.SetPaused(true, 0); rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1000);
            Require(rig.Requests == 0 && rig.Chatter.Paused && targets.All(t => rig.Chatter.Enabled(t.Id)), "Applying targets while paused sent a request or lost selection");
            rig.Chatter.SetPaused(false, 1000); rig.Tick(1001); var first = rig.Take(); rig.Complete(first); rig.Pump(1001);
            Require(targets.All(t => rig.Chatter.Person(t.Id) != null) && rig.Chatter.History.Count == 0, "Silent participants were not appointed together");
            var person = rig.Chatter.Person("unit:1"); rig.Chatter.SetPaused(true, 1002); rig.Tick(2000); Require(rig.Requests == 1, "A paused session continued generating");
            rig.Chatter.SetPaused(false, 2000); rig.Tick(2001); var next = rig.Take();
            Require(next.Batch.Input("").Data["appointments"] is JArray { Count: 0 }, "Resume regenerated existing people");
            var id = (string)next.Batch.Input("").Data["speakers"]![0]!["id"]!; rig.Complete(next, id); rig.Pump(2001);
            rig.Chatter.SetPaused(true, 2002);
            Require(rig.Chatter.NextNotification(2002) == null && rig.Chatter.History.Count == 1 && rig.Chatter.Person("unit:1") == person,
                "Pause retained a notification or erased people/history");
        });
        check("Pausing cancels an in-flight appointment and rejects its late result without disabling targets", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0); rig.Tick(1); var old = rig.Take();
            rig.Chatter.SetPaused(true, 1); rig.Complete(old, "unit:1"); rig.Tick(100);
            Require(old.Token.IsCancellationRequested && !rig.Chatter.Busy && rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1") == null && rig.Chatter.History.Count == 0,
                "A late result survived pause or the target was disabled");
            rig.Chatter.SetPaused(false, 100); rig.Tick(101); var fresh = rig.Take(); rig.Complete(fresh); rig.Pump(101);
            Require(rig.Chatter.Person("unit:1") != null && rig.Chatter.History.Count == 0, "Resume failed to appoint a silent participant");
        });
        check("Large appointment batches complete without capturing situation data or forcing extra speech", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, CrewBatch.MaximumAppointments + 1).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets);
            rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1); var first = rig.Take(); rig.Complete(first); rig.Pump(1);
            Require(targets.Count(t => rig.Chatter.Person(t.Id) != null) == CrewBatch.MaximumAppointments, "Appointment batching ignored its bound");
            rig.Tick(2); var second = rig.Take(); var data = second.Batch.Input("").Data;
            Require(rig.Captures == 1 && data["units"] == null && data["speakers"] is JArray { Count: 0 } && data["appointments"] is JArray { Count: 1 },
                "Appointment-only work captured a situation or forced speech");
            rig.Complete(second); rig.Pump(2);
            Require(targets.All(t => rig.Chatter.Person(t.Id) != null) && rig.Chatter.History.Count == 0, "Some silent appointments were lost");
        });
        check("Mission usage survives history trimming, selection changes, pause and global suspension but resets for a new mission", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            for (var i = 0; i < 201; i++)
            {
                var now = 1 + i * 80; rig.Tick(now); var request = rig.Take(); rig.Complete(request, "unit:1"); rig.Pump(now);
            }
            var usage = rig.Chatter.Usage.Snapshot;
            Require(rig.Chatter.History.Count == 200 && usage.ApiRequests == 201 && usage.KnownTokens == 201 * 12 && usage.UnknownRequests == 0 && usage.LastTokens == 12,
                "Usage followed retained history instead of all completed requests");
            rig.Chatter.SetPaused(true, 20000); rig.Chatter.SetPaused(false, 20000); rig.SetEnabled("unit:1", false, 20000);
            rig.SetEnabled("unit:1", true, 20000); rig.Chatter.Suspend();
            var api = new CrewLocalApi(rig.Chatter, () => 20000, () => { }); var status = api.Status();
            Require(ReferenceEquals(rig.Chatter.Usage.Snapshot, usage) && (int?)status["usage"]!["api_requests"] == 201 && (long?)status["usage"]!["known_total_tokens"] == 201 * 12,
                "A lifecycle operation reset usage or the developer API reported different totals");
            using var nextMission = new CrewChatter(_ => { });
            Require(nextMission.Usage.Snapshot.ApiRequests == 0 && nextMission.Usage.Snapshot.KnownTokens == 0 && nextMission.Usage.Snapshot.LastTokens == null,
                "A new mission inherited the previous mission's usage");
        });
        check("Late usage is counted while paused even though the cancelled remark and appointment are discarded", () =>
        {
            using var rig = new Rig(); rig.Chatter.Refresh(new[] { Target(1) }); rig.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var pending = rig.Take(); rig.Chatter.SetPaused(true, 1); rig.Complete(pending, "unit:1");
            Require(SpinWait.SpinUntil(() => rig.Chatter.Usage.Snapshot.ApiRequests == 1, 3000), "Late usage was lost when the generation task was detached");
            rig.Tick(100);
            Require(rig.Chatter.Paused && rig.Chatter.Usage.Snapshot.KnownTokens == 12 && rig.Chatter.History.Count == 0 && rig.Chatter.Person("unit:1") == null,
                "Late usage resumed speaking or allowed a stale remark or appointment");
        });
        check("A failed generation pauses all Chatter without changing selection, people or history and resumes only explicitly", () =>
        {
            foreach (var failure in new Exception[] { new CrewException("invalid_response"), new AiConnectionException("api_error", 503) })
            {
                using var rig = new Rig(); var targets = Enumerable.Range(1, 6).Select(i => Target(i)).ToArray(); rig.Chatter.Refresh(targets);
                rig.Chatter.ApplyTargets(targets.Select(t => t.Id).ToArray(), 0); rig.Tick(1); var initial = rig.Take();
                var first = (string)initial.Batch.Input("").Data["speakers"]![0]!["id"]!; rig.Complete(initial, first); rig.Pump(1);
                var people = targets.ToDictionary(t => t.Id, t => rig.Chatter.Person(t.Id));
                rig.Tick(77); var failed = rig.Take(); failed.Completion.SetException(failure); rig.Pump(77);
                Require(rig.Chatter.Paused && rig.Chatter.Error != null && rig.Errors.Count == 1 && targets.All(t => rig.Chatter.Enabled(t.Id)) &&
                    targets.All(t => rig.Chatter.Person(t.Id) == people[t.Id]) && rig.Chatter.History.Count == 1 && rig.Chatter.NextNotification(77) == null,
                    "An error lost selection, people or history, or did not pause the whole session");
                rig.Tick(1000); Require(rig.Requests == 2 && !rig.Chatter.Busy && rig.Chatter.Usage.Snapshot.ApiRequests == 2,
                    "An error automatically retried or lost request accounting");
                var selected = rig.Chatter.Targets.Select(t => t.Id).ToArray(); rig.Chatter.ApplyTargets(selected.Take(5).ToArray(), 1000); rig.Tick(1000);
                Require(rig.Chatter.Paused && rig.Requests == 2, "Editing selection silently resumed an error pause");
                rig.Chatter.SetPaused(false, 1000); Require(rig.Chatter.Error == null && !rig.Chatter.Paused, "Explicit resume retained an error or paused state");
                rig.Tick(1001); var resumed = rig.Take();
                Require(resumed.Batch.Input("").Data["appointments"] is JArray { Count: 0 }, "Resume reappointed existing people");
                rig.Complete(resumed); rig.Pump(1001); Require(!rig.Chatter.Paused && rig.Chatter.Error == null, "A successful resumed request retained an error");
            }
        });
        check("A missing Chatter connection pauses generation while retaining selected units", () =>
        {
            using var chatter = new CrewChatter(_ => { }); chatter.Refresh(new[] { Target(1), Target(2) }); chatter.ApplyTargets(new[] { "unit:1", "unit:2" }, 0);
            chatter.Tick(1, false, new CrewSettings(), "en", _ => throw new InvalidOperationException("Unexpected capture"),
                (_, _, _, _) => throw new InvalidOperationException("Unexpected request"));
            Require(chatter.Paused && chatter.Error == "connection_required" && chatter.Enabled("unit:1") && chatter.Enabled("unit:2") && chatter.Usage.Snapshot.ApiRequests == 0,
                "Missing configuration cleared user selection or sent a request");
        });
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
    private static JObject AppointmentRow(string id, string name = "Fictional") => new() { ["id"] = id, ["person_name"] = name, ["personality"] = "Calm" };
    private static AiReply Reply(JArray rows, JArray? appointments = null) => new("", new[] { new AiToolCall("chatter", CrewBatch.ToolName, new JObject { ["appointments"] = appointments ?? new JArray(), ["remarks"] = rows }) }, 12);
    private static CrewTarget Target(int id, params int[] members) => new("unit:" + id, "Unit " + id, "US", "captain", members.Length == 0 ? new[] { id } : members, true);
    private static CrewTarget Follower(int id) => new("unit:" + id, "Unit " + id, "US", "captain", new[] { id }, false);
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
        internal int Captures, Requests;
        private readonly int mainThread = Environment.CurrentManagedThreadId;
        private readonly ConcurrentQueue<Call> calls = new();
        internal CrewSettings Settings;
        internal bool GamePaused;
        internal JObject Situation = new();
        internal Rig(Random? random = null, CrewSettings? settings = null)
        { Chatter = new CrewChatter(Errors.Add, random ?? new Random(1)); Settings = settings ?? new CrewSettings("chatter", 1, 10); }
        internal void Tick(double now) => Chatter.Tick(now, GamePaused, Settings, "en", _ =>
        { Require(Environment.CurrentManagedThreadId == mainThread, "Capture ran on a worker thread"); Captures++; return Situation; },
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
        internal void SetEnabled(string id, bool enabled, double now)
        {
            var selected = new HashSet<string>(Chatter.Targets.Where(t => Chatter.Enabled(t.Id)).Select(t => t.Id));
            if (enabled) selected.Add(id); else selected.Remove(id); Chatter.ApplyTargets(selected, now);
        }
        internal void Complete(Call call, params string[] ids)
        {
            var appointments = new JArray(((JArray)call.Batch.Input("").Data["appointments"]!).Select(t => AppointmentRow((string)t["id"]!)));
            call.Completion.SetResult(call.Batch.Read(Reply(new JArray(ids.Select(Row)), appointments)));
        }
        internal void Pump(double now) => TickUntil(now, () => !Chatter.Busy);
        internal void TickUntil(double now, Func<bool> done) => Require(SpinWait.SpinUntil(() => { Tick(now); return done(); }, 3000), "Chatter did not complete");
        public void Dispose() => Chatter.Dispose();
    }
}
