using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class CrewSaveTests
{
    private const string CharacterId = "11111111111111111111111111111111";
    private const string PortraitId = "22222222222222222222222222222222.png";
    private static readonly CrewPerson First = new("アンガス・マクレガー", "中佐", "冷静。短く丁寧に話す。", PortraitId, CharacterId);
    private static readonly CrewPerson Second = new("Jane Smith", "Lt.", "Optimistic");
    internal static void Run(Action<string, Action> check)
    {
        check("Crew saves round-trip confirmed people, participation, pause and cumulative rank votes without history or usage", () =>
        {
            using var source = new Rig(); source.Appoint();
            source.Chatter.ApplyTargets(new[] { "unit:1" }, 0);
            var edited = new CrewPerson("編集後の名前", First.Rank, "丁寧。\n語尾は短く。", PortraitId, CharacterId);
            source.Chatter.UpdatePerson("unit:1", source.Chatter.Person("unit:1")!, edited);
            source.Tick(1); source.Pump(1); Require(source.Chatter.History.Count == 1 && source.Chatter.Usage.Snapshot.ApiRequests > 0, "The fixture did not speak");
            source.Chatter.SetPaused(true, 2);
            var bytes = source.Gateway.Capture()!;
            var saved = JObject.Parse(Encoding.UTF8.GetString(bytes));
            Require(saved["data"]?["crew"]?["people"]?.Count() == 2, "Confirmed people were not embedded");
            using var loaded = new Rig(bytes);
            var person = loaded.Chatter.Person("unit:1")!;
            Require(person.Name == edited.Name && person.Rank == edited.Rank && person.Personality == edited.Personality &&
                person.PortraitId == PortraitId && person.RegisteredCharacterId == CharacterId && loaded.Chatter.Person("unit:2")!.Name == Second.Name &&
                loaded.Chatter.Enabled("unit:1") && !loaded.Chatter.Enabled("unit:2") && loaded.Chatter.Paused &&
                loaded.Chatter.RankAfterVotes == 2 && loaded.Chatter.RankBeforeVotes == 0 && !loaded.Chatter.RankBeforeName,
                "Saved identity, references, selection, pause or rank order was lost");
            Require(loaded.Chatter.History.Count == 0 && loaded.Chatter.NextNotification(100) == null && !loaded.Chatter.Busy &&
                !loaded.Chatter.HasPendingAppointments && loaded.Chatter.Usage.Snapshot.ApiRequests == 0 && loaded.Chatter.Error == null,
                "Restoration revived transient work, remarks, errors or consumption");
            Require(loaded.Gateway.Capture()!.SequenceEqual(bytes), "Round trip rewrote the confirmed state");
        });
        check("Restored Chatter starts on the first active tick and then uses the configured interval", () =>
        {
            using var original = new Rig(); original.Appoint();
            using var loaded = new Rig(original.Gateway.Capture(), 100, new CrewSettings("api", 15));
            loaded.Tick(100); loaded.Pump(100);
            Require(loaded.Requests == 1 && loaded.Chatter.History.Count == 1,
                "The restored session waited for a cycle instead of starting its initial generation");
            loaded.Tick(999); Require(loaded.Requests == 1, "The initial immediate opportunity repeated before the normal interval");
            loaded.Tick(1000); loaded.Pump(1000);
            Require(loaded.Requests == 2 && loaded.Errors.Count == 0, "The regular cycle did not follow initial completion");
        });
        check("Game pause defers the initial restored generation and silence starts the normal interval at completion", () =>
        {
            using var original = new Rig(); original.Appoint();
            using var loaded = new Rig(original.Gateway.Capture(), 100, new CrewSettings("api", 2));
            var first = new TaskCompletionSource<CrewGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
            var normal = loaded.Generate; loaded.Generate = batch => loaded.Requests == 1 ? first.Task : normal(batch);
            loaded.GamePaused = true; loaded.Tick(100); loaded.Tick(1000);
            Require(loaded.Requests == 0 && !loaded.Chatter.Busy, "Loading into game pause sent a request");
            loaded.GamePaused = false; loaded.Tick(1000);
            Require(SpinWait.SpinUntil(() => loaded.Requests == 1, 3000), "Resuming did not start the initial generation");
            loaded.Tick(1049); Require(loaded.Requests == 1 && loaded.Chatter.Busy, "Waiting for a reply launched a second generation");
            first.SetResult(new CrewGeneration(new Dictionary<string, CrewAppointment>(), Array.Empty<CrewSubmission>()));
            loaded.Pump(1050);
            Require(loaded.Requests == 1 && loaded.Chatter.History.Count == 0 && !loaded.Chatter.Paused,
                "Initial silence was retried or treated as a failure");
            loaded.GamePaused = true; loaded.Tick(1060); loaded.Tick(2060);
            loaded.GamePaused = false; loaded.Tick(2060); loaded.Tick(2169);
            Require(loaded.Requests == 1, "The interval included communication or game-paused time");
            loaded.Tick(2170); loaded.Pump(2170);
            Require(loaded.Requests == 2 && loaded.LastInput?["recent_remarks"] is JArray { Count: 0 } && loaded.Errors.Count == 0,
                "The next cycle failed or old history was resent");
        });
        check("Saved Chatter pause survives load and selection edits until explicit resume", () =>
        {
            using var original = new Rig(); original.Appoint(); original.Chatter.SetPaused(true, 0);
            using var loaded = new Rig(original.Gateway.Capture());
            loaded.Chatter.ApplyTargets(new[] { "unit:1" }, 100); loaded.Tick(9000);
            Require(loaded.Chatter.Paused && loaded.Requests == 0, "Loading or selection edits resumed a saved pause");
            loaded.Chatter.SetPaused(false, 9000); loaded.Tick(9001); loaded.Pump(9001);
            Require(loaded.Requests == 1 && loaded.Chatter.Enabled("unit:1") && !loaded.Chatter.Enabled("unit:2"), "Explicit resume lost the saved selection");
        });
        check("Saving during reappointment preserves the old person and never replays the interrupted request", () =>
        {
            using var original = new Rig(); original.Appoint(); original.Chatter.ApplyTargets(new[] { "unit:2" }, 0);
            var pending = new TaskCompletionSource<CrewGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
            original.Generate = _ => pending.Task; original.Chatter.RequestReappointment("unit:1", 0); original.Tick(1);
            Require(SpinWait.SpinUntil(() => original.Requests == 2, 3000), "Reappointment did not start");
            var saved = original.Gateway.Capture();
            using var loaded = new Rig(saved);
            Require(loaded.Chatter.Person("unit:1")!.Name == First.Name && !loaded.Chatter.Enabled("unit:1") &&
                !loaded.Chatter.HasPendingAppointments && !loaded.Chatter.Busy, "Saving committed or replayed an incomplete replacement");
            pending.SetResult(new CrewGeneration(new Dictionary<string, CrewAppointment> { ["unit:1"] = new(new CrewPerson("Replacement", "Captain", "Bold"), "Captain Replacement") }, Array.Empty<CrewSubmission>()));
            original.Pump(1);
            Require(loaded.Chatter.Person("unit:1")!.Name == First.Name && loaded.Requests == 0, "A late result changed the loaded person's identity");
        });
        check("Queued initial appointments are not saved and an unappointed load does not start automatic appointments", () =>
        {
            using var original = new Rig(); original.Chatter.RequestAppointments(0);
            Require(original.Gateway.Capture() == null, "A pending request acquired a saved appointment");
            using var loaded = new Rig(null, settings: new CrewSettings("api", 1, autoAppointments: true)); loaded.Tick(1000);
            Require(!loaded.Chatter.HasPendingAppointments && loaded.Chatter.Person("unit:1") == null && loaded.Requests == 0,
                "Loading fabricated or automatically retried the initial appointment request");
        });
        check("Crew data can be re-saved before feature activation and late prior-load cleanup cannot overwrite it", () =>
        {
            using var original = new Rig(); original.Appoint(); original.Chatter.ApplyTargets(new[] { "unit:2" }, 0);
            var bytes = original.Gateway.Capture()!;
            var data = new CrewSaveData(); var gateway = new AdvisorSaveData(crew: data);
            var session = new SaveDataSession(gateway.Begin, () => gateway.Capture, gateway.Restore); var scene = new object();
            session.Begin(scene); session.Stage(scene, SaveDataSection.Encode(bytes)); session.Complete(scene, true);
            Require(SaveDataSection.Decode(session.Capture())!.SequenceEqual(bytes), "Saving while Crew was disabled lost pending restoration");
            using var first = new CrewChatter(_ => { }); data.Attach(first, 1); first.Refresh(Targets());
            data.Detach(first); first.Dispose(); using var second = new CrewChatter(_ => { }); data.Attach(second, 10); second.Refresh(Targets());
            Require(!second.Enabled("unit:1") && second.Enabled("unit:2"), "Recreating UI lost participation");
            gateway.Begin(new object()); gateway.Restore(bytes); second.Suspend(); data.Detach(second);
            Require(gateway.Capture()!.SequenceEqual(bytes), "A retired session's late cleanup overwrote a newly restored save");
        });
        check("Saving OFF excludes Crew without discarding restored data or unrelated mission messages", () =>
        {
            using var original = new Rig(); original.Appoint();
            var saved = original.Gateway.Capture()!; var crew = new CrewSaveData(); var messages = new BattleMissionMessageHistory();
            var gateway = new AdvisorSaveData(missionMessages: messages, crew: crew); var scene = new object(); gateway.Begin(scene); gateway.Restore(saved);
            messages.Publish(scene, "Orders", "Protect the convoy", new DateTime(1985, 1, 1));
            Require(gateway.PrepareCapture(new AdvisorOptions(includeModSaveData: false)) == null, "Saving OFF still collected Crew");
            var resumed = JObject.Parse(Encoding.UTF8.GetString(gateway.PrepareCapture(new AdvisorOptions())!()!));
            Require(resumed["data"]?["crew"]?["people"]?.Count() == 2 && resumed["data"]?["mission_messages"]?["messages"]?.Count() == 1,
                "Saving OFF erased retained crew or mission messages");
            gateway.Begin(new object()); Require(gateway.Capture() == null, "A new mission inherited prior crew or messages");
        });
        check("Restored assignments wait for absent units and use the currently loaded formation", () =>
        {
            using var original = new Rig(); original.Appoint(); original.Chatter.ApplyTargets(new[] { "unit:1" }, 0);
            using var loaded = new Rig(original.Gateway.Capture(), targets: new[] { Target(2) }); loaded.Tick(100);
            Require(!loaded.Chatter.HasEnabledTargets && loaded.Chatter.CaptureSaveState().People.Single(p => p.UnitId == "unit:1").Person.Name == First.Name && loaded.Requests == 0,
                "An absent unit spoke or lost its saved identity");
            loaded.Chatter.Refresh(new[] { Target(1, false), Target(2) }); loaded.Tick(101);
            Require(loaded.Chatter.Enabled("unit:1") && !loaded.Chatter.HasPendingAppointments && loaded.Requests == 0,
                "A restored follower lost selection or requested a second identity");
            loaded.Chatter.Refresh(Targets()); Require(loaded.Chatter.Enabled("unit:1") && !loaded.Chatter.Enabled("unit:2"), "Formation changes reset saved selection");
        });
        check("Restored registration and portrait references keep independent identity and prevent duplicate assignment", () =>
        {
            using var original = new Rig(); original.Appoint(); using var loaded = new Rig(original.Gateway.Capture());
            var changedDefinition = new CrewCharacter(CharacterId, "Registry changed", "Captain", "Loud");
            Require(!loaded.Chatter.CanAssignCharacter("unit:2", changedDefinition) && loaded.Chatter.CanAssignCharacter("unit:1", changedDefinition) &&
                loaded.Chatter.Person("unit:1")!.Name == First.Name && loaded.Chatter.Person("unit:1")!.PortraitId == PortraitId,
                "Registration editing, absent libraries or restored assignment weakened identity/reference semantics");
            loaded.Chatter.RequestReappointment("unit:1", 100); loaded.Tick(101); loaded.Pump(101);
            Require(loaded.Chatter.RankAfterVotes == 3 && loaded.Chatter.RankBeforeVotes == 0 && !loaded.Chatter.RankBeforeName,
                "A later appointment replaced the saved rank vote totals");
        });
        check("Saved assignments follow native unit identity across changed map numbers and never bind to a reused map number", () =>
        {
            using var original = new Rig(); original.Appoint(); original.Chatter.ApplyTargets(new[] { "unit:1" }, 0);
            using var loaded = new Rig(original.Gateway.Capture(), targets: new[]
            {
                Target(1001, persistentId: "unit:2"), Target(1002, persistentId: "unit:1"), Target(1, persistentId: "unit:99")
            });
            Require(loaded.Chatter.Person("unit:1002")!.Name == First.Name && loaded.Chatter.Enabled("unit:1002") &&
                loaded.Chatter.Person("unit:1001")!.Name == Second.Name && !loaded.Chatter.Enabled("unit:1001") &&
                loaded.Chatter.Person("unit:1") == null, "Restoration matched a display number instead of the saved native unit");
            loaded.Chatter.Refresh(new[] { Target(1003, persistentId: "unit:1"), Target(1001, persistentId: "unit:2") });
            Require(loaded.Chatter.Enabled("unit:1003") && loaded.Chatter.Person("unit:1003")!.Name == First.Name,
                "A new map number for the same unit disabled its confirmed participation");
            loaded.Chatter.Refresh(new[] { Target(1003, persistentId: "unit:99") });
            Require(loaded.Chatter.Person("unit:1003") == null && !loaded.Chatter.Enabled("unit:1003"),
                "A reused map number inherited an unrelated person's identity or participation");
        });
        check("Malformed Crew archives fail without replacing already valid state", () =>
        {
            using var original = new Rig(); original.Appoint(); var expected = original.Save.Capture()!;
            var invalid = new List<JToken> { JValue.CreateNull(), new JArray(), new JObject(), expected.DeepClone() };
            invalid[^1]["version"] = 2;
            void Mutate(Action<JObject> edit) { var value = (JObject)expected.DeepClone(); edit(value); invalid.Add(value); }
            Mutate(v => ((JArray)v["people"]!).Add(v["people"]![0]!.DeepClone()));
            foreach (var id in new[] { "unit:-1", "unit:0", "unit:01", "unit:2147483648", "own_unit_1" }) Mutate(v => v["people"]![0]!["unit_id"] = id);
            Mutate(v => v["people"]![0]!["name"] = ""); Mutate(v => v["people"]![0]!["portrait_id"] = "invalid");
            Mutate(v => v["people"]![0]!["enabled"] = "true"); Mutate(v => v["rank_order"]!["after"] = -1);
            Mutate(v => v["rank_order"]!["before_name"] = true); Mutate(v => v.Remove("paused"));
            foreach (var value in invalid)
            { Reject(() => original.Save.Restore(value)); Require(JToken.DeepEquals(expected, original.Save.Capture()), "Malformed data overwrote confirmed people"); }
        });
        check("Retired Crew formats notify through existing compatibility handling and keep other features", () =>
        {
            using var original = new Rig(); original.Appoint(); var crew = new CrewSaveData(new SaveDataFormat(2, 2));
            var messages = new BattleMissionMessageHistory(); var gateway = new AdvisorSaveData(missionMessages: messages, crew: crew); var scene = new object();
            gateway.Begin(scene); messages.Publish(scene, "New orders", "Kept", new DateTime(1985, 1, 1)); gateway.Restore(original.Gateway.Capture());
            Require(gateway.UnsupportedData?.Part == "crew" && gateway.UnsupportedData.SourceVersion == 1 && crew.Capture() == null &&
                messages.Capture(scene).Count == 1, "An unsupported Crew format blocked play, survived silently or erased another feature");
            var bytes = gateway.Capture()!; var saved = JObject.Parse(Encoding.UTF8.GetString(bytes));
            Require(saved["data"]?["crew"] == null && saved["data"]?["mission_messages"] != null, "A retired Crew payload was written again");
        });
        check("An older save without Crew data loads as unappointed with no migration or notification", () =>
        {
            var bytes = Encoding.UTF8.GetBytes("{\"version\":1,\"data\":{\"unrelated\":7}}"); using var loaded = new Rig(bytes);
            loaded.Tick(1000);
            Require(loaded.Gateway.UnsupportedData == null && loaded.Chatter.Person("unit:1") == null && loaded.Requests == 0 &&
                (int?)loaded.Gateway.Data["unrelated"] == 7, "A missing optional feature was treated as obsolete or created an appointment");
        });
    }
    private static CrewTarget Target(int id, bool speaker = true, string? persistentId = null) => new("unit:" + id, "Unit " + id, "US", "captain", "vessel", "Destroyer", false, new[] { id }, speaker, persistentId);
    private static CrewTarget[] Targets() => new[] { Target(1), Target(2) };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is FormatException or CrewException or CrewPortraitLibraryException) { return; }
        throw new InvalidOperationException("Expected malformed saved Crew data to be rejected.");
    }
    private sealed class Rig : IDisposable
    {
        internal readonly CrewSaveData Save = new();
        internal readonly AdvisorSaveData Gateway;
        internal readonly List<Exception> Errors = new();
        internal readonly CrewChatter Chatter;
        internal readonly CrewSettings Settings;
        internal bool GamePaused;
        internal int Requests;
        internal JObject? LastInput;
        internal Func<CrewBatch, Task<CrewGeneration>> Generate;
        internal Rig(byte[]? saved = null, double now = 0, CrewSettings? settings = null, IReadOnlyList<CrewTarget>? targets = null)
        {
            Chatter = new CrewChatter(Errors.Add); Settings = settings ?? new CrewSettings("api", 1); Generate = Result; Gateway = new AdvisorSaveData(crew: Save);
            Gateway.Begin(new object()); Gateway.Restore(saved); Save.Attach(Chatter, now); Chatter.Refresh(targets ?? Targets());
        }
        internal void Appoint()
        { Chatter.RequestAppointments(-2); Tick(-1); Pump(-1); Require(Chatter.Person("unit:1") != null, "Appointment fixture did not finish: " + string.Join("; ", Errors)); }
        internal void Tick(double now) => Chatter.Tick(now, GamePaused, Settings, "ja", _ => new JObject(),
            _ => new JObject { ["captured_at_utc"] = "1985-01-01T00:00:00" }, (batch, _, token, usage) =>
            { Interlocked.Increment(ref Requests); LastInput = batch.Snapshot; usage(12); return Generate(batch); });
        private Task<CrewGeneration> Result(CrewBatch batch)
        {
            var data = batch.Snapshot;
            var appointments = ((JArray)data["appointments"]!).ToDictionary(t => (string)t["id"]!, t =>
            { var person = (string?)t["id"] == "unit:1" ? First : Second; return new CrewAppointment(person, person.Name + " " + person.Rank); });
            var remarks = ((JArray)data["speakers"]!).Take(1).Select(t =>
            { var id = (string)t["id"]!; return new CrewSubmission(id, id == "unit:1" ? First : Second, "Steady as she goes."); });
            return Task.FromResult(new CrewGeneration(appointments, remarks));
        }
        internal void Pump(double now) => Require(SpinWait.SpinUntil(() => { Tick(now); return !Chatter.Busy; }, 3000), "Crew fixture did not complete");
        public void Dispose() { Save.Detach(Chatter); Chatter.Dispose(); }
    }
}
