using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class CrewAppointmentTests
{
    private const string CharacterId = "11111111111111111111111111111111";
    internal static void Run(Action<string, Action> check)
    {
        check("Automatic appointment evaluates fit before selection and isolates fixed personal identity from foreign-unit context", () =>
        {
            var character = new CrewCharacter(CharacterId, personality: new string('あ', 255), gender: "female", nationality: "United States");
            var batch = Batch(character); var calls = 0;
            var result = batch.GenerateAsync("CONNECTION_PROMPT", input =>
            {
                calls++; var tool = Name(input); Require(input.SystemInstruction.Contains("CONNECTION_PROMPT", StringComparison.Ordinal), "A stage lost the selected connection's prompt");
                if (tool == "assess_crew_fit") return Task.FromResult(Reply(tool, Fit("unit:2")));
                if (tool == "select_crew")
                {
                    Require(input.Data["registered_characters"]![0]!["unit_ids"]!.Values<string>().SequenceEqual(new[] { "unit:2" }), "Selection ignored the fit assessment");
                    return Task.FromResult(Reply(tool, Selections("new", CharacterId)));
                }
                Require(tool == "submit_crew_people" && input.Data.Properties().Select(p => p.Name).ToHashSet().SetEquals(new[] { "reply_language", "year", "people" }), "Completion received mission or unit context");
                var person = input.Data["people"]!.Single(p => (string?)p["id"] == "unit:2");
                Require((string?)person["nationality"] == "United States" && (string?)person["gender"] == "female" && (string?)person["personality"] == character.Personality &&
                    !input.Data.ToString().Contains("Classified fixture briefing", StringComparison.Ordinal) && person["nation"] == null && person["role"] == null,
                    "The unit's nation or reply language overwrote personal identity or exposed appointment context");
                return Task.FromResult(Reply(tool, People()));
            }, CancellationToken.None).GetAwaiter().GetResult();
            Require(calls == 3 && result.Remarks.Count == 0 && result.Appointments["unit:2"].Person.RegisteredCharacterId == CharacterId &&
                result.Appointments["unit:2"].Person.Personality == character.Personality && result.Appointments["unit:1"].Person.RegisteredCharacterId == null,
                "The job mixed speech with appointment, lost fixed values or hard-filtered a coherent foreign assignment");
        });
        check("No suitable registered placement is normal and generates new people without forcing a registered candidate", () =>
        {
            var calls = 0; var result = Batch(new CrewCharacter(CharacterId)).GenerateAsync("", input =>
            {
                calls++; var name = Name(input);
                return Task.FromResult(Reply(name, name == "assess_crew_fit" ? Fit() : name == "select_crew" ? Selections("new", "new") : People()));
            }, CancellationToken.None).GetAwaiter().GetResult();
            Require(calls == 3 && result.Appointments.Values.All(p => p.Person.RegisteredCharacterId == null), "Empty fit triggered failure or forced a character");
        });
        check("Invalid assessments cannot widen platform eligibility, omit characters or introduce unknown or duplicate IDs", () =>
        {
            foreach (var kind in new[] { "unknown_character", "missing", "duplicate_character", "unknown_unit", "outside_scope", "duplicate_unit" })
            {
                var calls = 0; var batch = Batch(new CrewCharacter(CharacterId), firstUnitOnly: true);
                Throws<CrewException>(() => batch.GenerateAsync("", input =>
                {
                    calls++; var rows = Fit("unit:1");
                    if (kind == "unknown_character") rows[0]!["character_id"] = "22222222222222222222222222222222";
                    if (kind == "missing") rows.Clear();
                    if (kind == "duplicate_character") rows.Add(rows[0]!.DeepClone());
                    if (kind == "unknown_unit") rows[0]!["unit_ids"] = new JArray("unit:99");
                    if (kind == "outside_scope") rows[0]!["unit_ids"] = new JArray("unit:2");
                    if (kind == "duplicate_unit") rows[0]!["unit_ids"] = new JArray("unit:1", "unit:1");
                    return Task.FromResult(Reply(Name(input), rows));
                }, CancellationToken.None).GetAwaiter().GetResult());
                Require(calls == 1, "An invalid assessment progressed to selection: " + kind);
            }
        });
        check("Selection rejects evaluated-out units, unknown or duplicate units and non-reusable character collisions before generating names", () =>
        {
            foreach (var kind in new[] { "outside_fit", "unknown_character", "unknown_unit", "duplicate_unit", "duplicate_character", "missing_unit", "extra_field" })
            {
                var calls = 0;
                Throws<CrewException>(() => Batch(new CrewCharacter(CharacterId)).GenerateAsync("", input =>
                {
                    calls++; var name = Name(input);
                    if (name == "assess_crew_fit") return Task.FromResult(Reply(name, kind == "duplicate_character" ? Fit("unit:1", "unit:2") : Fit("unit:1")));
                    var rows = Selections(CharacterId, "new");
                    if (kind == "outside_fit") rows = Selections("new", CharacterId);
                    if (kind == "unknown_character") rows[0]!["character_id"] = "22222222222222222222222222222222";
                    if (kind == "unknown_unit") rows[1]!["id"] = "unit:99";
                    if (kind == "duplicate_unit") rows[1]!["id"] = "unit:1";
                    if (kind == "duplicate_character") rows = Selections(CharacterId, CharacterId);
                    if (kind == "missing_unit") rows.RemoveAt(1);
                    if (kind == "extra_field") rows[0]!["unexpected"] = true;
                    return Task.FromResult(Reply(name, rows));
                }, CancellationToken.None).GetAwaiter().GetResult());
                Require(calls == 2, "Invalid selection progressed to completion: " + kind);
            }
        });
        check("Completion cannot change chosen rank or unit scope and incomplete final people never publish a partial generation", () =>
        {
            foreach (var kind in new[] { "missing", "unknown", "duplicate", "rank_override", "character_override" })
            {
                var calls = 0;
                Throws<CrewException>(() => Batch(new CrewCharacter(CharacterId)).GenerateAsync("", input =>
                {
                    calls++; var name = Name(input);
                    if (name == "assess_crew_fit") return Task.FromResult(Reply(name, Fit("unit:1")));
                    if (name == "select_crew") return Task.FromResult(Reply(name, Selections(CharacterId, "new")));
                    var rows = People();
                    if (kind == "missing") rows.RemoveAt(1);
                    if (kind == "unknown") rows[1]!["id"] = "unit:99";
                    if (kind == "duplicate") rows[1]!["id"] = "unit:1";
                    if (kind == "rank_override") rows[0]!["rank"] = "Admiral";
                    if (kind == "character_override") rows[0]!["character_id"] = "new";
                    return Task.FromResult(Reply(name, rows));
                }, CancellationToken.None).GetAwaiter().GetResult());
                Require(calls == 3, "The completion failure was retried or returned early");
            }
        });
        check("Manual registered completion skips assessment and preserves explicit name, rank, nationality and portrait", () =>
        {
            var character = new CrewCharacter(CharacterId, "登録名", "既存階級", gender: "female", nationality: "日本", portraitId: "33333333333333333333333333333333.png");
            var calls = 0; var batch = Single(character);
            var result = batch.GenerateAsync("", input =>
            {
                calls++; var name = Name(input);
                if (name == "select_crew") return Task.FromResult(Reply(name, new JArray(Selection("unit:1", CharacterId, "AI altered rank"))));
                var person = input.Data["people"]![0]!;
                Require((string?)person["nationality"] == "日本" && (string?)person["rank"] == "既存階級" && (string?)person["name"] == "登録名", "Explicit identity was replaced by unit defaults");
                return Task.FromResult(Reply(name, new JArray(Person("unit:1", "AI altered name"))));
            }, CancellationToken.None).GetAwaiter().GetResult();
            var appointed = result.Appointments["unit:1"].Person;
            Require(calls == 2 && appointed.Name == character.Name && appointed.Rank == character.Rank && appointed.PortraitId == character.PortraitId && appointed.RegisteredCharacterId == character.Id,
                "Manual completion lost fixed data or performed unnecessary fit evaluation");
        });
        check("Cancellation after any appointment stage prevents subsequent requests and returns no incomplete job", () =>
        {
            foreach (var after in new[] { 1, 2, 3 })
            {
                using var cancellation = new CancellationTokenSource(); var calls = 0;
                Throws<OperationCanceledException>(() => Batch(new CrewCharacter(CharacterId)).GenerateAsync("", input =>
                {
                    calls++; var name = Name(input); var reply = Reply(name, name == "assess_crew_fit" ? Fit("unit:1") : name == "select_crew" ? Selections(CharacterId, "new") : People());
                    if (calls == after) cancellation.Cancel(); return Task.FromResult(reply);
                }, cancellation.Token).GetAwaiter().GetResult());
                Require(calls == after, "Cancellation continued another stage");
            }
        });
        check("The selected Chatter connection accounts for every staged request, including absent usage and partial failures", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); SaveConnection(path); var usage = new CrewUsage(); var requests = 0; var failLast = false;
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
                new AiHttpTransport(new Handler(async (request, _) =>
                {
                    requests++; var body = JObject.Parse(await request.Content!.ReadAsStringAsync()); var name = (string)body["tools"]![0]!["function"]!["name"]!;
                    Require(((JArray)body["messages"]!).Count == 2 && (string?)body["model"] == "deepseek/test" && body["messages"]![0]!["content"]!.Value<string>()!.Contains("STAGE_PROMPT", StringComparison.Ordinal), "A stage changed model, lost its prompt or resent earlier exchanges");
                    if (failLast && name == "submit_crew_people") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    return Response(Reply(name, name == "assess_crew_fit" ? Fit("unit:1") : name == "select_crew" ? Selections(CharacterId, "new") : People(), name == "submit_crew_people" ? null : name == "assess_crew_fit" ? 10 : 20));
                }), trace));
            _ = connections.GenerateAsync(Batch(new CrewCharacter(CharacterId)), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            Require(requests == 3 && usage.Snapshot.ApiRequests == 3 && usage.Snapshot.KnownTokens == 30 && usage.Snapshot.UnknownRequests == 1 && usage.Snapshot.LastTokens == null, "Only the last stage was counted or missing usage became zero");
            failLast = true;
            Throws<AiConnectionException>(() => connections.GenerateAsync(Batch(new CrewCharacter(CharacterId)), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult());
            Require(requests == 6 && usage.Snapshot.ApiRequests == 6 && usage.Snapshot.KnownTokens == 60 && usage.Snapshot.UnknownRequests == 2, "Partial failure discarded earlier usage or triggered fallback");
        }));
        check("Cancelling between real connection stages retains completed usage and never starts the next API request", () => Temp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); SaveConnection(path); var usage = new CrewUsage(); var requests = 0;
            using var cancellation = new CancellationTokenSource();
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
                new AiHttpTransport(new Handler(async (request, _) =>
                {
                    requests++; var name = (string)JObject.Parse(await request.Content!.ReadAsStringAsync())["tools"]![0]!["function"]!["name"]!;
                    return Response(Reply(name, name == "assess_crew_fit" ? Fit("unit:1") : Selections(CharacterId, "new"), 10));
                }), trace));
            Throws<OperationCanceledException>(() => connections.GenerateAsync(Batch(new CrewCharacter(CharacterId)), "chatter", cancellation.Token, tokens =>
            { usage.Record(tokens); if (usage.Snapshot.ApiRequests == 2) cancellation.Cancel(); }).GetAwaiter().GetResult());
            Require(requests == 2 && usage.Snapshot.ApiRequests == 2 && usage.Snapshot.KnownTokens == 20 && usage.Snapshot.UnknownRequests == 0, "Cancellation lost known usage or sent the completion request");
        }));
    }
    private static CrewBatch Batch(CrewCharacter character, bool firstUnitOnly = false)
    {
        var data = Data("unit:1", "unit:2");
        return new CrewBatch(data, new Dictionary<string, CrewPerson?>(), new[] { "unit:1", "unit:2" }, automaticCharacters:
            firstUnitOnly ? new Dictionary<string, CrewCharacter[]> { ["unit:1"] = new[] { character } } : new Dictionary<string, CrewCharacter[]> { ["unit:1"] = new[] { character }, ["unit:2"] = new[] { character } });
    }
    private static CrewBatch Single(CrewCharacter character) => new(Data("unit:1"), new Dictionary<string, CrewPerson?>(), new[] { "unit:1" }, characters: new Dictionary<string, CrewCharacter> { ["unit:1"] = character });
    private static JObject Data(params string[] ids) => new()
    {
        ["reply_language"] = "ja", ["appointment_context"] = new JObject { ["captured_at_utc"] = "1985-06-26T20:57:00.0000000", ["mission_briefing"] = "Classified fixture briefing" },
        ["appointments"] = new JArray(ids.Select(id => new JObject { ["id"] = id, ["nation"] = id == "unit:1" ? "US" : "Japan", ["role"] = "captain", ["kind"] = "vessel", ["class_name"] = "Fixture" }))
    };
    private static JArray Fit(params string[] ids) => new(new JObject { ["character_id"] = CharacterId, ["reason"] = "Fixture coherent staffing.", ["unit_ids"] = new JArray(ids) });
    private static JObject Selection(string id, string character, string rank = "Lt.") => new() { ["id"] = id, ["character_id"] = character, ["rank"] = rank };
    private static JArray Selections(string first, string second) => new(Selection("unit:1", first), Selection("unit:2", second));
    private static JObject Person(string id, string name = "Alex Morgan") => new() { ["id"] = id, ["person_name"] = name, ["name_with_rank"] = "Lt. " + name, ["personality"] = "Calm" };
    private static JArray People() => new(Person("unit:1"), Person("unit:2"));
    private static string Name(AiProviderInput input) => (string)input.Tools.Single()["name"]!;
    private static AiReply Reply(string tool, JArray rows, long? tokens = 10) => new("", new[] { new AiToolCall("fixture", tool, new JObject { ["appointments"] = rows }) }, tokens);
    private static HttpResponseMessage Response(AiReply reply)
    {
        var call = reply.Calls.Single(); var value = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = "tool_calls", ["message"] = new JObject { ["role"] = "assistant", ["tool_calls"] = new JArray(new JObject
        { ["id"] = "fixture", ["type"] = "function", ["function"] = new JObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString(Newtonsoft.Json.Formatting.None) } }) } }), ["usage"] = reply.TotalTokens.HasValue ? new JObject { ["total_tokens"] = reply.TotalTokens.Value } : JValue.CreateNull() };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value.ToString(), Encoding.UTF8, "application/json") };
    }
    private static void SaveConnection(string path)
    {
        var settings = new AdvisorSettingsDraft(new JObject()); settings.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration("chatter", "Chatter", "nanogpt", new JObject { ["api_key"] = "mock", ["model"] = "deepseek/test" }, prompt: "STAGE_PROMPT")));
        AdvisorSettings.WriteBytes(path, settings.Data());
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Temp(Action<string> action)
    { var path = Path.Combine(Path.GetTempPath(), "crew-stages-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); try { action(path); } finally { Directory.Delete(path, true); } }
}
