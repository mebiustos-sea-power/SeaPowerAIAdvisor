using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Shared;

internal static class SaveDataTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Embedded save data preserves Unicode, multiline text and other owners without accumulating revisions", () =>
        {
            var sections = new Dictionary<string, Dictionary<string, string>> {
                ["File"] = new() { ["Version"] = "1", ["SaveFile"] = "True" } };
            var a = new SaveDataSection("example.advisor"); var b = new SaveDataSection("example.creator");
            var bytes = Encoding.UTF8.GetBytes("日本語 😀\n[File]\n// comment = value");
            var encoded = SaveDataSection.Encode(bytes);
            a.Write(sections, encoded); b.Write(sections, SaveDataSection.Encode(new byte[] { 1, 2 }));
            bytes[0] = 0;
            for (var i = 0; i < 3; i++) a.Write(sections, encoded);
            Require(Encoding.UTF8.GetString(SaveDataSection.Decode(a.Read(sections))!) == "日本語 😀\n[File]\n// comment = value" &&
                sections.Count == 3 && sections["File"]["Version"] == "1", "Writing altered the snapshot or native sections, or accumulated entries");
            a.Write(sections, null);
            Require(a.Read(sections) == null && SaveDataSection.Decode(b.Read(sections))!.SequenceEqual(new byte[] { 1, 2 }),
                "Removing absent data affected another owner");
        });
        check("Load restoration waits for both metadata and ready units and runs once", () =>
        {
            var mission = new object(); var calls = new List<string>(); byte[]? restored = null;
            var session = new SaveDataSession(() => calls.Add("reset"), () => restored, bytes => { calls.Add("restore"); restored = bytes; });
            session.Begin(mission); session.Complete(mission, true);
            Require(calls.SequenceEqual(new[] { "reset" }), "Restored before metadata was read");
            session.Stage(mission, SaveDataSection.Encode(new byte[] { 4, 5 }));
            session.Complete(mission, false); session.Complete(new object(), true);
            Require(calls.Count == 1, "Restored while units were loading or into another mission");
            session.Complete(mission, true); session.Complete(mission, true);
            Require(calls.SequenceEqual(new[] { "reset", "restore" }) && restored!.SequenceEqual(new byte[] { 4, 5 }), "Restored more than once or lost bytes");
        });
        check("A new load discards the previous pending payload and cannot save unrestored state", () =>
        {
            var first = new object(); var second = new object(); byte[]? state = new byte[] { 99 }; var count = 0;
            var session = new SaveDataSession(() => state = null, () => state, bytes => { state = bytes; count++; });
            session.Begin(first); session.Stage(first, SaveDataSection.Encode(new byte[] { 7 }));
            session.Begin(second); session.Complete(first, true); Reject(() => session.Capture());
            Reject(() => session.Stage(first, SaveDataSection.Encode(new byte[] { 8 })));
            session.Stage(second, null); session.Complete(second, true);
            Require(count == 1 && state == null && session.Capture() == null, "Old mission data leaked into a new or unmodified save");
        });
        check("Malformed embedded data remains an error rather than becoming an empty valid save", () =>
        {
            var section = new SaveDataSection("example"); var sections = new Dictionary<string, Dictionary<string, string>> { ["ModData.example"] = new() };
            Reject(() => section.Read(sections));
            var mission = new object(); var calls = 0;
            var session = new SaveDataSession(() => { }, () => new byte[] { 1 }, _ => calls++);
            session.Begin(mission); Reject(() => session.Stage(mission, "not-base64!"));
            session.Complete(mission, true); Reject(() => session.Capture());
            Require(calls == 0, "Malformed data was restored as absent data");
        });
        check("A failed feature reset or restoration blocks saving and is not retried every frame", () =>
        {
            var mission = new object(); var restores = 0; var resetFails = true;
            var session = new SaveDataSession(() => { if (resetFails) throw new InvalidOperationException("reset"); }, () => null,
                _ => { restores++; throw new InvalidOperationException("restore"); });
            Reject(() => session.Begin(mission)); Reject(() => session.Stage(mission, null)); Reject(() => session.Capture());
            resetFails = false; session.Begin(mission); session.Stage(mission, null);
            Reject(() => session.Complete(mission, true)); session.Complete(mission, true); Reject(() => session.Capture());
            Require(restores == 1, "Failed restore ran repeatedly or reset failure was hidden");
        });
        check("Capture failure allows an explicit retry without losing the restored mission", () =>
        {
            var mission = new object(); var fail = true;
            var session = new SaveDataSession(() => { }, () => fail ? throw new InvalidOperationException("capture") : new byte[] { 3 }, _ => { });
            session.Begin(mission); session.Stage(mission, null); session.Complete(mission, true);
            Reject(() => session.Capture()); fail = false;
            Require(SaveDataSection.Decode(session.Capture())!.SequenceEqual(new byte[] { 3 }), "Capture failure damaged the active session");
        });
        check("The MOD gateway round-trips detached feature data and emits nothing for a mission with no state", () =>
        {
            var original = new AdvisorSaveData(); Require(original.Capture() == null, "An empty mission produced a dummy save section");
            original.Data["probe"] = new JObject { ["unit_id"] = 42, ["name"] = "艦長 😀", ["count"] = 1 };
            var bytes = original.Capture()!; original.Data["probe"]!["count"] = 2;
            var restored = new AdvisorSaveData(); restored.Restore(bytes);
            Require((int?)restored.Data["probe"]!["count"] == 1 && (string?)restored.Data["probe"]!["name"] == "艦長 😀",
                "The saved snapshot changed with the live feature document");
            Require(restored.Capture()!.SequenceEqual(bytes), "Re-saving an unchanged document rewrote its values");
            restored.Reset(); Require(restored.Capture() == null, "A new mission kept the prior payload");
        });
        check("Invalid or future MOD envelopes are rejected without replacing valid feature data", () =>
        {
            var state = new AdvisorSaveData(); state.Data["feature"] = 9;
            foreach (var document in new[] { "{\"version\":2,\"data\":{}}", "{\"version\":1}", "{\"version\":\"1\",\"data\":{}}", "not json" })
                Reject(() => state.Restore(Encoding.UTF8.GetBytes(document)));
            Require((int?)state.Data["feature"] == 9, "Invalid data replaced a valid runtime document");
            state.Restore(null); Require(state.Capture() == null, "Loading a save without MOD data retained old state");
        });
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidOperationException or FormatException or Newtonsoft.Json.JsonException) { return; }
        throw new InvalidOperationException("Expected invalid save data or lifecycle order to be rejected.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
