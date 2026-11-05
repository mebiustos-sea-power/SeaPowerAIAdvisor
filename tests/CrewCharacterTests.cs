using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class CrewCharacterTests
{
    private static string Id() => Guid.NewGuid().ToString("N");
    internal static void Run(Action<string, Action> check)
    {
        check("Character registrations survive reopening with optional fields, stable IDs and independent image ownership", () => Temp(directory =>
        {
            var portraits = new CrewPortraitLibrary(directory); var portrait = portraits.Add(CrewTests.PortraitHeader(256, 256));
            var library = new CrewCharacterLibrary(directory);
            Require(library.Characters.Count == 0 && !File.Exists(Path.Combine(directory, "Crew", "characters.json")), "Opening created a registry");
            var blank = new CrewCharacter(Id(), portraitId: portrait); library.Save(blank);
            var read = new CrewCharacterLibrary(directory).Find(blank.Id)!;
            Require(read.Name == "" && read.PortraitId == portrait && !read.AllowDuplicates && read.AiAppointment == "normal" && read.Gender == "", "Optional defaults or image link were lost");
            var full = new CrewCharacter(blank.Id, "Name", "Rank", new string('a', 255), "female", "Fictional nation", portrait, "priority", true);
            library.Save(full); var reopened = new CrewCharacterLibrary(directory);
            Require(reopened.Characters.Count == 1 && reopened.Find(full.Id)!.Json().ToString() == full.Json().ToString(), "Editing replaced the identity or lost fields");
            library.Delete(full.Id);
            Require(new CrewCharacterLibrary(directory).Characters.Count == 0 && portraits.Contains(portrait), "Deleting a character removed its image or did not persist");
            Throws<CrewException>(() => new CrewCharacter(Id(), personality: new string('a', 256)));
        }));
        check("Corrupt character libraries and failed saves remain visible without publishing draft changes", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var original = new CrewCharacter(Id(), "Original"); library.Save(original);
            var path = Path.Combine(directory, "Crew", "characters.json");
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => library.Save(new CrewCharacter(original.Id, "Unsaved")));
            Require(library.Find(original.Id)!.Name == "Original" && library.Version == 1, "Failed write published a draft");
            Throws<CrewException>(() => new CrewCharacterLibrary(directory)); Directory.Delete(path);
            foreach (var document in new[] {
                new JObject { ["format_version"] = 1, ["characters"] = new JArray(original.Json(), original.Json()) },
                new JObject { ["format_version"] = 2, ["characters"] = new JArray(original.Json()) },
                new JObject { ["format_version"] = 1, ["characters"] = new JArray(new JObject { ["id"] = original.Id }) } })
            { File.WriteAllText(path, document.ToString()); Throws<CrewException>(() => new CrewCharacterLibrary(directory)); }
        }));
        check("Complete registered characters appoint without generation and replacements preserve participation and registration identity", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var first = Full(); library.Save(first); var second = Full(); library.Save(second);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", first.Id, 0);
            Require(rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == first.Id && !rig.Chatter.HasPendingAppointments, "Initial local appointment did not complete");
            rig.Tick(0); Require(rig.Requests == 0, "Complete appointment invoked AI while paused");
            rig.Chatter.ApplyTargets(Array.Empty<string>(), 0); rig.Chatter.AssignCharacter("unit:1", second.Id, 0);
            Require(!rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == second.Id, "Replacement changed participation or source identity");
            var current = rig.Chatter.Person("unit:1")!; var draft = new CrewPersonDraft(current) { Name = "Edited locally" };
            rig.Chatter.UpdatePerson("unit:1", current, draft.Build());
            Require(library.Find(second.Id)!.Name == "Registered" && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == second.Id, "Person edits changed registration or severed its identity");
            Require(!current.Json().ToString().Contains(second.Id), "A registration ID leaked into speech input");
            library.Delete(second.Id); Require(rig.Chatter.Person("unit:1")!.Name == "Edited locally", "Registration deletion removed the appointed person");
        }));
        check("Duplicate appointment policy uses current assignments, preserves existing duplicates and frees replaced or removed units", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var character = Full(); library.Save(character);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", character.Id, 0);
            Throws<CrewException>(() => rig.Chatter.AssignCharacter("unit:2", character.Id, 0));
            library.Save(new CrewCharacter(character.Id, character.Name, character.Rank, character.Personality, allowDuplicates: true));
            rig.Chatter.AssignCharacter("unit:2", character.Id, 0); library.Save(character);
            Require(rig.Chatter.Person("unit:1")!.RegisteredCharacterId == character.Id && rig.Chatter.Person("unit:2")!.RegisteredCharacterId == character.Id, "Turning duplication off evicted existing people");
            rig.Chatter.AssignCharacter("unit:1", character.Id, 0);
            Throws<CrewException>(() => rig.Chatter.AssignCharacter("unit:3", character.Id, 0));
            var other = Full(); library.Save(other); rig.Chatter.AssignCharacter("unit:1", other.Id, 0);
            rig.Chatter.Refresh(new[] { Target(1), Target(3) }); rig.Chatter.AssignCharacter("unit:3", character.Id, 0);
            Require(rig.Chatter.Person("unit:3")!.RegisteredCharacterId == character.Id, "Removed or replaced assignments blocked reuse");
        }));
        check("Pending appointments reserve nonduplicate characters and send only selected generation hints", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory);
            var image = "11111111111111111111111111111111.png";
            var selected = new CrewCharacter(Id(), "登録名 그대로", personality: new string('あ', 255), gender: "female", nationality: "Japan", portraitId: image, aiAppointment: "excluded");
            library.Save(selected); var unrelated = Full(); library.Save(unrelated);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", selected.Id, 0);
            Throws<CrewException>(() => rig.Chatter.AssignCharacter("unit:2", selected.Id, 0));
            rig.Tick(1); var batch = rig.Take(); var input = batch.Input(""); var json = input.Data.ToString();
            Require(json.Contains(selected.Name) && json.Contains("female") && json.Contains("Japan") && !json.Contains(selected.Id) && !json.Contains(image) && !json.Contains(unrelated.Id), "Wrong metadata or unselected characters were sent");
            var result = batch.Read(Reply("unit:1", "Changed by AI", "Commander", "Shortened"));
            rig.Complete(result); rig.Pump(1);
            var person = rig.Chatter.Person("unit:1")!;
            Require(person.Name == selected.Name && person.Rank == "Commander" && person.Personality == selected.Personality && person.PortraitId == image && person.RegisteredCharacterId == selected.Id,
                "Explicit values were translated, truncated or lost while filling the missing rank");
            Require(library.Find(selected.Id)!.Rank == "", "AI completion wrote back to the registration");
        }));
        check("Each duplicate appointment has independent completion values and registration edits cannot rewrite pending snapshots", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var character = new CrewCharacter(Id(), allowDuplicates: true); library.Save(character);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", character.Id, 0); rig.Chatter.AssignCharacter("unit:2", character.Id, 0);
            rig.Tick(1); var batch = rig.Take(); library.Save(Full(character.Id)); library.Delete(character.Id);
            var reply = Reply("unit:1", "First", "Rank", "Calm");
            ((JArray)reply.Calls[0].Arguments["appointments"]!).Add(Row("unit:2", "Second", "Rank", "Bold"));
            rig.Complete(batch.Read(reply)); rig.Pump(1);
            Require(rig.Chatter.Person("unit:1")!.Name == "First" && rig.Chatter.Person("unit:2")!.Name == "Second" &&
                rig.Chatter.Person("unit:1")!.RegisteredCharacterId == character.Id && rig.Chatter.Person("unit:2")!.RegisteredCharacterId == character.Id,
                "Independent completions were replaced by a changed or deleted template");
        }));
        check("Failed or superseded registered replacements retain the old person and participation", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var old = Full(); var next = new CrewCharacter(Id()); library.Save(old); library.Save(next);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", old.Id, 0); rig.Chatter.ApplyTargets(Array.Empty<string>(), 0);
            var prior = rig.Chatter.Person("unit:1")!; rig.Chatter.AssignCharacter("unit:1", next.Id, 0); rig.Tick(1); _ = rig.Take();
            rig.Fail(); rig.Pump(1);
            Require(rig.Chatter.Paused && ReferenceEquals(prior, rig.Chatter.Person("unit:1")) && !rig.Chatter.Enabled("unit:1"), "Failure changed the current person or participation");
            rig.Chatter.SetPaused(false, 2); rig.Tick(3); var batch = rig.Take();
            var draft = new CrewPersonDraft(prior) { Name = "Local edit" }; rig.Chatter.SetPaused(true, 3); rig.Chatter.UpdatePerson("unit:1", prior, draft.Build());
            rig.Complete(batch.Read(Reply("unit:1", "Stale", "Rank", "Calm")));
            rig.Chatter.SetPaused(false, 4); rig.Tick(4);
            Require(rig.Chatter.Person("unit:1")!.Name == "Local edit" && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == old.Id, "Stale replacement overwrote an edit or changed the source ID");
        }));
        check("Character edits and replacements preserve historical people without reserving registrations from history", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var first = Full(); var second = Full(); library.Save(first); library.Save(second);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", first.Id, 0); rig.Tick(0);
            rig.GamePaused = false; rig.Tick(1); rig.Tick(2); var batch = rig.Take();
            var speech = new AiReply("", new[] { new AiToolCall("test", CrewBatch.ToolName, new JObject {
                ["appointments"] = new JArray(), ["remarks"] = new JArray(new JObject { ["id"] = "unit:1", ["text"] = "Keeping watch." }) }) }, null);
            rig.Complete(batch.Read(speech)); rig.Pump(2);
            var remark = rig.Chatter.History.Single(); var original = rig.Chatter.Person("unit:1")!;
            Throws<CrewException>(() => rig.Chatter.UpdatePerson("unit:1", original, new CrewPerson("Edited", "Rank", "Calm")));
            library.Save(new CrewCharacter(first.Id, "Library edit")); rig.Chatter.AssignCharacter("unit:1", second.Id, 3);
            Require(ReferenceEquals(remark.Submission.Person, original) && remark.Submission.Person.Name == first.Name &&
                remark.Submission.Person.RegisteredCharacterId == first.Id && remark.Submission.Text == "Keeping watch.", "Registration edits rewrote history");
            Require(rig.Chatter.NextNotification(3) == null && rig.Chatter.CanAssignCharacter("unit:2", first), "An old notification survived replacement or history reserved its character");
        }));
        check("Suspending Crew releases queued character reservations and removed units reject their old completions", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var character = new CrewCharacter(Id()); library.Save(character);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", character.Id, 0); rig.Chatter.Suspend();
            rig.Chatter.AssignCharacter("unit:2", character.Id, 0); rig.Tick(1); var batch = rig.Take();
            rig.Chatter.Refresh(new[] { Target(1), Target(3) }); rig.Complete(batch.Read(Reply("unit:2", "Removed", "Rank", "Calm"))); rig.Pump(1);
            Require(rig.Chatter.Person("unit:2") == null, "A removed target accepted a late appointment");
            rig.Chatter.AssignCharacter("unit:3", character.Id, 2);
            Require(rig.Chatter.AppointmentRequested("unit:3"), "A removed pending assignment blocked reuse");
        }));
    }
    private static CrewCharacter Full(string? id = null) => new(id ?? Id(), "Registered", "Commander", "Calm");
    private static CrewTarget Target(int n) => new("unit:" + n, "Ship " + n, "US", "captain", "ship", "Class", false, new[] { n }, true);
    private static JObject Row(string id, string name, string rank, string personality) => new()
        { ["id"] = id, ["person_name"] = name, ["rank"] = rank, ["name_with_rank"] = rank + " " + name, ["personality"] = personality };
    private static AiReply Reply(string id, string name, string rank, string personality) => new("", new[] {
        new AiToolCall("test", CrewBatch.ToolName, new JObject { ["appointments"] = new JArray(Row(id, name, rank, personality)), ["remarks"] = new JArray() }) }, null);
    private sealed class Rig : IDisposable
    {
        internal readonly CrewChatter Chatter;
        private readonly ConcurrentQueue<CrewBatch> calls = new();
        private TaskCompletionSource<CrewGeneration>? completion;
        internal int Requests;
        internal bool GamePaused = true;
        internal Rig(CrewCharacterLibrary library) { Chatter = new CrewChatter(_ => { }, characters: library); Chatter.Refresh(new[] { Target(1), Target(2), Target(3) }); }
        internal void Tick(double now) => Chatter.Tick(now, GamePaused, new CrewSettings("test"), "ja", _ => new JObject(), _ => new JObject(),
            (batch, _, _, _) => { Interlocked.Increment(ref Requests); var next = new TaskCompletionSource<CrewGeneration>(); completion = next; calls.Enqueue(batch); return next.Task; });
        internal CrewBatch Take() { CrewBatch? batch = null; Require(SpinWait.SpinUntil(() => calls.TryDequeue(out batch), 3000), "Generation did not start"); return batch!; }
        internal void Complete(CrewGeneration value) => completion!.SetResult(value);
        internal void Fail() => completion!.SetException(new CrewException("invalid_response"));
        internal void Pump(double now) => Require(SpinWait.SpinUntil(() => { Tick(now); return !Chatter.Busy; }, 3000), "Generation did not complete");
        public void Dispose() => Chatter.Dispose();
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Temp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-characters-" + Id()); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
