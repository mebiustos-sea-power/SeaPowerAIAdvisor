using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunBackends(Action<string, Action> check)
    {
        check("A selected custom backend uses its own workflow while the host retains disclosure and retrieval limits", () => Async(async () =>
        {
            var registry = new AdvisorBackendRegistry();
            registry.Register("unused", () => throw new Exception("Unselected provider configuration was loaded"));
            var knowledge = new Knowledge(2, AdvisorInformation.Default.With(InformationKind.BattleBriefing, false));
            var events = new List<AdvisorProgress>();
            var backend = new CustomBackend(async (context, token) =>
            {
                Require(context.Input.Data["user_name"]!.Value<string>() == "Commander" && context.Input.Data["prior_dialogue"]!.Count() == 1,
                    "The custom workflow lost common consultation inputs");
                var denied = Read(context, "get_briefing");
                Require((string?)denied["error"]?["code"] == "information_disabled", "A custom backend bypassed the user's data selection");
                Require((string?)Read(context, "catalog")["status"] == "ok" &&
                    (string?)Read(context, "catalog")["error"]?["code"] == "round_limit", "A custom backend bypassed host retrieval limits");
                for (var step = 1; step <= 4; step++)
                {
                    token.ThrowIfCancellationRequested();
                    context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.Waiting, step, knowledge.RoundsUsed, knowledge.MaxRounds, model: "custom-model"));
                    await Task.Yield();
                }
                return new AdviceResult("Custom answer", 4, 999, null, new[] { "catalog" }, "custom-model");
            });
            registry.Register("custom", () => Setup(backend)); registry.Select("custom");
            var setup = registry.Prepare();
            var result = await AdvisorConsultation.RunAsync(setup,
                AdvisorContext.Create(knowledge, new[] { new ChatExchange("Earlier", "Answer") }, progress: events.Add, userName: "Commander"),
                "unused-custom-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(result.Completed && result.Text == "Custom answer" && result.ApiRequests == 4 && result.QueryRounds == 2 && result.TotalTokens == null &&
                backend.Disposed && events.Count == 4 && events.Last().Model == "custom-model",
                "The host imposed standard API rounds, trusted fabricated retrieval counts, lost progress or leaked the backend");
        }));
        check("Backend selection affects the next consultation without replacing an already prepared execution", () => Async(async () =>
        {
            var registry = new AdvisorBackendRegistry();
            registry.Register("first", () => Setup(new CustomBackend((_, _) => Task.FromResult(Answer("first")))));
            registry.Register("second", () => Setup(new CustomBackend((_, _) => Task.FromResult(Answer("second")))));
            registry.Select("first"); var prepared = registry.Prepare();
            registry.Select("second");
            try { registry.Register("second", () => throw new Exception()); throw new Exception("Duplicate registration was accepted"); }
            catch (InvalidOperationException) { }
            try { registry.Select("missing"); throw new Exception("Missing backend selection was accepted"); }
            catch (KeyNotFoundException) { }
            var before = await RunPrepared(prepared);
            var after = await RunPrepared(registry.Prepare());
            Require(before.Text == "first" && after.Text == "second" && registry.SelectedId == "second",
                "Selection changed in-flight configuration or invalid registration silently changed the selected backend");
        }));
        check("Selected backend configuration and execution errors remain visible without falling back to another implementation", () => Async(async () =>
        {
            var registry = new AdvisorBackendRegistry();
            var configured = false;
            registry.Register("default", () => { configured = true; return Setup(new CustomBackend((_, _) => Task.FromResult(Answer("wrong")))); });
            registry.Register("broken", () => throw new InvalidOperationException("backend configuration failed"));
            registry.Select("broken");
            try { _ = registry.Prepare(); throw new Exception("Configuration failure was hidden"); }
            catch (InvalidOperationException e) { Require(e.Message == "backend configuration failed", "Original configuration failure was replaced"); }
            var backend = new CustomBackend((_, _) => throw new InvalidOperationException("backend execution failed"));
            registry.Register("execution", () => Setup(backend)); registry.Select("execution");
            try { await RunPrepared(registry.Prepare()); throw new Exception("Execution failure was hidden"); }
            catch (InvalidOperationException e) { Require(e.Message == "backend execution failed", "Original execution failure was replaced"); }
            Require(!configured && backend.Disposed, "A failing backend triggered an unrelated implementation or leaked its scope");
        }));
        check("User cancellation reaches independent backends and releases their resources without a global deadline", () => Async(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new CustomBackend(async (_, token) =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new Exception("Cancelled backend continued");
            });
            var task = RunPrepared(Setup(backend), cancellation.Token);
            await started.Task; cancellation.Cancel();
            try { await task; throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Require(backend.Disposed, "Cancelled backend resources remained open");
        }));
        check("Independent backends share outcome logging and secret redaction without Gemini settings or protocol metadata", () => Async(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-backend-" + Guid.NewGuid().ToString("N"));
            const string secret = "private_custom_backend_credential";
            try
            {
                var setup = new AdvisorBackendSetup(new AdvisorTraceOptions(true, "custom-wire", "custom-model", secrets: new[] { secret }), trace =>
                    new AdvisorBackendHandle(new CustomBackend((_, _) =>
                    {
                        trace!.Request(1, new JObject { ["echo"] = secret }.ToString(), model: "custom-model");
                        trace.Response(1, 200, "{}", true, 2);
                        return Task.FromResult(Answer("Custom answer"));
                    })));
                await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>()),
                    directory, new AdvisorQuestion("Question"), CancellationToken.None);
                var raw = File.ReadAllText(Path.Combine(directory, "ai-communication.jsonl"));
                var records = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();
                Require(!raw.Contains(secret, StringComparison.Ordinal) && (string?)records[0]["api"] == "custom-wire" &&
                    (string?)records.Last()["outcome"] == "completed" && records.Length == 4,
                    "Custom backend diagnostics leaked secrets, assumed Gemini, or omitted consultation completion");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }));
        check("The registered Gemini backend preserves Interactions and snapshots settings separately for successive consultations", () => Async(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-registered-gemini-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "mock-settings.json");
                var models = new List<string>(); var handlers = new List<BackendHttpHandler>();
                void Settings(string model) => File.WriteAllText(path, new JObject { ["api_key"] = "mock_backend_key", ["model"] = model }.ToString());
                Settings("gemini-first-model");
                var registry = AdvisorServices.CreateRegistry(path, transportFactory: trace =>
                {
                    var handler = new BackendHttpHandler(async (request, token) =>
                    {
                        Require(request.RequestUri!.AbsolutePath == "/v1beta/interactions", "Registration changed the Gemini protocol");
                        models.Add((string)JObject.Parse(await request.Content!.ReadAsStringAsync(token))["model"]!);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InteractionFixture.Reply(new JObject { ["text"] = "Gemini answer" }).ToString()) };
                    });
                    handlers.Add(handler); return new AiHttpTransport(handler, trace);
                });
                var first = registry.Prepare();
                Settings("gemini-second-model");
                var second = registry.Prepare();
                Require((await RunPrepared(first)).Text == "Gemini answer" && (await RunPrepared(second)).Text == "Gemini answer" &&
                    models.SequenceEqual(new[] { "gemini-first-model", "gemini-second-model" }) && handlers.All(h => h.Disposed),
                    "Gemini configuration became stale, changed mid-flight, or leaked its HTTP handler");
            }
            finally { Directory.Delete(directory, true); }
        }));
    }

    private static JObject Read(AdvisorContext context, string query) => context.Knowledge.Execute(new JObject
    { ["consultation_id"] = context.Knowledge.Id, ["requests"] = new JArray(new JObject { ["query"] = query }) });
    private static AdviceResult Answer(string text) => new(text, 1, 0, null, model: "custom-model");
    private static AdvisorBackendSetup Setup(CustomBackend backend) =>
        new(new AdvisorTraceOptions(false, "custom-wire", "custom-model"), _ => new AdvisorBackendHandle(backend));
    private static Task<AdviceResult> RunPrepared(AdvisorBackendSetup setup, CancellationToken cancellation = default) =>
        AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>()),
            "unused-backend-log", new AdvisorQuestion("Question"), cancellation);
    private sealed class CustomBackend : IAdvisorBackend, IDisposable
    {
        private readonly Func<AdvisorContext, CancellationToken, Task<AdviceResult>> run;
        internal bool Disposed { get; private set; }
        internal CustomBackend(Func<AdvisorContext, CancellationToken, Task<AdviceResult>> run) { this.run = run; }
        public Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation) => run(context, cancellation);
        public void Dispose() => Disposed = true;
    }
    private sealed class BackendHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal bool Disposed { get; private set; }
        internal BackendHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation);
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
