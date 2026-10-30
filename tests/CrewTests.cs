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
        check("Reports opt in only on saved general settings and remain enabled after unrelated category saves", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "options.json"); var store = new AdvisorOptionsStore(path);
            Require(!store.Current.CrewEnabled && !File.Exists(path), "Reports started or wrote settings by default");
            store.SaveGeneral(store.Current.TextSize, "Commander", crewEnabled: true);
            store.SaveInformation(AdvisorInformation.Default); store.SaveConsultation(7, 1, "adviser-only instruction");
            store.SaveDeveloper(false); store.SaveCommands(new[] { "mock.disabled" }); store.SaveCampaign(false);
            store.SaveTemplates(new AdvisorTemplateOptions()); store.SaveGeneral(store.Current.TextSize, "Commander");
            Require(new AdvisorOptionsStore(path).Current.CrewEnabled, "Another settings form reset AI Crew");
            store.SaveGeneral(store.Current.TextSize, "Commander", crewEnabled: false);
            Require(!new AdvisorOptionsStore(path).Current.CrewEnabled, "A saved disable did not survive reopening");
            Throws<AdvisorOptionsException>(() => AdvisorOptions.Parse("{\"crew_enabled\":\"true\"}"));
        }));
        check("Reports settings persist independently and failed writes cannot apply a draft", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(store.Current.ConnectionId == null && !File.Exists(path), "Opening Reports implicitly selected or saved a connection");
            store.Save(new CrewSettings("reports-only", 1, 2, 9));
            var reopened = new CrewSettingsStore(path);
            Require(reopened.Current.ConnectionId == "reports-only" && reopened.Current.MinimumMinutes == 1 && reopened.Current.MaximumMinutes == 2 && reopened.Current.DisplaySeconds == 9, "Reports preferences were lost");
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => store.Save(new CrewSettings("other", 2, 3, 8)));
            Require(store.Current.ConnectionId == "reports-only", "Failed storage published the pending connection");
            Throws<CrewException>(() => new CrewSettings("id", 0, 3)); Throws<CrewException>(() => new CrewSettings("id", 3, 2));
            Throws<CrewException>(() => CrewSettings.Parse("{\"minimum_minutes\":1.5}"));
        }));
        check("Report positions default to top center, persist all six choices and reject invalid saved values", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "crew.json"); var store = new CrewSettingsStore(path);
            Require(store.Current.NotificationPosition == CrewNotificationPosition.TopCenter && !File.Exists(path), "Unset position did not use top center without writing");
            Require(CrewSettings.Parse("{}").NotificationPosition == CrewNotificationPosition.TopCenter, "An unset field did not use the requested default");
            foreach (CrewNotificationPosition position in Enum.GetValues(typeof(CrewNotificationPosition)))
            {
                store.Save(new CrewSettings("reports-only", 1, 3, 9, position));
                var read = new CrewSettingsStore(path).Current;
                Require(read.NotificationPosition == position && read.ConnectionId == "reports-only" && read.MinimumMinutes == 1 && read.MaximumMinutes == 3 && read.DisplaySeconds == 9,
                    "A saved position was lost across reopening or changed another Reports preference");
            }
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("offscreen"), new JValue(2), new JValue(true) })
                Throws<CrewException>(() => CrewSettings.Parse(new JObject { ["notification_position"] = invalid }.ToString()));
            Throws<CrewException>(() => new CrewSettings(notificationPosition: (CrewNotificationPosition)99));
        }));
        check("A report batch accepts out-of-order unique IDs, preserves appointed people and rejects missing or duplicate reporters", () =>
        {
            var person = new CrewPerson("Existing", "Calm"); var input = new JObject { ["units"] = new JArray("snapshot") };
            var batch = new CrewBatch(input, new Dictionary<string, CrewPerson?> { ["unit:1"] = person, ["unit:2"] = null });
            input["units"] = new JArray("changed");
            var providerInput = batch.Input("connection instruction");
            Require((string?)providerInput.Data["units"]![0] == "snapshot" && providerInput.Tools.Count == 1, "Live input or game tools leaked into a batch");
            var rows = new JArray(Row("unit:2", "New"), Row("unit:1", "Unwanted rename")); var result = batch.Read(Reply(rows));
            Require(result.Count == 2 && result.Single(x => x.Id == "unit:1").Person == person && result.Single(x => x.Id == "unit:2").Person.Name == "New", "Reporter identity or appointment was corrupted");
            foreach (var invalid in new[] { new JArray(Row("unit:1")), new JArray(Row("unit:1"), Row("unit:1")), new JArray(Row("unit:1"), Row("unit:99")) })
                Throws<CrewException>(() => batch.Read(Reply(invalid)));
            Throws<CrewException>(() => batch.Read(new AiReply("plain text without a report", Array.Empty<AiToolCall>(), null)));
        });
        check("Targets remain silent until enabled, coalesce into one batch and use real elapsed time for the next interval", () =>
        {
            using var rig = new Rig(); var targets = new[] { Target(1), Target(2) }; rig.Reports.Refresh(targets);
            rig.Tick(100); Require(rig.Captures == 0 && !rig.Reports.Busy, "Eligible units automatically started sending");
            foreach (var t in targets) rig.Reports.SetEnabled(t.Id, true, 100);
            rig.Tick(100.5); Require(rig.Captures == 0, "The initial coalescing interval was skipped");
            rig.Tick(101); var call = rig.Take();
            Require(call.Batch.Input("").Data["reporters"] is JArray { Count: 2 } && rig.Captures == 1, "One enabled group produced separate captures or requests");
            rig.Complete(call, "unit:1", "unit:2"); rig.Pump(101);
            Require(rig.Reports.History.Count == 2 && rig.Reports.Person("unit:1") != null, "One batched result did not appoint both reporters");
            rig.Tick(160.9); Require(rig.Captures == 1, "Reports ignored the minimum real-time interval");
            rig.Tick(161); var next = rig.Take(); Require(rig.Captures == 2, "Elapsed real time did not trigger the next report");
            Require(next.Batch.Input("").Data["reporters"]![0]!["person"] is JObject, "A subsequent request lost the appointment");
            rig.Complete(next, "unit:1", "unit:2"); rig.Pump(161);
        });
        check("Only one report request can be in flight and a large enabled set is split without enabling new targets", () =>
        {
            using var rig = new Rig(); var targets = Enumerable.Range(1, 6).Select(i => Target(i)).ToArray(); rig.Reports.Refresh(targets);
            foreach (var t in targets.Take(5)) rig.Reports.SetEnabled(t.Id, true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Tick(20);
            Require(rig.Captures == 1, "A second request overlapped the first");
            var ids = ((JArray)first.Batch.Input("").Data["reporters"]!).Select(x => (string)x["id"]!).ToArray();
            Require(ids.Length == 4, "The batch bound was ignored");
            rig.Complete(first, ids); rig.TickUntil(20, () => rig.Captures == 2); var second = rig.Take();
            var last = ((JArray)second.Batch.Input("").Data["reporters"]!).Select(x => (string)x["id"]!).ToArray();
            Require(last.SequenceEqual(new[] { "unit:5" }) && !rig.Reports.Enabled("unit:6"), "Batch splitting enabled an unselected unit");
            rig.Complete(second, last); rig.Pump(20);
        });
        check("Disabling and re-enabling a target rejects its old response and pending notification", () =>
        {
            using var rig = new Rig(); rig.Reports.Refresh(new[] { Target(1) }); rig.Reports.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.Reports.SetEnabled("unit:1", false, 1); rig.Reports.SetEnabled("unit:1", true, 1);
            rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Reports.History.Count == 0 && rig.Reports.Person("unit:1") == null, "A stale response appointed a disabled target");
            rig.Tick(2); var fresh = rig.Take(); rig.Complete(fresh, "unit:1"); rig.Pump(2);
            Require(rig.Reports.History.Count == 1, "A fresh enabled response was lost");
            rig.Reports.SetEnabled("unit:1", false, 2); rig.Reports.SetEnabled("unit:1", true, 2);
            Require(rig.Reports.NextNotification(2) == null, "A previous activation's notification survived toggling");
        });
        check("Global suspension cancels in-flight work, turns targets off, preserves session people and rejects late results", () =>
        {
            using var rig = new Rig(); rig.Reports.Refresh(new[] { Target(1) }); rig.Reports.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var first = rig.Take(); rig.Complete(first, "unit:1"); rig.Pump(1);
            var person = rig.Reports.Person("unit:1"); rig.Tick(61); var late = rig.Take(); rig.Reports.Suspend();
            Require(late.Token.IsCancellationRequested && !rig.Reports.Enabled("unit:1") && rig.Reports.NextNotification(61) == null, "Suspension retained work or pending notifications");
            rig.Complete(late, "unit:1"); rig.Reports.SetEnabled("unit:1", true, 61); rig.Tick(62); var fresh = rig.Take();
            rig.Complete(fresh, "unit:1"); rig.Pump(62);
            Require(rig.Reports.History.Count == 2 && rig.Reports.Person("unit:1") == person, "A late result survived suspension or replaced the existing appointment");
        });
        check("Formation changes invalidate a captured report while a returning reporter retains its appointed person", () =>
        {
            using var rig = new Rig(); rig.Reports.Refresh(new[] { Target(1, 1, 2) }); rig.Reports.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var old = rig.Take(); rig.Reports.Refresh(new[] { Target(1, 1, 3) }); rig.Complete(old, "unit:1"); rig.Pump(1);
            Require(rig.Reports.History.Count == 0, "A report about the former formation was applied");
            rig.Tick(61); var current = rig.Take(); rig.Complete(current, "unit:1"); rig.Pump(61); var person = rig.Reports.Person("unit:1");
            rig.Reports.Refresh(Array.Empty<CrewTarget>()); rig.Reports.Refresh(new[] { Target(1) });
            Require(rig.Reports.Person("unit:1") == person && person != null && !rig.Reports.Enabled("unit:1"), "Returning to eligibility lost the person or silently resumed reporting");
        });
        check("An invalid result stops only the affected batch and remains a visible logged error", () =>
        {
            using var rig = new Rig(); rig.Reports.Refresh(new[] { Target(1), Target(2) }); rig.Reports.SetEnabled("unit:1", true, 0);
            rig.Tick(1); var call = rig.Take(); call.Completion.SetException(new CrewException("invalid_response")); rig.Pump(1);
            Require(rig.Reports.Error == "invalid_response" && rig.Errors.Count == 1 && !rig.Reports.Enabled("unit:1") && rig.Reports.History.Count == 0, "An invalid response disappeared or left automatic requests running");
        });
    }
    private static JObject Row(string id, string name = "Fictional") => new() { ["id"] = id, ["name"] = name, ["personality"] = "Calm", ["text"] = "Maintaining station." };
    private static AiReply Reply(JArray rows) => new("", new[] { new AiToolCall("reports", CrewBatch.ToolName, new JObject { ["reports"] = rows }) }, 12);
    private static CrewTarget Target(int id, params int[] members) => new("unit:" + id, "Unit " + id, "US", "captain", members.Length == 0 ? new[] { id } : members);
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
        internal readonly TaskCompletionSource<IReadOnlyList<CrewSubmission>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Call(CrewBatch batch, CancellationToken token) { Batch = batch; Token = token; }
    }
    private sealed class Rig : IDisposable
    {
        internal readonly List<Exception> Errors = new();
        internal readonly CrewReports Reports;
        internal int Captures;
        private readonly int mainThread = Environment.CurrentManagedThreadId;
        private readonly ConcurrentQueue<Call> calls = new();
        private readonly CrewSettings settings = new("reports", 1, 1, 10);
        internal Rig() => Reports = new CrewReports(Errors.Add, new Random(1));
        internal void Tick(double now) => Reports.Tick(now, settings, "en", _ =>
        { Require(Environment.CurrentManagedThreadId == mainThread, "Capture ran on a worker thread"); Captures++; return new JObject(); },
            (batch, _, token) => { var call = new Call(batch, token); calls.Enqueue(call); return call.Completion.Task; });
        internal Call Take()
        {
            Call? call = null; Require(SpinWait.SpinUntil(() => calls.TryDequeue(out call), 3000), "Report generation did not start"); return call!;
        }
        internal void Complete(Call call, params string[] ids) => call.Completion.SetResult(call.Batch.Read(Reply(new JArray(ids.Select(id => Row(id))))));
        internal void Pump(double now) => TickUntil(now, () => !Reports.Busy);
        internal void TickUntil(double now, Func<bool> done) => Require(SpinWait.SpinUntil(() => { Tick(now); return done(); }, 3000), "Reports did not complete");
        public void Dispose() => Reports.Dispose();
    }
}
