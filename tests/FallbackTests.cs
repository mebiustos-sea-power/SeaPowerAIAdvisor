using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class FallbackTests
{
    private const string Main = "mock_primary_never_sent_to_real_API";
    private const string Backup = "mock_fallback_never_sent_to_real_API";
    internal static void Run(Action<string, Action> check)
    {
        check("The main key stays required while the optional fallback persists, clears and discards without changing advanced settings", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var original = new JObject { ["api_key"] = Main, ["model"] = "gemini-test-model", ["max_output_tokens"] = 2048,
                ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90 };
            File.WriteAllText(path, original.ToString()); var before = File.ReadAllBytes(path);
            Require(AdvisorSettings.Load(path).FallbackApiKey == "", "A legacy configuration enabled fallback");
            var draft = AdvisorSettings.Edit(path); var added = draft.Add(); added.ApiKey = Backup; draft.SubId = added.Id; _ = draft.Data();
            Require(File.ReadAllBytes(path).SequenceEqual(before) && AdvisorSettings.Edit(path).SubId == null, "Editing enabled fallback before Save");
            AdvisorSettings.WriteBytes(path, draft.Data()); var saved = AdvisorSettings.Load(path);
            Require(saved.ApiKey == Main && saved.FallbackApiKey == Backup && saved.ForFallback().RequestApiKey == Backup &&
                saved.ForFallback().ApiKey == Main && saved.MaxOutputTokens == 2048 && saved.RequestTimeoutSeconds == 45,
                "Saving fallback changed the primary or advanced settings");
            draft = AdvisorSettings.Edit(path); draft.SubId = null; AdvisorSettings.WriteBytes(path, draft.Data());
            Require(AdvisorSettings.Load(path).FallbackApiKey == "", "Clearing the optional key did not disable fallback");
            Throws("api_key_missing", () => AdvisorSettings.Parse(new JObject { ["api_key"] = "", ["fallback_api_key"] = Backup }.ToString()));
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue(42), new JValue("has\ncontrol"), new JValue(new string('x', 513)) })
                Throws("settings_invalid", () => AdvisorSettings.Parse(new JObject { ["api_key"] = Main, ["fallback_api_key"] = invalid }.ToString()));
        }));
        check("A mid-consultation switch starts a new server conversation with evidence and keeps subsequent retrievals on fallback", () => Async(async () =>
        {
            var keys = new List<string>(); var bodies = new List<string>(); var events = new List<AdvisorProgress>();
            var switches = 0; var session = Session(2);
            string? primaryId = null, backupId = null;
            using var transport = new GeminiTransport(new Handler(async (request, token) =>
            {
                keys.Add(Key(request)); bodies.Add(await request.Content!.ReadAsStringAsync(token));
                var call = keys.Count;
                if (call == 1)
                {
                    var first = JObject.Parse(Reply(Tool("catalog", "signature-A"))); primaryId = (string)first["id"]!;
                    Require(JObject.Parse(bodies.Last())["previous_interaction_id"] == null, "Initial consultation reused another conversation");
                    return Response(200, first.ToString(Formatting.None));
                }
                if (call == 2)
                {
                    Require((string?)JObject.Parse(bodies.Last())["previous_interaction_id"] == primaryId, "Primary continuation lost its interaction ID");
                    return Response(429, "{\"error\":\"quota\"}");
                }
                if (call == 3)
                {
                    var body = JObject.Parse(bodies.Last());
                    var continuation = InteractionFixture.Initial(body)["continuation"]!;
                    Require(body["previous_interaction_id"] == null && !body.ToString().Contains("signature-A", StringComparison.Ordinal) &&
                        continuation["completed_retrievals"]!.Count() == 1 && (int?)continuation["query_rounds_used"] == 1 &&
                        session.RoundsUsed == 1, "Fallback reused a foreign conversation, lost evidence or reset queries");
                    var next = JObject.Parse(Reply(Tool("get_briefing", "signature-B"))); backupId = (string)next["id"]!;
                    return Response(200, next.ToString(Formatting.None));
                }
                var current = JObject.Parse(bodies.Last());
                if (call == 4)
                    Require((string?)current["generation_config"]!["tool_choice"] == "none" &&
                        (string?)current["previous_interaction_id"] == backupId, "Failover reset the limit or lost the backup conversation");
                else Require(current["previous_interaction_id"] == null && InteractionFixture.Initial(current)["continuation"] == null,
                    "A new consultation inherited an earlier server conversation or fallback context");
                return Response(200, Reply(new JObject { ["text"] = "Answer" }));
            }));
            var advisor = new GeminiAdvisor(transport, _ => switches++);
            var result = await advisor.ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None, progress: events.Add);
            Require(result.Text == "Answer" && result.ApiRequests == 4 && result.QueryRounds == 2 && result.TotalTokens == null && switches == 1 &&
                keys.SequenceEqual(new[] { Main, Main, Backup, Backup }), "Fallback state, actual request count or unknown usage was incorrect");
            var handoff = events.Single(p => p.Stage == AdvisorStage.SwitchingKey);
            Require(handoff.ApiRequest == 2 && handoff.Rounds == 1 && handoff.UsingFallback &&
                events.Last().Stage == AdvisorStage.FinalAnswer && events.Last().ApiRequest == 4 && events.Last().UsingFallback,
                "Progress does not describe the actual requests and active key");
            foreach (var language in new[] { "ja", "en", "fr" })
            {
                var text = AdvisorText.Load(language);
                Require(AdvisorProgress.Format(events.Last(), text).Contains(text["progress.fallback_active"], StringComparison.Ordinal), "Ongoing progress lost the fallback indicator");
            }
            await advisor.ConsultAsync(Session(), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(keys.Last() == Main && switches == 1, "A new consultation inherited fallback state");
        }));
        check("Temporary server, network and request-timeout failures can use fallback while client and generation errors remain visible", () => Async(async () =>
        {
            foreach (var failure in new[] { "503", "408", "504", "network", "timeout" })
            {
                var keys = new List<string>();
                using var transport = new GeminiTransport(new Handler((request, token) =>
                {
                    keys.Add(Key(request));
                    if (keys.Count == 1)
                    {
                        if (failure == "network") throw new HttpRequestException("simulated network outage");
                        if (failure == "timeout") throw new OperationCanceledException();
                        return Task.FromResult(Response(int.Parse(failure), "{}"));
                    }
                    return Task.FromResult(Response(200, Reply(new JObject { ["text"] = "Recovered" })));
                }));
                var answer = await new GeminiAdvisor(transport).ConsultAsync(Session(), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
                Require(answer.ApiRequests == 2 && keys.SequenceEqual(new[] { Main, Backup }), "A supported temporary failure did not switch: " + failure);
            }
            foreach (var (status, code) in new[] { (400,"api_bad_request"), (401,"api_access_denied"), (403,"api_access_denied"),
                (404,"model_unavailable"), (500,"api_error"), (200,"invalid_response") })
            {
                var handler = new Handler((request, token) => Task.FromResult(Response(status, "malformed")));
                using var transport = new GeminiTransport(handler);
                var error = await Fails(code, () => new GeminiAdvisor(transport).ConsultAsync(Session(), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
                Require(handler.Calls == 1 && !error.UsingFallback, "An unrelated error was hidden by fallback");
            }
            var limited = new Handler((request, token) => Task.FromResult(Response(429, "{}")));
            using var primaryOnly = new GeminiTransport(limited);
            await Fails("rate_limited", () => new GeminiAdvisor(primaryOnly).ConsultAsync(Session(), Settings(""), Array.Empty<ChatExchange>(), CancellationToken.None));
            Require(limited.Calls == 1, "A missing optional key triggered a retry");
        }));
        check("Fallback failure is terminal and cancellation interrupts the handoff without another request", () => Async(async () =>
        {
            var keys = new List<string>();
            using (var transport = new GeminiTransport(new Handler((request, token) =>
            { keys.Add(Key(request)); return Task.FromResult(Response(503,"{}")); })))
            {
                var error = await Fails("api_error", () => new GeminiAdvisor(transport).ConsultAsync(Session(), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
                Require(keys.SequenceEqual(new[] { Main, Backup }) && error.UsingFallback &&
                    AdvisorNotice.Connection(error).Message(AdvisorText.Load("ja")).Contains(AdvisorText.Load("ja")["connection.fallback_failed"], StringComparison.Ordinal),
                    "A fallback failure retried or did not identify the failing key slot");
            }
            using var cancellation = new CancellationTokenSource();
            var handler = new Handler((request, token) => Task.FromResult(Response(503,"{}")));
            using var cancelled = new GeminiTransport(handler);
            try { await new GeminiAdvisor(cancelled, _ => cancellation.Cancel()).ConsultAsync(Session(), Settings(), Array.Empty<ChatExchange>(), cancellation.Token); }
            catch (OperationCanceledException)
            { Require(handler.Calls == 1, "Cancellation during the handoff sent another request"); return; }
            throw new InvalidOperationException("Cancellation was ignored");
        }));
        check("Resending counts against the existing payload budget instead of resetting consultation limits", () => Async(async () =>
        {
            var handler = new Handler((request, token) => Task.FromResult(Response(503,"{}")));
            using var transport = new GeminiTransport(handler);
            var session = Session();
            await Fails("request_budget", () => new GeminiAdvisor(transport).ConsultAsync(session, Settings(),
                new[] { new ChatExchange("Earlier question", new string('x', 550000)) }, CancellationToken.None));
            Require(handler.Calls == 1 && session.RoundsUsed == 0, "Failover bypassed the byte budget or consumed an information round");
        }));
        check("Each connection test uses only its chosen pending key and cannot conceal a failing main key through fallback", () => Async(async () =>
        {
            var draft = new AdvisorSettingsDraft(new JObject { ["api_key"] = Main, ["fallback_api_key"] = Backup, ["model"] = "gemini-test-model", ["fallback_model"] = "gemini-connection-backup" });
            var keys = new List<string>();
            var handler = new Handler(async (request, token) =>
            {
                keys.Add(Key(request));
                var expectedModel = Key(request) == Main ? "gemini-test-model" : "gemini-connection-backup";
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(request.RequestUri!.AbsolutePath == "/v1beta/interactions" && (string?)body["model"] == expectedModel,
                    "The connection test used the other key's model");
                return Key(request) == Main ? Response(503,"{}") : Response(200, Reply(new JObject { ["text"] = "OK" }));
            });
            await Fails("api_error", () => GeminiConnectionTest.RunAsync("unused-fallback-tests", draft.TestSettings(), CancellationToken.None, trace => new GeminiTransport(handler, trace)));
            var result = await GeminiConnectionTest.RunAsync("unused-fallback-tests", draft.TestSettings(true), CancellationToken.None, trace => new GeminiTransport(handler, trace));
            Require(keys.SequenceEqual(new[] { Main, Backup }) && result.ApiRequests == 1, "A key test switched credentials or tested both keys");
            draft.SubId = null;
            Throws("fallback_key_missing", () => draft.TestSettings(true));
        }));
        check("Fallback logs keep both attempts and the switch in order while removing both credentials from echoed content", () => InTemp(directory => Async(async () =>
        {
            var sent = new List<string>();
            var handler = new Handler(async (request, token) =>
            {
                sent.Add(await request.Content!.ReadAsStringAsync(token));
                if (sent.Count == 1) return Response(429, new JObject { ["error"] = Main + " " + Backup }.ToString());
                var nested = new JObject { ["key"] = Backup }.ToString(Formatting.None);
                return Response(200, Reply(new JObject { ["text"] = "Answer " + Main + " " + nested }));
            });
            var result = await GeminiAdvisor.RunAsync(directory, Session(), Settings(log: true), new AdvisorQuestion("Question"),
                Array.Empty<ChatExchange>(), CancellationToken.None, trace => new GeminiTransport(handler, trace));
            var file = Directory.GetFiles(directory, "*.jsonl").Single(); var raw = File.ReadAllText(file);
            var records = File.ReadAllLines(file).Select(JObject.Parse).ToArray();
            Require(!raw.Contains(Main, StringComparison.Ordinal) && !raw.Contains(Backup, StringComparison.Ordinal) &&
                result.Text.Contains(Main, StringComparison.Ordinal) && result.Text.Contains(Backup, StringComparison.Ordinal), "Key scrubbing leaked secrets or modified the actual answer");
            Require(records.Select(r => (string?)r["event"]).SequenceEqual(new[] {
                "consultation_started","request","response","request_failed","key_changed","request","response","consultation_finished" }),
                "A failed attempt or handoff is missing from chronological logs");
            var requests = records.Where(r => (string?)r["event"] == "request").ToArray();
            Require(requests.Select(r => (string?)r["key_slot"]).SequenceEqual(new[] { "primary", "fallback" }) &&
                requests.Select(r => (int)r["api_request"]!).SequenceEqual(new[] { 1,2 }) &&
                requests.Select(r => (string?)r["body_json"]).SequenceEqual(sent) && (int)records.Last()["api_requests"]! == 2,
                "Logs do not identify actual attempts and key slots or altered the outgoing JSON");
        })));
    }
    private static AdvisorSettings Settings(string fallback = Backup, bool log = false) => AdvisorSettings.Parse(new JObject {
        ["api_key"] = Main, ["fallback_api_key"] = fallback, ["model"] = "gemini-test-model", ["log_ai_json"] = log }.ToString());
    private static BattleQuerySession Session(int maxRounds = 1) => new(new BattleSnapshot(new JObject {
        ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
        ["public_objectives"] = new JArray(),
        ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(), ["unit_capabilities"] = new JArray(),
        ["contact_threat_references"] = new JArray(), ["weapon_references"] = new JArray(), ["published_reports"] = new JArray(), ["mission_briefing"] = "Briefing"
    }), "ja", "Question", maxRounds: maxRounds);
    private static JObject Tool(string query, string signature) => new() { ["thoughtSignature"] = signature, ["functionCall"] = new JObject {
        ["id"] = query, ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) } } };
    private static string Reply(params JObject[] parts) => InteractionFixture.Reply(parts).ToString(Formatting.None);
    private static string Key(HttpRequestMessage request) => request.Headers.GetValues("x-goog-api-key").Single();
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send;
        internal int Calls;
        internal Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request,token); }
    }
    private static async Task<AdvisorConnectionException> Fails(string code, Func<Task<AdviceResult>> action)
    { try { await action(); } catch (AdvisorConnectionException error) { Require(error.Code == code, "Wrong error: " + error.Code); return error; } throw new InvalidOperationException("Expected " + code); }
    private static void Throws(string code, Action action)
    { try { action(); } catch (AdvisorConnectionException error) { Require(error.Code == code, "Wrong validation error"); return; } throw new InvalidOperationException("Expected " + code); }
    private static void InTemp(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); try { action(path); } finally { Directory.Delete(path,true); }
    }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
