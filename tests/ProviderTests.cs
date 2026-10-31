using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private const string ToolName = "query_situation";
    internal static void Run(Action<string, Action> check)
    {
        RunBackends(check);
        RunUnifiedReads(check);
        RunSplitTools(check);
        RunSituationDelivery(check);
        RunConnectionPrompts(check);
        RunRequestedSituation(check);
        RunLocalFallbackPolicy(check);
        RunLocalConnections(check);
        RunOllama(check);
        RunOpenRouter(check);
        RunNanoGpt(check);
        RunErrorHistory(check);
        RunRequestTimeouts(check);
        RunOutputTokens(check);
        RunExtensions(check);
        RunOperations(check);
        RunOperationPermission(check);
        RunShortcutDelivery(check);
        RunCompletion(check);
        check("A provider without Gemini wire types uses the shared engine and receives correctly mapped parallel results", () => Async(async () =>
        {
            var knowledge = new Knowledge(2);
            var context = AdvisorContext.Create(knowledge, new[] { new ChatExchange("Earlier", "Answer") }, userName: "Commander");
            var provider = new FakeProvider("local-model", (s, n, allowed, token) =>
            {
                if (n == 1)
                {
                    Require((string?)s.Input.Data["user_name"] == "Commander" && s.Input.Data["prior_dialogue"]!.Count() == 1,
                        "Provider-independent consultation input lost user preferences or history");
                    s.Input.Data["question"] = "Provider-specific representation";
                    return Task.FromResult(Reply(Call("briefing", "get_briefing"), Call("catalog", "catalog")));
                }
                Require(s.Results.Count == 2 && s.Results[0].Id == "briefing" && s.Results[1].Id == "catalog" &&
                    (string?)s.Results[0].Data["results"]![0]!["query"] == "get_briefing" &&
                    (string?)s.Results[1].Data["results"]![0]!["query"] == "catalog", "Parallel tool results lost their identity or were mixed");
                return Task.FromResult(new AiReply("Shared answer", Array.Empty<AiToolCall>(), 7));
            });
            IAdvisorBackend backend = new AdvisorEngine(provider);
            var answer = await AdvisorConsultation.RunAsync(backend, context, CancellationToken.None);
            Require(answer.Text == "Shared answer" && answer.Model == "local-model" && answer.ApiRequests == 2 &&
                answer.QueryRounds == 1 && answer.TotalTokens == 14 && answer.Queries.SequenceEqual(new[] { "get_briefing", "catalog" }),
                "Shared execution depended on a Gemini response or changed result accounting");
            Require((string?)context.Input.Data["question"] == "Question" && knowledge.Requests.Count == 1 &&
                (string?)knowledge.Requests[0]["consultation_id"] == knowledge.Id && provider.Sessions.Single().Disposed,
                "The provider changed host input, selected another consultation, or leaked its session");
        }));
        check("Switching independent providers transfers only host evidence and keeps the same query budget", () => Async(async () =>
        {
            var knowledge = new Knowledge(2);
            var events = new List<AdvisorProgress>();
            var main = new FakeProvider("main-model", (s, n, allowed, token) =>
            {
                if (n == 1)
                {
                    s.Input.Data["question"] = "PRIVATE_PROVIDER_STATE";
                    return Task.FromResult(Reply(Call("first", "catalog")));
                }
                throw new AiConnectionException("rate_limited", 429);
            });
            var sub = new FakeProvider("other-provider-model", (s, n, allowed, token) =>
            {
                Require((string?)s.Input.Data["question"] == "Question" && s.Continuation != null &&
                    s.Continuation.Retrievals.Count == 1 && s.Continuation.RoundsUsed == 1 && s.Continuation.MaxRounds == 2 &&
                    (string?)s.Continuation.Retrievals[0]!["response"]!["status"] == "ok" &&
                    !s.Input.Data.ToString().Contains("PRIVATE_PROVIDER_STATE", StringComparison.Ordinal),
                    "Handoff lost evidence, reset query limits, or copied private provider state");
                Require(n == 1 && allowed, "The fallback provider was called after the shared limit");
                return Task.FromResult(Reply(Call("next", "get_briefing")));
            });
            var result = await new AdvisorEngine(main, sub).ConsultAsync(AdvisorContext.Create(knowledge,
                Array.Empty<ChatExchange>(), progress: events.Add), CancellationToken.None);
            Require(!result.Completed && result.CompletionReason == AdvisorCompletionReason.RoundLimit && result.ApiRequests == 3 &&
                result.QueryRounds == 2 && result.Model == sub.Model && result.TotalTokens == null &&
                events.Count(e => e.Stage == AdvisorStage.SwitchingKey) == 1 && sub.Sessions.Single().Sends == 1 &&
                main.Sessions.Single().Disposed && sub.Sessions.Single().Disposed,
                "Cross-provider fallback changed actual attempts, usage uncertainty, progress, or session cleanup");
        }));
        check("Large cumulative requests still stop at the configured consultation limit without fallback", () => Async(async () =>
        {
            var knowledge = new Knowledge(3);
            var provider = new FakeProvider("independent", (s, n, allowed, token) =>
            {
                Require(n <= 3 && allowed, "A tools-disabled or extra request was sent");
                return Task.FromResult(Reply(Call("call-" + n, "catalog")));
            }, bytes: 600_000);
            var fallback = new FakeProvider("unused", (_, _, _, _) => throw new Exception("Limit triggered fallback"));
            var result = await new AdvisorEngine(provider, fallback).ConsultAsync(
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(!result.Completed && result.CompletionReason == AdvisorCompletionReason.RoundLimit && result.Text == "" &&
                result.TotalTokens == 21 && result.ApiRequests == 3 && knowledge.RoundsUsed == 3 &&
                provider.Sessions.Single().Sends == 3 && provider.Sessions.Single().Disposed && fallback.Sessions.Count == 0,
                "A limit was treated as success, retried, or lost its usage accounting");
        }));
        check("Standard consultations can complete after requests exceeding one megabyte individually and cumulatively", () => Async(async () =>
        {
            var provider = new FakeProvider("independent", (s, n, allowed, token) =>
            {
                Require(n <= 3 && allowed, "The consultation sent an extra request or disabled tools");
                return Task.FromResult(n < 3 ? Reply(Call("call-" + n, "catalog")) :
                    new AiReply("Completed answer", Array.Empty<AiToolCall>(), 7));
            }, bytes: 1_000_001);
            var result = await new AdvisorEngine(provider).ConsultAsync(
                AdvisorContext.Create(new Knowledge(3), Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(result.Completed && result.Text == "Completed answer" && result.ApiRequests == 3 && result.QueryRounds == 2 &&
                result.TotalTokens == 21 && provider.Sessions.Single().Sends == 3 && provider.Sessions.Single().Disposed,
                "Request size prevented completion or changed accounting and session cleanup");
        }));
    }

    private static AiToolCall Call(string id, string query) => new(id, ToolName,
        new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) });
    private static AiReply Reply(params AiToolCall[] calls) => new("", calls, 7);

    private sealed class FakeProvider : IAiProvider
    {
        public string Model { get; }
        private readonly Func<FakeSession, int, bool, CancellationToken, Task<AiReply>> send;
        private readonly int bytes;
        internal List<FakeSession> Sessions { get; } = new();
        internal FakeProvider(string model, Func<FakeSession, int, bool, CancellationToken, Task<AiReply>> send, int bytes = 100)
        { Model = model; this.send = send; this.bytes = bytes; }
        public IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation)
        {
            var session = new FakeSession(input, continuation, send, bytes);
            Sessions.Add(session); return session;
        }
    }

    private sealed class FakeSession : IAiProviderSession
    {
        internal AiProviderInput Input { get; }
        internal AiContinuation? Continuation { get; }
        internal IReadOnlyList<AiToolResult> Results { get; private set; } = Array.Empty<AiToolResult>();
        internal int Sends { get; private set; }
        internal bool Disposed { get; private set; }
        private readonly Func<FakeSession, int, bool, CancellationToken, Task<AiReply>> send;
        private readonly int bytes;
        internal FakeSession(AiProviderInput input, AiContinuation? continuation,
            Func<FakeSession, int, bool, CancellationToken, Task<AiReply>> send, int bytes)
        { Input = input; Continuation = continuation; this.send = send; this.bytes = bytes; }
        public AiProviderRequest Prepare(bool allowTools) => new(bytes, cancellation => send(this, ++Sends, allowTools, cancellation));
        public void AcceptResults(IReadOnlyList<AiToolResult> results) => Results = results;
        public void Dispose() => Disposed = true;
    }

    // The real battle gateway still owns validation, snapshot identity, and round accounting.
    private sealed class Knowledge : IAdvisorQuerySession
    {
        private readonly BattleQuerySession inner;
        internal List<JObject> Requests { get; } = new();
        public string Id => inner.Id;
        public int RoundsUsed => inner.RoundsUsed;
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        internal Knowledge(int maxRounds, AdvisorInformation? information = null) => inner = new BattleQuerySession(new BattleSnapshot(new JObject
        {
            ["captured_at_utc"] = "1985-06-01T00:00:00Z", ["mission_display_name"] = "Mission", ["mission_briefing"] = "Public briefing",
            ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(), ["public_objectives"] = new JArray(), ["mission_map_areas"] = new JArray(),
            ["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
            ["unit_capabilities"] = new JArray(), ["contact_threat_references"] = new JArray(),
            ["weapon_references"] = new JArray(), ["published_reports"] = new JArray()
        }), "ja", "Question", maxRounds: maxRounds, information: information);
        public JObject InitialInput()
        {
            var value = inner.InitialInput(); value["query_tool"]!["name"] = ToolName; return value;
        }
        public JObject Execute(JObject request) { Requests.Add((JObject)request.DeepClone()); return inner.Execute(request); }
    }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
