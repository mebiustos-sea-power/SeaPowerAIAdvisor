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
            Require(read.Name == "" && read.PortraitId == portrait && !read.AllowDuplicates && read.AiAppointment == "normal" && read.Gender == "" && read.AssignmentTarget == "any", "Optional defaults or image link were lost");
            var full = new CrewCharacter(blank.Id, "Name", "Rank", new string('a', 255), "female", "Fictional nation", portrait, "priority", true, "aircraft");
            library.Save(full); var reopened = new CrewCharacterLibrary(directory);
            Require(reopened.Characters.Count == 1 && reopened.Find(full.Id)!.Json().ToString() == full.Json().ToString(), "Editing replaced the identity or lost fields");
            library.Delete(full.Id);
            Require(new CrewCharacterLibrary(directory).Characters.Count == 0 && portraits.Contains(portrait), "Deleting a character removed its image or did not persist");
            Throws<CrewException>(() => new CrewCharacter(Id(), personality: new string('a', 256)));
        }));
        check("Automatic assignment targets include submarines with ships and helicopters with aircraft and survive save edits", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var id = Id();
            var kinds = new[] { "vessel", "submarine", "aircraft", "helicopter" };
            var scopes = new[] {
                (Selected: "any", Eligible: new[] { "vessel", "submarine", "aircraft", "helicopter" }),
                (Selected: "ship", Eligible: new[] { "vessel", "submarine" }),
                (Selected: "aircraft", Eligible: new[] { "aircraft", "helicopter" }) };
            foreach (var scope in scopes)
            {
                library.Save(new CrewCharacter(id, assignmentTarget: scope.Selected));
                var saved = new CrewCharacterLibrary(directory).Find(id)!;
                Require(saved.AssignmentTarget == scope.Selected && library.Characters.Count == 1, "Editing an assignment target lost the selection or changed identity");
                foreach (var kind in kinds)
                    Require(saved.MatchesAssignmentTarget(kind) == scope.Eligible.Contains(kind), "An automatic candidate was assigned to the wrong unit category");
            }
            foreach (var invalid in new[] { "", "navy", "air_force" })
                Throws<CrewException>(() => new CrewCharacter(Id(), assignmentTarget: invalid));
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue(true), new JValue("navy") })
            {
                var value = library.Find(id)!.Json(); value["assignment_target"] = invalid;
                File.WriteAllText(Path.Combine(directory, "Crew", "characters.json"), new JObject { ["format_version"] = 1, ["characters"] = new JArray(value) }.ToString());
                Throws<CrewException>(() => new CrewCharacterLibrary(directory));
            }
        }));
        check("Corrupt character libraries and failed saves remain visible without publishing draft changes", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var original = new CrewCharacter(Id(), "Original"); library.Save(original);
            var path = Path.Combine(directory, "Crew", "characters.json");
            File.Delete(path); Directory.CreateDirectory(path);
            Throws<CrewException>(() => library.Save(new CrewCharacter(original.Id, "Unsaved")));
            Require(library.Find(original.Id)!.Name == "Original" && library.Version == 1, "Failed write published a draft");
            Throws<CrewException>(() => new CrewCharacterLibrary(directory)); Directory.Delete(path);
            var previousFormat = original.Json(); previousFormat.Remove("assignment_target");
            foreach (var document in new[] {
                new JObject { ["format_version"] = 1, ["characters"] = new JArray(previousFormat) },
                new JObject { ["format_version"] = 1, ["characters"] = new JArray(original.Json(), original.Json()) },
                new JObject { ["format_version"] = 2, ["characters"] = new JArray(original.Json()) },
                new JObject { ["format_version"] = 1, ["characters"] = new JArray(new JObject { ["id"] = original.Id }) } })
            { File.WriteAllText(path, document.ToString()); Throws<CrewException>(() => new CrewCharacterLibrary(directory)); }
        }));
        check("Complete registered characters appoint without generation and replacements preserve participation and registration identity", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var first = Full(); library.Save(first);
            var second = new CrewCharacter(Id(), "Registered pilot", "Captain", "Calm", assignmentTarget: "aircraft"); library.Save(second);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", first.Id, 0);
            Require(rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == first.Id && !rig.Chatter.HasPendingAppointments, "Initial local appointment did not complete");
            rig.Tick(0); Require(rig.Requests == 0, "Complete appointment invoked AI while paused");
            Require(!second.MatchesAssignmentTarget("vessel") && rig.Chatter.CanAssignCharacter("unit:1", second), "An automatic assignment target blocked explicit manual selection");
            rig.Chatter.ApplyTargets(Array.Empty<string>(), 0); rig.Chatter.AssignCharacter("unit:1", second.Id, 0);
            Require(!rig.Chatter.Enabled("unit:1") && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == second.Id, "Replacement changed participation or source identity");
            var current = rig.Chatter.Person("unit:1")!; var draft = new CrewPersonDraft(current) { Name = "Edited locally" };
            rig.Chatter.UpdatePerson("unit:1", current, draft.Build());
            Require(library.Find(second.Id)!.Name == second.Name && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == second.Id, "Person edits changed registration or severed its identity");
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
            var selected = new CrewCharacter(Id(), "登録名 그대로", personality: new string('あ', 255), gender: "female", nationality: "Japan", portraitId: image, aiAppointment: "excluded", assignmentTarget: "aircraft");
            library.Save(selected); var unrelated = Full(); library.Save(unrelated);
            using var rig = new Rig(library); rig.Chatter.AssignCharacter("unit:1", selected.Id, 0);
            Throws<CrewException>(() => rig.Chatter.AssignCharacter("unit:2", selected.Id, 0));
            rig.Tick(1); var batch = rig.Take(); var input = batch.Input(""); var json = input.Data.ToString();
            Require(json.Contains(selected.Name) && json.Contains("female") && json.Contains("Japan") && json.Contains(selected.Id) && !json.Contains(image) && !json.Contains(unrelated.Id) && !json.Contains("assignment_target"), "Wrong metadata or unselected characters were sent");
            var result = batch.Read(Reply("unit:1", "Changed by AI", "Commander", "Shortened", selected.Id));
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
            var reply = Reply("unit:1", "First", "Rank", "Calm", character.Id);
            ((JArray)reply.Calls[0].Arguments["appointments"]!).Add(Row("unit:2", "Second", "Rank", "Bold", character.Id));
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
            rig.Complete(batch.Read(Reply("unit:1", "Stale", "Rank", "Calm", next.Id)));
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
            rig.Chatter.Refresh(new[] { Target(1), Target(3) }); rig.Complete(batch.Read(Reply("unit:2", "Removed", "Rank", "Calm", character.Id))); rig.Pump(1);
            Require(rig.Chatter.Person("unit:2") == null, "A removed target accepted a late appointment");
            rig.Chatter.AssignCharacter("unit:3", character.Id, 2);
            Require(rig.Chatter.AppointmentRequested("unit:3"), "A removed pending assignment blocked reuse");
        }));
        check("Bulk appointments offer each eligible registration once with unit scopes and preserve registered fields in one generation", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory);
            var occupied = Full(); var reserved = new CrewCharacter(Id(), "Fixed");
            var excluded = new CrewCharacter(Id(), aiAppointment: "excluded");
            var ship = new CrewCharacter(Id(), "登録名 그대로", personality: new string('あ', 255), gender: "female", nationality: "Japan", aiAppointment: "priority", assignmentTarget: "ship");
            var air = new CrewCharacter(Id(), "Pilot", "Captain", "Bold", assignmentTarget: "aircraft");
            var any = new CrewCharacter(Id(), portraitId: "11111111111111111111111111111111.png");
            foreach (var c in new[] { occupied, reserved, excluded, ship, air, any }) library.Save(c);
            using var rig = new Rig(library);
            rig.Chatter.Refresh(new[] { Target(1), Target(2, "submarine"), Target(3, "aircraft"), Target(4, "helicopter"), Target(5) });
            rig.Chatter.AssignCharacter("unit:5", occupied.Id, 0); rig.Chatter.AssignCharacter("unit:1", reserved.Id, 0);
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var batch = rig.Take(); var data = batch.Input("").Data;
            var offered = ((JArray)data["registered_characters"]!).Cast<JObject>().ToArray();
            Require(offered.Length == 3 && (string?)offered[0]["id"] == ship.Id && (bool?)offered[0]["priority"] == true,
                "Priority or occupied, pending and excluded candidate filtering failed");
            string[] Units(string id) => offered.Single(c => (string?)c["id"] == id)["unit_ids"]!.Values<string>().OrderBy(v => v).ToArray()!;
            Require(Units(ship.Id).SequenceEqual(new[] { "unit:2" }) && Units(air.Id).SequenceEqual(new[] { "unit:3", "unit:4" }) &&
                Units(any.Id).SequenceEqual(new[] { "unit:2", "unit:3", "unit:4" }), "An automatic candidate escaped its unit scope");
            Require((string?)offered[0]["gender"] == "female" && (string?)offered[0]["nationality"] == "Japan" &&
                !data.ToString().Contains(any.PortraitId!) && (string?)data["appointments"]![0]!["fixed_character_id"] == reserved.Id,
                "Selection hints or fixed selection were missing, or image metadata was sent to AI");
            rig.Complete(batch.Read(ReplyRows(Row("unit:1", "Ignored", "Commander", "Calm", reserved.Id),
                Row("unit:2", "Ignored", "Lieutenant", "Ignored", ship.Id), Row("unit:3", "Ignored", "Ignored", "Ignored", air.Id),
                Row("unit:4", "Generated pilot", "Captain", "Cheerful", any.Id)))); rig.Pump(1);
            Require(rig.Requests == 1 && !rig.Chatter.HasPendingAppointments && rig.Chatter.Targets.All(t => rig.Chatter.Enabled(t.Id)), "Selection required another request or failed to enroll appointments");
            var person = rig.Chatter.Person("unit:2")!;
            Require(person.Name == ship.Name && person.Personality == ship.Personality && person.Rank == "Lieutenant" && person.RegisteredCharacterId == ship.Id &&
                rig.Chatter.Person("unit:4")!.PortraitId == any.PortraitId && library.Find(ship.Id)!.Rank == "", "Registered fields or local image links were lost, or completion changed the template");
            var status = new CrewLocalApi(rig.Chatter, () => 1, () => { }).Status();
            Require((string?)status["targets"]!.Single(t => (string?)t["id"] == "unit:2")["registered_character_id"] == ship.Id && !person.Json().ToString().Contains(ship.Id),
                "The dev status lost source identity or speech input gained registration metadata");
        }));
        check("Automatic selection rejects unknown, wrong-unit and duplicate registrations but may choose a normal candidate or a new person", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var priority = new CrewCharacter(Id(), aiAppointment: "priority", assignmentTarget: "ship");
            var normal = new CrewCharacter(Id()); library.Save(priority); library.Save(normal);
            using var rig = new Rig(library); rig.Chatter.Refresh(new[] { Target(1), Target(2), Target(3, "aircraft") });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var batch = rig.Take();
            AiReply Choices(string first, string second, string third) => ReplyRows(Row("unit:1", "One", "Rank", "Calm", first),
                Row("unit:2", "Two", "Rank", "Bold", second), Row("unit:3", "Three", "Rank", "Calm", third));
            Throws<CrewException>(() => batch.Read(Choices(Id(), "new", "new")));
            Throws<CrewException>(() => batch.Read(Choices("new", "new", priority.Id)));
            Throws<CrewException>(() => batch.Read(Choices(normal.Id, normal.Id, "new")));
            var missing = Choices("new", "new", "new"); ((JObject)missing.Calls[0].Arguments["appointments"]![0]!).Remove("character_id");
            Throws<CrewException>(() => batch.Read(missing));
            Require(rig.Chatter.Person("unit:1") == null, "Invalid responses published partial assignments");
            rig.Complete(batch.Read(Choices("new", normal.Id, "new"))); rig.Pump(1);
            Require(!rig.Chatter.Paused && rig.Chatter.Person("unit:1")!.RegisteredCharacterId == null && rig.Chatter.Person("unit:2")!.RegisteredCharacterId == normal.Id &&
                rig.Chatter.Person("unit:3")!.RegisteredCharacterId == null, "Priority was forced or choosing a new person failed");
        }));
        check("Duplicate-enabled automatic registrations complete independently and retain their captured definitions", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var original = new CrewCharacter(Id(), rank: "Registered rank", allowDuplicates: true); library.Save(original);
            using var rig = new Rig(library); rig.Chatter.Refresh(new[] { Target(1), Target(2) });
            rig.Chatter.RequestAppointments(0); rig.Tick(1); var batch = rig.Take();
            library.Save(Full(original.Id)); library.Delete(original.Id);
            rig.Complete(batch.Read(ReplyRows(Row("unit:2", "Second", "Ignored", "Bold", original.Id), Row("unit:1", "First", "Ignored", "Calm", original.Id)))); rig.Pump(1);
            Require(rig.Chatter.Person("unit:1")!.Name == "First" && rig.Chatter.Person("unit:2")!.Name == "Second" &&
                rig.Chatter.Person("unit:1")!.Personality == "Calm" && rig.Chatter.Person("unit:2")!.Personality == "Bold" &&
                rig.Chatter.Targets.All(t => rig.Chatter.Person(t.Id)!.RegisteredCharacterId == original.Id && rig.Chatter.Person(t.Id)!.Rank == original.Rank),
                "Duplicate completions were shared or overwritten by a later library edit");
        }));
        check("Later appointment batches exclude newly occupied registrations and reserve fixed assignments outside the first batch", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var first = new CrewCharacter(Id()); var fixedLater = new CrewCharacter(Id()); var remaining = new CrewCharacter(Id());
            foreach (var c in new[] { first, fixedLater, remaining }) library.Save(c);
            using var rig = new Rig(library); rig.Chatter.Refresh(Enumerable.Range(1, CrewBatch.MaximumAppointments + 2).Select(n => Target(n)).ToArray());
            rig.Chatter.AssignCharacter("unit:34", fixedLater.Id, 0); rig.Chatter.RequestAppointments(0); rig.Tick(1); var initial = rig.Take();
            Require(initial.Input("").Data["registered_characters"]!.All(c => (string?)c["id"] != fixedLater.Id), "A fixed pending assignment was offered in an earlier batch");
            rig.Complete(initial.Read(ReplyRows(Enumerable.Range(1, CrewBatch.MaximumAppointments)
                .Select(n => Row("unit:" + n, "Person " + n, "Rank", "Calm", n == 1 ? first.Id : "new")).ToArray()))); rig.Pump(1);
            rig.Tick(2); var next = rig.Take(); var offered = next.Input("").Data["registered_characters"]!;
            Require(offered.Count() == 1 && (string?)offered[0]!["id"] == remaining.Id, "A used or pending nonduplicate registration was offered again");
            var wrongFixed = ReplyRows(Row("unit:33", "Next", "Rank", "Calm", remaining.Id), Row("unit:34", "Wrong", "Rank", "Calm"));
            Throws<CrewException>(() => next.Read(wrongFixed));
            rig.Complete(next.Read(ReplyRows(Row("unit:33", "Next", "Rank", "Calm", remaining.Id), Row("unit:34", "Fixed", "Rank", "Calm", fixedLater.Id)))); rig.Pump(2);
            Require(rig.Requests == 2 && !rig.Chatter.HasUnappointedTargets && rig.Chatter.Person("unit:34")!.RegisteredCharacterId == fixedLater.Id,
                "Batch continuation lost a fixed request or required another selection round");
        }));
        check("Additional-unit automatic appointments use registrations without changing existing OFF choices and release removed-unit assignments", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var seed = new CrewCharacter(Id(), "Seed", "Rank", "Calm", aiAppointment: "excluded"); var added = Full();
            library.Save(seed); library.Save(added);
            using var rig = new Rig(library); rig.Chatter.Refresh(new[] { Target(1) }); rig.Settings = new CrewSettings("test", 1, autoAppointments: true);
            rig.Chatter.AssignCharacter("unit:1", seed.Id, 0); var existing = rig.Chatter.Person("unit:1"); rig.Chatter.ApplyTargets(Array.Empty<string>(), 0);
            rig.Tick(0); rig.GamePaused = false; rig.Tick(1); rig.Chatter.Refresh(new[] { Target(1), Target(2) }); rig.Tick(60);
            Require(rig.Requests == 0, "Automatic selection bypassed the configured countdown");
            rig.Tick(61); var batch = rig.Take();
            Require(batch.Input("").Data["registered_characters"] is JArray { Count: 1 }, "Additional units did not receive the registry candidate");
            rig.Complete(batch.Read(Reply("unit:2", "Ignored", "Ignored", "Ignored", added.Id))); rig.Pump(61);
            Require(ReferenceEquals(existing, rig.Chatter.Person("unit:1")) && !rig.Chatter.Enabled("unit:1") && rig.Chatter.Enabled("unit:2") &&
                rig.Chatter.Person("unit:2")!.RegisteredCharacterId == added.Id, "Automatic selection changed existing participation or failed to assign the registration");
            rig.Chatter.ApplyTargets(Array.Empty<string>(), 61); rig.Chatter.Refresh(new[] { Target(1), Target(3) }); rig.Tick(121); var reused = rig.Take();
            Require(reused.Input("").Data["registered_characters"]!.Any(c => (string?)c["id"] == added.Id), "A disappeared unit's retained person blocked registry reuse");
            rig.Complete(reused.Read(Reply("unit:3", "Ignored", "Ignored", "Ignored", added.Id))); rig.Pump(121);
            Require(rig.Chatter.Person("unit:3")!.RegisteredCharacterId == added.Id, "A released registration could not be reused automatically");
        }));
        check("Individual AI replacement generates a new person and speech requests omit the registration library", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var current = Full(); var candidate = Full(); library.Save(current); library.Save(candidate);
            using var rig = new Rig(library); rig.Chatter.Refresh(new[] { Target(1) }); rig.Chatter.AssignCharacter("unit:1", current.Id, 0);
            rig.Chatter.RequestReappointment("unit:1", 0); rig.Tick(1); var replacement = rig.Take();
            Require(replacement.Input("").Data["registered_characters"] == null, "Individual AI replacement became automatic registry selection");
            Throws<CrewException>(() => replacement.Read(Reply("unit:1", "Other", "Rank", "Calm", candidate.Id)));
            rig.Complete(replacement.Read(Reply("unit:1", "New person", "Rank", "Calm"))); rig.Pump(1);
            rig.GamePaused = false; rig.Tick(2); rig.Tick(3); var speech = rig.Take(); var data = speech.Input("").Data;
            Require(rig.Chatter.Person("unit:1")!.RegisteredCharacterId == null && data["registered_characters"] == null && data["appointment_context"] == null &&
                data["appointments"] is JArray { Count: 0 }, "Individual replacement retained the registration or routine speech resent appointment metadata");
            rig.Complete(speech.Read(ReplyRows())); rig.Pump(3);
        }));
    }
    private static CrewCharacter Full(string? id = null) => new(id ?? Id(), "Registered", "Commander", "Calm");
    private static CrewTarget Target(int n, string kind = "vessel") => new("unit:" + n, "Unit " + n, "US", kind is "aircraft" or "helicopter" ? "pilot" : "captain", kind, "Class", false, new[] { n }, true);
    private static JObject Row(string id, string name, string rank, string personality, string characterId = "new") => new()
        { ["id"] = id, ["character_id"] = characterId, ["person_name"] = name, ["rank"] = rank, ["name_with_rank"] = rank + " " + name, ["personality"] = personality };
    private static AiReply Reply(string id, string name, string rank, string personality, string characterId = "new") => new("", new[] {
        new AiToolCall("test", CrewBatch.ToolName, new JObject { ["appointments"] = new JArray(Row(id, name, rank, personality, characterId)), ["remarks"] = new JArray() }) }, null);
    private static AiReply ReplyRows(params JObject[] appointments) => new("", new[] {
        new AiToolCall("test", CrewBatch.ToolName, new JObject { ["appointments"] = new JArray(appointments), ["remarks"] = new JArray() }) }, null);
    private sealed class Rig : IDisposable
    {
        internal readonly CrewChatter Chatter;
        private readonly ConcurrentQueue<CrewBatch> calls = new();
        private TaskCompletionSource<CrewGeneration>? completion;
        internal int Requests;
        internal bool GamePaused = true;
        internal CrewSettings Settings = new("test");
        internal Rig(CrewCharacterLibrary library) { Chatter = new CrewChatter(_ => { }, characters: library); Chatter.Refresh(new[] { Target(1), Target(2), Target(3) }); }
        internal void Tick(double now) => Chatter.Tick(now, GamePaused, Settings, "ja", _ => new JObject(), _ => new JObject(),
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
