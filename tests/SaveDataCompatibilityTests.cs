using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Shared;

internal static class SaveDataCompatibilityTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Save migrations advance in order without modifying the saved input and current data bypasses them", () =>
        {
            var calls = new List<int>();
            var format = new SaveDataFormat(1, 3, (value, version) =>
            {
                calls.Add(version); Advance(value, version);
            });
            var old = Document(1, new JObject { ["amount"] = 7 });
            var before = old.ToString();
            var upgraded = format.Upgrade(old);
            Require(upgraded.SourceVersion == 1 && (int?)upgraded.Document?["version"] == 3 &&
                (int?)upgraded.Document?["data"]?["quantity"] == 14 && calls.SequenceEqual(new[] { 1, 2 }) && old.ToString() == before,
                "Migration order, values or the original saved document changed");
            var current = format.Upgrade(upgraded.Document!);
            Require(ReferenceEquals(current.Document, upgraded.Document) && calls.Count == 2, "Current data ran through a migration again");
        });
        check("Retiring the oldest save migration leaves the remaining migration and current reader usable", () =>
        {
            var calls = new List<int>();
            var format = new SaveDataFormat(2, 3, (value, version) =>
            {
                Require(version == 2, "A retired migration was invoked"); calls.Add(version); Advance(value, version);
            });
            var retired = format.Upgrade(new JObject { ["version"] = 1, ["obsolete_payload"] = "not the current structure" });
            Require(retired.SourceVersion == 1 && retired.Document == null && calls.Count == 0, "A retired schema entered normal reading or conversion");
            var state = new AdvisorSaveData(format);
            state.Restore(Bytes(Document(2, new JObject { ["quantity"] = 7 })));
            Require((int?)state.Data["quantity"] == 14 && calls.SequenceEqual(new[] { 2 }) && state.UnsupportedData == null,
                "Retiring v1 also broke supported v2 data");
            var saved = JObject.Parse(Encoding.UTF8.GetString(state.Capture()!));
            Require((int?)saved["version"] == 3 && (int?)saved["data"]?["quantity"] == 14, "Saving failed to write the current format");
        });
        check("Unsupported old MOD data is reset once and permits new saves without touching the original bytes", () =>
        {
            var state = new AdvisorSaveData(new SaveDataFormat(2, 2));
            var session = new SaveDataSession(state.Reset, state.Capture, state.Restore);
            var original = Bytes(Document(1, new JObject { ["obsolete"] = 7 })); var before = original.ToArray();
            var mission = new object(); state.Data["previous_mission"] = true;
            session.Begin(mission); session.Stage(mission, SaveDataSection.Encode(original)); session.Complete(mission, true);
            var issue = state.UnsupportedData;
            Require(issue?.SourceVersion == 1 && !state.Data.HasValues && session.Capture() == null,
                "Unsupported old data blocked saving, retained previous state or failed to report the reset");
            session.Complete(mission, true);
            Require(ReferenceEquals(issue, state.UnsupportedData), "The same load generated another compatibility notice");
            state.Data["new_state"] = 8;
            var saved = JObject.Parse(Encoding.UTF8.GetString(SaveDataSection.Decode(session.Capture())!));
            Require((int?)saved["version"] == 2 && (int?)saved["data"]?["new_state"] == 8 && saved["data"]?["obsolete"] == null && original.SequenceEqual(before),
                "Saving current state reused discarded data or changed the original save snapshot");
            session.Begin(new object());
            Require(state.UnsupportedData == null && !state.Data.HasValues, "A new load inherited an earlier compatibility notice or data");
            state.Restore(original); var nextIssue = state.UnsupportedData;
            Require(nextIssue != null && !ReferenceEquals(issue, nextIssue), "A later load cannot notify about the same old file again");
            state.Restore(null);
            Require(state.UnsupportedData == null && state.Capture() == null, "A save with no MOD section produced a compatibility notice");
        });
        check("Malformed or future save formats remain errors and never become unsupported-old results", () =>
        {
            var state = new AdvisorSaveData(new SaveDataFormat(2, 3, Advance)); state.Data["existing"] = 9;
            foreach (var json in new[] {
                "{}", "{\"version\":0}", "{\"version\":-1}", "{\"version\":null}", "{\"version\":\"1\"}",
                "{\"version\":1.5}", "{\"version\":2147483648}", "{\"version\":4}", "{\"version\":3}",
                "{\"version\":1,\"version\":3,\"data\":{}}", "{\"version\":3,\"data\":{}} {}", "not json" })
                Reject(() => state.Restore(Encoding.UTF8.GetBytes(json)));
            Require(state.UnsupportedData == null && (int?)state.Data["existing"] == 9, "A malformed or future schema was silently reset");
            var session = new SaveDataSession(state.Reset, state.Capture, state.Restore); var mission = new object();
            session.Begin(mission); session.Stage(mission, SaveDataSection.Encode(Bytes(Document(4, new JObject()))));
            Reject(() => session.Complete(mission, true)); Reject(() => session.Capture());
            Require(state.UnsupportedData == null, "A future schema was treated as a retired one");
        });
        check("Broken or missing save migrations fail visibly without mutating source or committed runtime data", () =>
        {
            var old = Document(1, new JObject { ["amount"] = 7 }); var before = old.ToString();
            var formats = new[] {
                new SaveDataFormat(1, 3),
                new SaveDataFormat(1, 3, (value, version) => { }),
                new SaveDataFormat(1, 3, (value, version) => value["version"] = 3),
                new SaveDataFormat(1, 3, (value, version) =>
                {
                    if (version == 2) throw new InvalidOperationException("Failed later migration");
                    Advance(value, version);
                })
            };
            foreach (var format in formats)
            {
                var state = new AdvisorSaveData(format); state.Data["existing"] = 9;
                Reject(() => state.Restore(Bytes(old)));
                Require(old.ToString() == before && (int?)state.Data["existing"] == 9 && state.UnsupportedData == null,
                    "A failed migration changed data or was mistaken for unsupported old data");
            }
        });
        check("MOD save restoration preserves date-like strings as strings", () =>
        {
            var state = new AdvisorSaveData(); state.Data["timestamp_text"] = "1985-06-01T01:02:03Z";
            var bytes = state.Capture()!; state.Restore(bytes);
            Require(state.Data["timestamp_text"]!.Type == JTokenType.String && state.Capture()!.SequenceEqual(bytes),
                "Restoration changed stored text into a date value");
        });
    }
    // Test-only schemas exercise the lifecycle before any real feature needs a format change.
    private static void Advance(JObject value, int version)
    {
        var data = (JObject)value["data"]!;
        if (version == 1) { data["quantity"] = data["amount"]!.DeepClone(); data.Remove("amount"); }
        else if (version == 2) data["quantity"] = (int)data["quantity"]! * 2;
        else throw new InvalidOperationException("No migration for this test format");
        value["version"] = version + 1;
    }
    private static JObject Document(int version, JObject data) => new() { ["version"] = version, ["data"] = data };
    private static byte[] Bytes(JObject document) => Encoding.UTF8.GetBytes(document.ToString(Formatting.None));
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { return; }
        throw new Exception("Expected invalid save data or a broken migration to fail.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
