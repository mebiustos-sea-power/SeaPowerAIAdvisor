using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class TraceTests
{
    private const string Key = "mock_trace_key_never_used_for_real_network";
    internal static void Run(string campaignPath, Action<string, Action> check)
    {
        var assembly = Assembly.LoadFrom(campaignPath);
        AdvisorQuerySession Session(AdvisorQuestion question) => new(QueryTests.Capture(new SituationTests.Fixture(assembly)), "ja", question.Text);
        check("A consultation logs every actual HTTP request and response in order including tool exchanges", () => InTemp(async directory =>
        {
            var question = AdvisorShortcut.NextSteps.Prepare("次の行動");
            var session = Session(question);
            var sent = new List<string>();
            var received = new List<string>();
            var handler = new Handler(async (request, cancellation) =>
            {
                var json = await request.Content!.ReadAsStringAsync();
                sent.Add(json);
                string response;
                if (sent.Count == 1)
                    response = Reply(new JObject { ["functionCall"] = new JObject
                    {
                        ["id"] = "force-list", ["name"] = "query_situation",
                        ["args"] = new JObject { ["requests"] = new JArray(QueryTests.Q("list_forces")) }
                    }, ["thoughtSignature"] = "opaque-signature" });
                else
                {
                    var body = JObject.Parse(json);
                    Require(InteractionFixture.Result(body)["results"]![0]!["data"] != null,
                        "The follow-up request omitted the returned situation data");
                    response = Reply(new JObject { ["text"] = "具体的な回答𠮷\nLine two" });
                }
                received.Add(response);
                return Response(200, response);
            });
            var result = await GeminiAdvisor.RunAsync(directory, session, Settings(), question,
                Array.Empty<ChatExchange>(), CancellationToken.None, trace => new GeminiTransport(handler, trace));
            var file = Directory.GetFiles(directory, "*.jsonl").Single();
            var records = Read(file);
            Require(records.Select(r => (string)r["event"]!).SequenceEqual(new[]
                { "consultation_started", "request", "response", "request", "response", "consultation_finished" }), "HTTP events were missing or reordered");
            for (var i = 0; i < records.Length; i++)
            {
                Require((int)records[i]["sequence"]! == i + 1 && (string?)records[i]["consultation_id"] == session.Id, "Sequence or consultation identity changed");
                Require(DateTime.Parse((string)records[i]["timestamp_utc"]!, null, DateTimeStyles.RoundtripKind).Kind == DateTimeKind.Utc, "Log timestamp is not UTC");
                if (i > 0) Require((long)records[i]["elapsed_ms"]! >= (long)records[i - 1]["elapsed_ms"]!, "Elapsed time moved backwards");
            }
            Require((string?)records[0]["display_question"] == "次の行動", "The button caption is missing");
            Require(records.Where(r => (string?)r["event"] == "request").Select(r => (string)r["body_json"]!).SequenceEqual(sent) &&
                records.Where(r => (string?)r["event"] == "response").Select(r => (string)r["body_json"]!).SequenceEqual(received),
                "The log does not contain the complete actual JSON text");
            Require(records.Where(r => (string?)r["event"] == "response").All(r => (bool)r["body_complete"]! && (int)r["http_status"]! == 200),
                "Complete responses were mislabeled");
            Require((string?)records.Last()["outcome"] == "completed" && (int)records.Last()["api_requests"]! == 2 &&
                (int)records.Last()["query_rounds"]! == 1 && result.Text.Contains("𠮷", StringComparison.Ordinal), "The final consultation outcome is missing");
            Require(!File.ReadAllText(file).Contains(Key, StringComparison.Ordinal) &&
                !File.ReadAllText(file).Contains("x-goog-api-key", StringComparison.Ordinal), "Authentication entered the trace");
        }));
        check("Logging scrubs echoed keys while keeping sent and received content unchanged", () => InTemp(async directory =>
        {
            var special = Key + (char)34 + (char)92 + "tail";
            var settings = Settings(true, special);
            var question = new AdvisorQuestion("Check " + special);
            using var trace = AdvisorTrace.Start(directory, "redaction", settings, question)!;
            var body = new JObject { ["plain"] = special, ["nested_json"] = new JObject { ["secret"] = special }.ToString(Formatting.None) };
            var original = body.ToString(Formatting.None);
            var unicode = string.Concat(special.Select(c => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)));
            var reply = "{\"echo\":\"" + unicode + "\"}";
            var handler = new Handler(async (request, cancellation) =>
            {
                Require(await request.Content!.ReadAsStringAsync() == original, "Redaction modified the actual request");
                return Response(200, reply);
            });
            using var transport = new GeminiTransport(handler, trace);
            var returned = await transport.GenerateAsync(settings, body, CancellationToken.None);
            Require((string?)returned["echo"] == special && body.ToString(Formatting.None) == original, "Redaction modified the provider result or source object");
            var records = Read(trace.FilePath);
            foreach (var record in records)
            {
                Require(!record.ToString().Contains(special, StringComparison.Ordinal), "A literal key reached the trace");
                if (record["body_json"] != null)
                {
                    var logged = JObject.Parse((string)record["body_json"]!);
                    Require(!logged.ToString().Contains(Key, StringComparison.Ordinal), "An escaped key can be reconstructed from the log");
                }
            }
        }));
        check("HTTP error bodies and malformed responses are retained without exposing keys", () => InTemp(async directory =>
        {
            foreach (var (status, body, code) in new[]
            {
                (429, "{\"error\":{\"message\":\"limit " + Key + "\"}}", "rate_limited"),
                (200, "invalid JSON\n" + Key, "invalid_response")
            })
            {
                var question = new AdvisorQuestion("Question");
                var session = Session(question);
                await Throws(code, () => GeminiAdvisor.RunAsync(directory, session, Settings(), question, Array.Empty<ChatExchange>(),
                    CancellationToken.None, trace => new GeminiTransport(new Handler((r, c) => Task.FromResult(Response(status, body))), trace)));
                var records = ReadConsultation(directory, session.Id);
                var received = records.Single(r => (string?)r["event"] == "response");
                Require((int)received["http_status"]! == status && (bool)received["body_complete"]! &&
                    ((string)received["body_json"]!).Contains(status == 429 ? "limit" : "invalid JSON", StringComparison.Ordinal), "An error response body was lost");
                Require(!string.Join("", records.Select(r => r.ToString())).Contains(Key, StringComparison.Ordinal), "An error body exposed the API key");
                Require((string?)records.Last()["outcome"] == "failed" && (string?)records.Last()["error_code"] == code, "The final error was not logged");
            }
        }));
        check("Network failure and timeout retain attempted requests and distinct terminal errors", () => InTemp(async directory =>
        {
            foreach (var code in new[] { "network_error", "request_timeout" })
            {
                var question = new AdvisorQuestion("Question");
                var session = Session(question);
                var handler = new Handler((r, c) => Task.FromException<HttpResponseMessage>(
                    code == "network_error" ? new HttpRequestException("simulated") : new OperationCanceledException()));
                await Throws(code, () => GeminiAdvisor.RunAsync(directory, session, Settings(), question, Array.Empty<ChatExchange>(),
                    CancellationToken.None, trace => new GeminiTransport(handler, trace)));
                var records = ReadConsultation(directory, session.Id);
                Require(records.Count(r => (string?)r["event"] == "request") == 1 && !records.Any(r => (string?)r["event"] == "response"),
                    "A missing network response was fabricated or the attempt disappeared");
                Require((string?)records.Last()["error_code"] == code, "The failure kind changed in the log");
            }
        }));
        check("Cancellation records its received prefix and leaves the shared log unlocked", () => InTemp(async directory =>
        {
            using var cancellation = new CancellationTokenSource();
            var question = new AdvisorQuestion("Question");
            var handler = new Handler((r, c) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new InterruptedStream(cancellation)) }));
            try
            {
                await GeminiAdvisor.RunAsync(directory, Session(question), Settings(), question, Array.Empty<ChatExchange>(),
                    cancellation.Token, trace => new GeminiTransport(handler, trace));
                throw new InvalidOperationException("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            var file = Directory.GetFiles(directory, "*.jsonl").Single();
            var records = Read(file);
            var response = records.Single(r => (string?)r["event"] == "response");
            Require(!(bool)response["body_complete"]! && (string?)response["body_json"] == "{\"partial\":" &&
                (string?)records.Last()["outcome"] == "cancelled", "Partial reception or cancellation was lost");
            using var unlocked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }));
        check("Oversized response protection remains bounded and the captured body is marked incomplete", () => InTemp(async directory =>
        {
            var question = new AdvisorQuestion("Question");
            await Throws("response_too_large", () => GeminiAdvisor.RunAsync(directory, Session(question), Settings(), question,
                Array.Empty<ChatExchange>(), CancellationToken.None, trace => new GeminiTransport(
                    new Handler((r, c) => Task.FromResult(Response(200, new string('x', GeminiTransport.MaxResponseBytes + 20_000)))), trace)));
            var response = Read(Directory.GetFiles(directory, "*.jsonl").Single()).Single(r => (string?)r["event"] == "response");
            Require(!(bool)response["body_complete"]! && (long)response["received_bytes"]! > GeminiTransport.MaxResponseBytes &&
                (long)response["received_bytes"]! <= GeminiTransport.MaxResponseBytes + 8192 &&
                ((string)response["body_json"]!).Length == (long)response["received_bytes"]!, "Oversized reception was silently complete or unbounded");
        }));
        check("Logging defaults to off, explicit settings control it, and a write failure stops before HTTP", () => InTemp(async directory =>
        {
            var defaults = AdvisorSettings.Parse(new JObject { ["api_key"] = Key }.ToString());
            Require(!defaults.LogAiJson && Settings().LogAiJson && !Settings(false).LogAiJson,
                "Logging default or explicit override is wrong");
            foreach (var invalid in new[] { "null", "\"false\"", "0" })
            {
                try { AdvisorSettings.Parse("{\"api_key\":\"" + Key + "\",\"log_ai_json\":" + invalid + "}"); }
                catch (AdvisorConnectionException e) when (e.Code == "settings_invalid") { continue; }
                throw new InvalidOperationException("A non-boolean log setting was accepted");
            }
            var disabledPath = Path.Combine(directory, "disabled");
            var question = new AdvisorQuestion("Question");
            var calls = 0;
            foreach (var settings in new[] { defaults, Settings(false) })
                await GeminiAdvisor.RunAsync(disabledPath, Session(question), settings, question, Array.Empty<ChatExchange>(),
                    CancellationToken.None, trace =>
                    {
                        Require(trace == null, "Disabled logging created a writer");
                        return new GeminiTransport(new Handler((r, c) => { calls++; return Task.FromResult(Response(200, Reply(new JObject { ["text"] = "Answer" }))); }), trace);
                    });
            Require(calls == 2 && !Directory.Exists(disabledPath), "Disabling logs affected the consultation or wrote files");
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "file, not directory");
            await Throws("diagnostic_log_error", () => GeminiAdvisor.RunAsync(blocked, Session(question), Settings(), question,
                Array.Empty<ChatExchange>(), CancellationToken.None, trace => { calls++; throw new InvalidOperationException("HTTP should not start"); }));
            Require(calls == 2, "An unlogged consultation was sent despite a failed log");
        }));
        check("Simultaneous consultations share one file while keeping identities and sequences", () => InTemp(async directory =>
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                var question = new AdvisorQuestion("Question " + i);
                await GeminiAdvisor.RunAsync(directory, Session(question), Settings(), question, Array.Empty<ChatExchange>(),
                    CancellationToken.None, trace => new GeminiTransport(new Handler((r, c) =>
                        Task.FromResult(Response(200, Reply(new JObject { ["text"] = "Answer " + i })))), trace));
            })));
            var records = Read(Directory.GetFiles(directory, "*.jsonl").Single());
            var consultations = records.GroupBy(r => (string)r["consultation_id"]!).ToArray();
            Require(records.Length == 8 && consultations.Length == 2, "Shared logging lost a consultation or its identity");
            foreach (var consultation in consultations)
                Require(consultation.Count() == 4 && consultation.Select(r => (int)r["sequence"]!).SequenceEqual(new[] { 1, 2, 3, 4 }),
                    "Concurrent writes corrupted consultation sequences");
        }));
        check("The first enabled consultation replaces old logs once and later consultations and connection tests append", () => InTemp(async directory =>
        {
            var path = Path.Combine(directory, "ai-communication.jsonl");
            File.WriteAllText(path, "previous game run");
            var question = new AdvisorQuestion("First consultation");
            Require(AdvisorTrace.Start(directory, "disabled", Settings(false), question) == null &&
                File.ReadAllText(path) == "previous game run", "Disabled logging erased the previous game run");
            var session = Session(question);
            GeminiTransport Transport(AdvisorTrace? trace) => new(new Handler((r, c) =>
                Task.FromResult(Response(200, Reply(new JObject { ["text"] = "OK" })))), trace);
            await GeminiAdvisor.RunAsync(directory, session, Settings(), question, Array.Empty<ChatExchange>(),
                CancellationToken.None, Transport);
            var first = File.ReadAllText(path);
            Require(Directory.GetFiles(directory, "*.jsonl").Single() == path && Read(path).Length == 4 &&
                !first.Contains("previous game run", StringComparison.Ordinal), "The first logged consultation did not replace the fixed file");
            await GeminiAdvisor.RunAsync(directory, Session(question), Settings(false), question, Array.Empty<ChatExchange>(),
                CancellationToken.None, Transport);
            Require(File.ReadAllText(path) == first, "Turning logging off modified the current log");
            await GeminiConnectionTest.RunAsync(directory, Settings(), CancellationToken.None, Transport);
            var afterTest = File.ReadAllText(path);
            Require(afterTest.StartsWith(first, StringComparison.Ordinal) && Read(path).Length == 8 &&
                Read(path).Any(r => ((string)r["consultation_id"]!).StartsWith("connection-test-", StringComparison.Ordinal)),
                "Re-enabling logging or running a connection test erased earlier records");
            await GeminiAdvisor.RunAsync(directory, Session(question), Settings(), question, Array.Empty<ChatExchange>(),
                CancellationToken.None, Transport);
            Require(File.ReadAllText(path).StartsWith(afterTest, StringComparison.Ordinal) && Read(path).Length == 12 &&
                ReadConsultation(directory, session.Id).Length == 4, "A later consultation truncated earlier records");
        }));
        check("A new game-run log owner preserves old files until its first write and replaces only the shared file", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "ai-communication.jsonl");
            var legacy = Path.Combine(directory, "consultation-older.jsonl");
            File.WriteAllText(path, "previous game run");
            File.WriteAllText(legacy, "legacy log");
            var run = new AdvisorLogRun();
            Require(File.ReadAllText(path) == "previous game run", "Creating a run erased an inactive log");
            run.Write(path, "{\"message\":\"first\"}");
            run.Write(path, "{\"message\":\"second\"}");
            Require(Read(path).Select(r => (string)r["message"]!).SequenceEqual(new[] { "first", "second" }),
                "A run failed to replace old contents once and append subsequent records");
            var nextRun = new AdvisorLogRun();
            Require(Read(path).Length == 2, "Starting the next run erased logs before enabled output");
            nextRun.Write(path, "{\"message\":\"next run\"}");
            Require((string?)Read(path).Single()["message"] == "next run" && File.ReadAllText(legacy) == "legacy log",
                "The next run failed to replace its log or deleted a legacy file");
            return Task.CompletedTask;
        }));
    }

    private static AdvisorSettings Settings(bool logging = true, string? key = null) => AdvisorSettings.Parse(
        new JObject { ["api_key"] = key ?? Key, ["log_ai_json"] = logging }.ToString());
    private static JObject[] ReadConsultation(string directory, string id) =>
        Read(Path.Combine(directory, "ai-communication.jsonl")).Where(r => (string?)r["consultation_id"] == id).ToArray();
    private static JObject[] Read(string file)
    {
        // Allow a record to be appended while inspecting a live log.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(stream, Encoding.UTF8);
        var records = new List<JObject>();
        string? line;
        while ((line = text.ReadLine()) != null)
        {
            using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            records.Add(JObject.Load(reader));
        }
        return records.ToArray();
    }
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Reply(JObject part) => InteractionFixture.Reply(part).ToString(Formatting.None);
    private static void InTemp(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory).GetAwaiter().GetResult(); }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task Throws(string code, Func<Task> test)
    {
        try { await test(); }
        catch (AdvisorConnectionException error) { Require(error.Code == code && !error.ToString().Contains(Key, StringComparison.Ordinal), "Wrong or unsafe error: " + error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class InterruptedStream : MemoryStream
    {
        private readonly CancellationTokenSource cancellation;
        private bool delivered;
        internal InterruptedStream(CancellationTokenSource cancellation) { this.cancellation = cancellation; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (delivered) { cancellation.Cancel(); return Task.FromCanceled<int>(token); }
            var bytes = Encoding.UTF8.GetBytes("{\"partial\":");
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            delivered = true;
            return Task.FromResult(bytes.Length);
        }
    }
}
