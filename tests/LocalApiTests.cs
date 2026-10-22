using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class LocalApiTests
{
    internal static void Run(Action<string, Action> check)
    {
        RunConnections(check);
        check("Developer API is opt-in and unrelated settings saves preserve its choice", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-local-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "options.json"); var options = new AdvisorOptionsStore(path);
                Require(!options.Current.DeveloperApiEnabled && !File.Exists(path), "Developer listener was implicitly enabled");
                options.SaveDeveloper(true, () => { }); options.EnsureUserName("司令官");
                options.SaveGeneral(AdvisorTextSize.Large, true, "Captain");
                options.SaveConsultation(3, 2, "Test"); options.SaveInformation(AdvisorInformation.Default);
                Require(new AdvisorOptionsStore(path).Current.DeveloperApiEnabled, "Another category disabled the API");
                var before = File.ReadAllBytes(path);
                try { options.SaveDeveloper(false, () => throw new IOException("test write failure")); }
                catch (IOException) { }
                Require(options.Current.DeveloperApiEnabled && File.ReadAllBytes(path).SequenceEqual(before), "Partial developer save was published");
                options.SaveDeveloper(false, () => { });
                Require(!new AdvisorOptionsStore(path).Current.DeveloperApiEnabled, "Disabling was not saved");
            }
            finally { Directory.Delete(directory, true); }
        });
        check("Local commands run only on the pumping thread, reject duplicate work and retain completed diagnostics", () =>
        {
            var target = new Target(); var api = Api(); var owner = new object();
            api.Attach(owner, "tactical_battle", () => target);
            Require(api.Enqueue("GET", "/v1/status", new()).GetAwaiter().GetResult().Status == 503, "Disabled API accepted a command");
            api.Enable();
            var task = Task.Run(() => api.Enqueue("POST", "/v1/consultations", new JObject { ["question"] = "残弾と射程は？" }));
            SpinWait.SpinUntil(() => target.Called || task.IsCompleted, 30);
            Require(!target.Called && !task.IsCompleted, "HTTP worker invoked the game target");
            while (!task.IsCompleted) { api.Pump(); Thread.Yield(); }
            var accepted = task.GetAwaiter().GetResult();
            Require(accepted.Status == 202 && !target.Run!.AllowFallback && (bool?)accepted.Data["allow_fallback"] == false && target.Thread == Environment.CurrentManagedThreadId, "Request did not run on the pumping thread");
            var id = (string)accepted.Data["id"]!;
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "second" }).Status == 409, "A simultaneous consultation was accepted");
            Require(Send(api, "POST", "/v1/conversation/clear").Status == 409, "Busy history was silently destroyed");
            var run = target.Run!;
            var session = run.Observe(new Knowledge());
            var request = new JObject { ["query"] = "get_unit_capabilities" };
            session.Execute(request); request["query"] = "modified after return";
            run.CaptureInput(new AdvisorProviderInput(new JObject { ["own_units"] = 1 }, "effective instructions", new JObject()));
            target.Turn!.UpdateProgress(new AdvisorProgress(AdvisorStage.FinalAnswer, 2, 1, 5, new[] { "get_unit_capabilities" }, true, "test-model", "Sub"));
            run.Complete(new AdviceResult("**Act now**", 2, 1, 123, model: "test-model"), "Sub");
            target.Turn.Complete("**Act now**"); run.Finish(); target.Busy = false;
            var result = Send(api, "GET", "/v1/consultations/" + id).Data;
            Require((string?)result["state"] == "answered" && (string?)result["answer"] == "**Act now**" &&
                (string?)result["result"]!["connection_name"] == "Sub" && (long?)result["result"]!["reported_total_tokens"] == 123 &&
                (string?)result["retrievals"]![0]!["request"]!["query"] == "get_unit_capabilities" &&
                (int?)result["retrievals"]![0]!["response"]!["remaining"] == 20, "Result or evidence was lost or aliased");
            result["retrievals"]![0]!["response"]!["remaining"] = 0;
            Require((int?)Send(api, "GET", "/v1/consultations/" + id).Data["retrievals"]![0]!["response"]!["remaining"] == 20, "Client mutated retained evidence");
            Require(run.Attached, "An immediately completed or failed request lost its accepted identity");
            var failedTurn = new AdvisorTurn(new AdvisorQuestion("failure"), 0, "Michael", "default");
            var failed = new AdvisorLocalRun(); failed.Attach(failedTurn);
            failedTurn.Fail(AdvisorNotice.Connection(new AdvisorConnectionException("api_key_missing"))); failed.Finish();
            failedTurn.Retry(0, null, null);
            Require(failed.Attached && (string?)failed.Snapshot()["state"] == "failed" && (string?)failed.Snapshot()["error"]!["code"] == "api_key_missing", "A UI retry reopened the earlier failed API result");
            Require(Send(api, "POST", "/v1/conversation/clear").Status == 200 && target.Cleared, "Clear did not reach the same target");
            api.Detach(new object());
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["shortcut"] = "key_risks", ["allow_fallback"] = true }).Status == 202 && target.Shortcut == "key_risks" && target.Run!.AllowFallback, "Quick consultation or owner identity changed");
            var nextId = target.Run!.Id;
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "test", ["allow_fallback"] = "true" }).Status == 400,
                "A non-boolean fallback choice was accepted");
            Require(Send(api, "GET", "/v1/consultations/" + id).Status == 404, "Old diagnostic copies grew without a retention bound");
            Require(Send(api, "POST", "/v1/consultations/cancel").Status == 200 &&
                (string?)Send(api, "GET", "/v1/consultations/" + nextId).Data["state"] == "cancelled", "Cancellation remained pending");
            api.Detach(owner);
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "test" }).Status == 409, "Detached game target was reused");
            var pending = api.Enqueue("POST", "/v1/conversation/clear", new()); api.Disable();
            Require(pending.GetAwaiter().GetResult().Status == 503, "Disabling left queued commands pending");
        });
        check("Plain adviser is an explicit consultation choice and is retained without becoming a global setting", () =>
        {
            var target = new Target(); var api = Api();
            api.Attach(new object(), "tactical_battle", () => target); api.Enable();
            foreach (var invalid in new JToken[] { new JValue("true"), new JValue(1), JValue.CreateNull() })
            {
                var rejected = Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Question", ["plain_advisor"] = invalid });
                Require(rejected.Status == 400 && (string?)rejected.Data["error"] == "invalid_plain_advisor" && !target.Called,
                    "A malformed plain-adviser option reached the target");
            }
            var accepted = Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Question", ["plain_advisor"] = true });
            Require(accepted.Status == 202 && (bool?)accepted.Data["plain_advisor"] == true && target.Run!.PlainAdvisor &&
                target.Turn!.AdvisorName == AdvisorProfile.DefaultName, "The plain choice or its displayed identity was lost");
            var id = target.Run!.Id;
            Send(api, "POST", "/v1/consultations/cancel");
            Require((bool?)Send(api, "GET", "/v1/consultations/" + id).Data["plain_advisor"] == true,
                "Completed diagnostic results lost the requested adviser mode");
            accepted = Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Normal question" });
            Require(accepted.Status == 202 && (bool?)accepted.Data["plain_advisor"] == false && !target.Run!.PlainAdvisor &&
                target.Turn!.AdvisorName == "Michael", "A plain request changed the following request's default mode");
            api.Disable();
        });
        check("Local prompt patches merge atomically, preserve consultation snapshots and reset without writing files", () =>
        {
            var original = AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory); var overrides = new AdvisorPromptOverrides(original);
            var api = Api(overrides); api.Enable();
            var before = Directory.GetFiles(AdvisorPrompts.DefaultDirectory).ToDictionary(p => p, File.ReadAllBytes);
            var first = original.Fields[0].Id; var second = original.Fields[1].Id;
            Require(Send(api, "PATCH", "/v1/prompts", new JObject { ["values"] = new JObject { [first] = "SHORT_TEST" } }).Status == 200, "Prompt patch failed");
            var pinned = overrides.Current;
            Send(api, "PATCH", "/v1/prompts", new JObject { ["values"] = new JObject { [second] = "OTHER_TEST" } });
            Require(overrides.Current.Fields.Single(f => f.Id == first).Value == "SHORT_TEST" && pinned.Fields.Single(f => f.Id == second).Value == original.Fields[1].Value, "Patch replaced unrelated values or active snapshots");
            var editing = new Target { Editing = true }; var owner = new object(); api.Attach(owner, "tactical_battle", () => editing);
            Require(Send(api, "POST", "/v1/prompts/reset").Status == 409, "External reset discarded the open editor draft");
            api.Detach(owner);
            var edited = overrides.Current;
            Require(Send(api, "PATCH", "/v1/prompts", new JObject { ["values"] = new JObject { [first] = "bad partial edit", ["unknown"] = "x" } }).Status == 400 && ReferenceEquals(edited, overrides.Current), "Invalid patch partly applied");
            Send(api, "POST", "/v1/prompts/reset");
            Require(!overrides.HasOverrides && ReferenceEquals(original, overrides.Current) && before.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "Reset or temporary edits changed disk files");
            api.Disable();
        });
        check("Loopback HTTP requires a developer header, rejects browser origins and malformed input, and releases its port", () =>
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var endpoint = "http://127.0.0.1:" + port; var calls = 0;
            var server = new AdvisorLocalServer((method, path, body) => { calls++; return Task.FromResult(new AdvisorLocalResponse(200, new JObject { ["echo"] = body.DeepClone() })); }, message => throw new InvalidOperationException(message), endpoint);
            try
            {
                server.Start(); using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Require(client.GetAsync(endpoint + "/v1/status").GetAwaiter().GetResult().StatusCode == HttpStatusCode.Forbidden && calls == 0, "Header-free browser request reached the API");
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/v1/status");
                request.Headers.Add("X-Advisor-Developer", "1"); request.Headers.Add("Origin", "https://example.invalid");
                Require(client.Send(request).StatusCode == HttpStatusCode.Forbidden && calls == 0, "Browser origin reached the API");
                client.DefaultRequestHeaders.Add("X-Advisor-Developer", "1");
                Require(client.PostAsync(endpoint + "/v1/consultations", new StringContent("{\"question\":\"a\",\"question\":\"b\"}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult().StatusCode == HttpStatusCode.BadRequest && calls == 0, "Ambiguous JSON reached the target");
                var response = client.PostAsync(endpoint + "/v1/consultations", new StringContent("{\"question\":\"日本語\"}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                Require(response.IsSuccessStatusCode && calls == 1 && (string?)JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["echo"]!["question"] == "日本語", "HTTP changed the question");
            }
            finally { server.Dispose(); }
            using var replacement = new AdvisorLocalServer((m, p, b) => Task.FromResult(new AdvisorLocalResponse(200, new())), _ => { }, endpoint); replacement.Start();
        });
    }
    private static AdvisorLocalApi Api(AdvisorPromptOverrides? overrides = null) => new(
        () => overrides ?? new AdvisorPromptOverrides(AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory)), error => throw error,
        () => new JObject { ["default_connection_id"] = "test", ["connections"] = new JArray(new JObject { ["id"] = "test" }) },
        (_, fallback, _, _) => AdvisorServices.Prepare(new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock-local-key" }), allowFallback: fallback));
    private static AdvisorLocalResponse Send(AdvisorLocalApi api, string method, string path, JObject? body = null)
    { var task = api.Enqueue(method, path, body ?? new()); api.Pump(); return task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Target : IAdvisorLocalTarget
    {
        internal bool Called, Busy, Cleared, Editing;
        internal int Thread;
        internal string? Shortcut;
        internal AdvisorLocalRun? Run;
        internal AdvisorTurn? Turn;
        public JObject LocalStatus() => new() { ["busy"] = Busy, ["settings_open"] = Editing };
        public AdvisorLocalRun LocalConsult(string? question, string? shortcut, bool allowFallback, AdvisorBackendSetup execution, bool plainAdvisor)
        {
            if (Busy) throw new AdvisorLocalApiException(409, "consultation_in_progress");
            Called = Busy = true; Thread = Environment.CurrentManagedThreadId; Shortcut = shortcut;
            Turn = new AdvisorTurn(new AdvisorQuestion(question ?? shortcut!), 0, plainAdvisor ? AdvisorProfile.DefaultName : "Michael", "default");
            Run = new AdvisorLocalRun(allowFallback, plainAdvisor); Run.Attach(Turn); return Run;
        }
        public void LocalClear() { if (Busy) throw new AdvisorLocalApiException(409, "consultation_in_progress"); Cleared = true; }
        public void LocalCancel() { Turn!.Cancel(); Run!.Finish(); Busy = false; }
    }
    private sealed class Knowledge : IAdvisorQuerySession
    {
        public string Id => "sample";
        public int RoundsUsed => 1;
        public int MaxRounds => 5;
        public AdvisorPrompts Prompts => AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory);
        public JObject InitialInput() => new();
        public JObject Execute(JObject request) => new() { ["remaining"] = 20 };
    }
}
