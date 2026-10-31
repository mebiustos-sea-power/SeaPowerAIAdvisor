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

internal static class CrewConnectionTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Chatter uses its own selected saved profile and one submission request without adviser prompts, history or operations", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data());
            var sends = 0; var usage = new CrewUsage();
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace => new AiHttpTransport(new Handler(async (request, _) =>
            {
                sends++; var body = JObject.Parse(await request.Content!.ReadAsStringAsync()); var system = (string)body["messages"]![0]!["content"]!;
                Require((string?)body["model"] == "deepseek/chatter" && request.Headers.GetValues("Authorization").Single() == "Bearer mock_chatter", "Chatter used another feature's model or credentials");
                Require(system.Contains("CHATTER_SUPPLEMENT", StringComparison.Ordinal) && !system.Contains("ADVISOR_SUPPLEMENT", StringComparison.Ordinal) && !system.Contains("FALLBACK_SUPPLEMENT", StringComparison.Ordinal), "Another connection's prompt leaked into Chatter");
                Require(((JArray)body["messages"]!).Count == 2 && ((JArray)body["tools"]!).Count == 1 && (string?)body["tools"]![0]!["function"]!["name"] == CrewBatch.ToolName, "A tactical history, retrieval or operation tool entered Chatter");
                return Response(HttpStatusCode.OK);
            }), trace));
            var result = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            Require(sends == 1 && result.Remarks.Single().Id == "unit:1" && usage.Snapshot.KnownTokens == 10 && usage.Snapshot.ApiRequests == 1, "The terminal remark submission caused extra exchanges or lost usage");
        }));
        check("Missing Chatter connections fail before transport creation and server failure never uses the adviser's fallback", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data()); var created = 0; var sends = 0; var usage = new CrewUsage();
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
            { created++; return new AiHttpTransport(new Handler((_, _) => { sends++; return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)); }), trace); });
            try { _ = connections.GenerateAsync(Batch(), "removed-id", CancellationToken.None, usage.Record).GetAwaiter().GetResult(); throw new InvalidOperationException("Missing connection was accepted"); }
            catch (CrewException e) { Require(e.Code == "connection_missing" && created == 0 && usage.Snapshot.ApiRequests == 0, "Missing configuration opened a transport or was hidden"); }
            try { _ = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult(); throw new InvalidOperationException("Server failure was accepted"); }
            catch (AiConnectionException e) { Require(e.HttpStatus == 503 && sends == 1 && created == 1 && usage.Snapshot.ApiRequests == 1 && usage.Snapshot.UnknownRequests == 1 && usage.Snapshot.LastTokens == null, "Chatter silently tried another assigned model"); }
        }));
        check("Chatter accounts for silent and appointment-only generations even without any history entry", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data()); var sent = 0; var usage = new CrewUsage();
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
                new AiHttpTransport(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, ++sent * 10, silent: true))), trace));
            _ = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            var appointments = new CrewBatch(new JObject(), new Dictionary<string, CrewPerson?>(), new[] { "unit:1" });
            var result = connections.GenerateAsync(appointments, "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            Require(result.Remarks.Count == 0 && result.Appointments.Count == 1 && usage.Snapshot.ApiRequests == 2 && usage.Snapshot.KnownTokens == 30 &&
                usage.Snapshot.LastTokens == 20 && usage.Snapshot.UnknownRequests == 0, "Silent generation or appointment-only usage was lost");
        }));
        check("Invalid remark content still counts known usage, and unavailable usage is distinct from reported zero", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data()); var sent = 0; var usage = new CrewUsage();
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
                new AiHttpTransport(new Handler((_, _) => Task.FromResult(++sent switch
                {
                    1 => Response(HttpStatusCode.OK, 40, invalid: true),
                    2 => Response(HttpStatusCode.OK, null),
                    _ => Response(HttpStatusCode.OK, 0)
                })), trace));
            try { _ = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult(); throw new InvalidOperationException("Invalid remark was accepted"); }
            catch (CrewException e) { Require(e.Code == "invalid_response" && usage.Snapshot.KnownTokens == 40 && usage.Snapshot.ApiRequests == 1, "Rejected chatter erased known usage"); }
            _ = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            Require(usage.Snapshot.KnownTokens == 40 && usage.Snapshot.UnknownRequests == 1 && usage.Snapshot.LastTokens == null, "Unavailable usage was recorded as zero");
            _ = connections.GenerateAsync(Batch(), "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult();
            Require(usage.Snapshot.ApiRequests == 3 && usage.Snapshot.KnownTokens == 40 && usage.Snapshot.UnknownRequests == 1 && usage.Snapshot.LastTokens == 0,
                "An explicit zero was treated as unavailable or a generation was counted twice");
        }));
        check("Cancelling an in-flight Chatter request records unavailable usage while pre-send failures count no request", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data()); var usage = new CrewUsage(); var sent = 0;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connections = new ChatterConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
                new AiHttpTransport(new Handler(async (_, token) => { sent++; entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return Response(HttpStatusCode.OK); }), trace));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { _ = connections.GenerateAsync(Batch(), "chatter", cancelled.Token, usage.Record).GetAwaiter().GetResult(); throw new InvalidOperationException("A cancelled generation was accepted"); }
            catch (OperationCanceledException) { Require(sent == 0 && usage.Snapshot.ApiRequests == 0, "Pre-send cancellation counted a request"); }
            var oversized = new CrewBatch(new JObject { ["padding"] = new string('x', 1_000_001) }, new Dictionary<string, CrewPerson?>(), new[] { "unit:1" });
            try { _ = connections.GenerateAsync(oversized, "chatter", CancellationToken.None, usage.Record).GetAwaiter().GetResult(); throw new InvalidOperationException("Oversized generation was sent"); }
            catch (CrewException e) { Require(e.Code == "too_large" && sent == 0 && usage.Snapshot.ApiRequests == 0, "Local validation was charged as a request"); }
            using var running = new CancellationTokenSource(); var pending = connections.GenerateAsync(Batch(), "chatter", running.Token, usage.Record);
            Require(entered.Task.Wait(TimeSpan.FromSeconds(3)), "Request did not reach the mock server"); running.Cancel();
            try { _ = pending.GetAwaiter().GetResult(); throw new InvalidOperationException("An in-flight cancellation was accepted"); }
            catch (OperationCanceledException)
            { Require(sent == 1 && usage.Snapshot.ApiRequests == 1 && usage.Snapshot.KnownTokens == 0 && usage.Snapshot.UnknownRequests == 1 && usage.Snapshot.LastTokens == null, "Cancelled usage disappeared or became known zero"); }
        }));
    }
    private static CrewBatch Batch() => new(new JObject { ["reply_language"] = "en", ["appointments"] = new JArray(new JObject { ["id"] = "unit:1", ["nation"] = "US", ["role"] = "captain" }), ["speakers"] = new JArray(new JObject { ["id"] = "unit:1" }) }, new Dictionary<string, CrewPerson?> { ["unit:1"] = null }, new[] { "unit:1" });
    private static AdvisorSettingsDraft Settings()
    {
        var saved = new AdvisorSettingsDraft(new JObject());
        foreach (var id in new[] { "advisor", "chatter", "fallback" })
            saved.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, id, "nanogpt", new JObject { ["api_key"] = "mock_" + id, ["model"] = "deepseek/" + id }, prompt: id.ToUpperInvariant() + "_SUPPLEMENT")));
        saved.MainId = "advisor"; saved.SubId = "fallback"; return saved;
    }
    private static HttpResponseMessage Response(HttpStatusCode status, long? tokens = 10, bool silent = false, bool invalid = false)
    {
        var rows = silent ? new JArray() : new JArray(new JObject { ["id"] = invalid ? "unit:99" : "unit:1", ["text"] = "Maintaining station." });
        var appointments = new JArray(new JObject { ["id"] = "unit:1", ["person_name"] = "Fictional", ["personality"] = "Calm" });
        var result = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = "tool_calls", ["message"] = new JObject { ["role"] = "assistant", ["tool_calls"] = new JArray(new JObject { ["id"] = "chatter", ["type"] = "function", ["function"] = new JObject { ["name"] = CrewBatch.ToolName, ["arguments"] = new JObject { ["appointments"] = appointments, ["remarks"] = rows }.ToString(Newtonsoft.Json.Formatting.None) } }) } }), ["usage"] = tokens.HasValue ? new JObject { ["total_tokens"] = tokens.Value } : JValue.CreateNull() };
        return new HttpResponseMessage(status) { Content = new StringContent(result.ToString(), Encoding.UTF8, "application/json") };
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-connections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
