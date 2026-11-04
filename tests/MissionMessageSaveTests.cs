using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Shared;

internal static class MissionMessageSaveTests
{
    private static readonly DateTime Time = new(1985, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static void Run(Action<string, Action> check)
    {
        check("Mission history survives embedded save round-trips with publication times, order and detached values", () =>
        {
            var history = new BattleMissionMessageHistory(); var gateway = new AdvisorSaveData(missionMessages: history); var mission = new object();
            gateway.Begin(mission); gateway.Restore(null); history.Publish(mission, "Orders", "Retained order 日本語 😀\n[File] // = text", Time);
            history.Publish(mission, "Update", "Protect the convoy.", Time.AddMinutes(1));
            var expected = history.Capture(mission); var saved = gateway.Capture()!;
            history.Publish(mission, "After saving", "Not part of the saved snapshot.", Time.AddMinutes(2));
            var loaded = new BattleMissionMessageHistory(); var restored = new AdvisorSaveData(missionMessages: loaded);
            var options = new AdvisorOptions(); var session = new SaveDataSession(restored.Begin, () => restored.PrepareCapture(options), restored.Restore);
            var next = new object(); session.Begin(next); session.Stage(next, SaveDataSection.Encode(saved)); session.Complete(next, true);
            Require(JToken.DeepEquals(expected, loaded.Capture(next)), "Saved text, times or ordering changed across loading");
            session.Complete(next, true); Require(loaded.Capture(next).Count == 2, "The same save was restored more than once");
            var archive = loaded.SaveData()!; archive["messages"]![0]!["text"] = "edited copy";
            Require(JToken.DeepEquals(expected, loaded.Capture(next)), "Editing a serialized snapshot changed live history");
            var third = new object(); var again = SaveDataSection.Decode(session.Capture())!;
            restored.Begin(third); restored.Restore(again);
            Require(JToken.DeepEquals(expected, loaded.Capture(third)), "Repeated saves accumulated history copies");
        });
        check("Saved history merges load-time publications without multiplying re-published saved popups", () =>
        {
            var source = new BattleMissionMessageHistory(); var first = new object(); source.Begin(first);
            source.Publish(first, "Orders", "Same message", Time); source.Publish(first, "Orders", "Same message", Time);
            var saved = source.SaveData()!; var copy = saved.ToString();
            var loaded = new BattleMissionMessageHistory(); var next = new object(); loaded.Begin(next);
            for (var i = 0; i < 3; i++) loaded.Publish(next, "Orders", "Same message", Time);
            loaded.Publish(next, "Orders", "Same message", Time.AddMinutes(1));
            loaded.Restore(saved);
            var rows = loaded.Capture(next);
            Require(rows.Count == 4 && rows.Select(r => (string)r["id"]!).Distinct().Count() == 4 &&
                (string?)rows[3]!["published_at_utc"] == "1985-06-01T00:01:00Z" && saved.ToString() == copy,
                "Merging lost distinct publications, duplicated replayed messages, reused IDs or changed the archive");
            for (var load = 0; load < 3; load++)
            {
                var snapshot = loaded.Capture(next); saved = loaded.SaveData()!; next = new object(); loaded.Begin(next);
                foreach (var row in snapshot) loaded.Publish(next, "", (string)row["text"]!, DateTime.Parse((string)row["published_at_utc"]!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal));
                loaded.Restore(saved); Require(JToken.DeepEquals(snapshot, loaded.Capture(next)), "Replay increased history on a later load");
            }
        });
        check("Missing saved history retains only messages published during the new load without inventing earlier events", () =>
        {
            var history = new BattleMissionMessageHistory(); var gateway = new AdvisorSaveData(missionMessages: history); var first = new object();
            gateway.Begin(first); history.Publish(first, "Old mission", "Do not carry this into another load", Time);
            var next = new object(); gateway.Begin(next); history.Publish(next, "New load", "Published while loading", Time);
            gateway.Restore(null);
            Require(history.Capture(next).Count == 1 && (string?)history.Capture(next)[0]!["text"] == "New load\n\nPublished while loading",
                "A save without MOD data erased load-time publications or invented older history");
            gateway.Begin(next); gateway.Restore(Bytes(new JObject { ["version"] = 1, ["data"] = new JObject() }));
            Require(history.Capture(next).Count == 0 && gateway.Capture() == null, "An empty existing envelope acquired invented message history");
        });
        check("Saving OFF excludes message history but still restores it and preserves it for re-enabling", () =>
        {
            var history = new BattleMissionMessageHistory(); var gateway = new AdvisorSaveData(missionMessages: history); var first = new object();
            gateway.Begin(first); history.Publish(first, "Orders", "Retained order", Time); var saved = gateway.Capture()!;
            var options = new AdvisorOptions(includeModSaveData: false);
            var session = new SaveDataSession(gateway.Begin, () => gateway.PrepareCapture(options), gateway.Restore);
            var next = new object(); session.Begin(next); session.Stage(next, SaveDataSection.Encode(saved)); session.Complete(next, true);
            Require(history.Capture(next).Count == 1 && session.Capture() == null, "Saving OFF prevented history restoration or included history anyway");
            options = new AdvisorOptions(); var included = JObject.Parse(Encoding.UTF8.GetString(SaveDataSection.Decode(session.Capture())!));
            Require(included["data"]?["mission_messages"]?["messages"]?.Count() == 1, "Re-enabling saving lost retained messages");
            foreach (var enabled in new[] { false, true })
            {
                var policy = AdvisorInformation.All.With(InformationKind.BattleMissionMessages, enabled);
                var query = new BattleQuerySession(new BattleSnapshot(BattleMissionMessageTests.Snapshot(history.Capture(next))), "en", "Mission context", information: policy);
                Require(query.ExportSituation().ToString().Contains("Retained order", StringComparison.Ordinal) == enabled,
                    "Persistent history bypassed the independent AI information selection");
            }
        });
        check("Malformed mission-message archives fail atomically instead of becoming empty valid history", () =>
        {
            var history = new BattleMissionMessageHistory(); var mission = new object(); history.Begin(mission); history.Publish(mission, "Current", "Keep this", Time);
            var expected = history.Capture(mission);
            var row = new JObject { ["published_at_utc"] = "1985-06-01T00:00:00Z", ["text"] = "Saved" };
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JArray(), new JObject(),
                new JObject { ["version"] = 2, ["messages"] = new JArray() },
                new JObject { ["version"] = 1, ["messages"] = new JObject() },
                Archive(new JObject { ["text"] = "Missing time" }), Archive(new JObject { ["published_at_utc"] = "invalid", ["text"] = "Saved" }),
                Archive(new JObject { ["published_at_utc"] = row["published_at_utc"]!.DeepClone(), ["text"] = " " }),
                Archive(new JObject { ["published_at_utc"] = row["published_at_utc"]!.DeepClone(), ["text"] = 9 }) })
            {
                Reject(() => history.Restore(invalid)); Require(JToken.DeepEquals(expected, history.Capture(mission)), "Invalid data replaced valid live history");
            }
            Reject(() => new BattleMissionMessageHistory().Restore(Archive(row)));
        });
        check("Retired message formats are reported independently without erasing newly published messages or unrelated MOD data", () =>
        {
            var history = new BattleMissionMessageHistory(new SaveDataFormat(2, 2));
            var gateway = new AdvisorSaveData(missionMessages: history); var mission = new object(); gateway.Begin(mission);
            history.Publish(mission, "New", "Published this load", Time);
            var envelope = new JObject { ["version"] = 1, ["data"] = new JObject { ["other_feature"] = 7,
                ["mission_messages"] = Archive(new JObject { ["published_at_utc"] = "1985-06-01T00:00:00Z", ["text"] = "Retired" }) } };
            gateway.Restore(Bytes(envelope));
            Require(gateway.UnsupportedData?.Part == "mission_messages" && gateway.UnsupportedData.SourceVersion == 1 &&
                history.Capture(mission).Count == 1 && (int?)gateway.Data["other_feature"] == 7,
                "Retiring one feature reset unrelated information, lost new messages or failed to identify the affected part");
            var saved = JObject.Parse(Encoding.UTF8.GetString(gateway.Capture()!));
            Require((int?)saved["data"]?["mission_messages"]?["version"] == 2 &&
                (string?)saved["data"]?["mission_messages"]?["messages"]?[0]?["text"] == "New\n\nPublished this load",
                "A later save carried retired contents forward instead of current history");
        });
    }
    private static JObject Archive(JObject row) => new() { ["version"] = 1, ["messages"] = new JArray(row) };
    private static byte[] Bytes(JObject value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { return; }
        throw new Exception("Expected invalid history or an invalid lifecycle to be rejected.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
