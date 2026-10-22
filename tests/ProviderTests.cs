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
        RunRequestedSituation(check);
        RunLocalFallbackPolicy(check);
        RunLocalConnections(check);
        RunOllama(check);
        RunOpenRouter(check);
        RunNanoGpt(check);
        RunErrorHistory(check);
        RunRequestTimeouts(check);
        RunExtensions(check);
        RunOperations(check);
        RunOperationPermission(check);
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
                return Task.FromResult(new AdvisorReply("Shared answer", Array.Empty<AdvisorToolCall>(), 7));
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
                throw new AdvisorConnectionException("rate_limited", 429);
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
        check("The shared engine stops at the knowledge limit without another model request or fallback", () => Async(async () =>
        {
            var knowledge = new Knowledge(1);
            var provider = new FakeProvider("independent", (s, n, allowed, token) =>
            {
                Require(n == 1 && allowed, "A tools-disabled or extra request was sent");
                return Task.FromResult(Reply(Call("call-" + n, "catalog")));
            });
            var fallback = new FakeProvider("unused", (_, _, _, _) => throw new Exception("Limit triggered fallback"));
            var result = await new AdvisorEngine(provider, fallback).ConsultAsync(
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(!result.Completed && result.CompletionReason == AdvisorCompletionReason.RoundLimit && result.Text == "" &&
                result.TotalTokens == 7 && result.ApiRequests == 1 && knowledge.RoundsUsed == 1 &&
                provider.Sessions.Single().Sends == 1 && provider.Sessions.Single().Disposed && fallback.Sessions.Count == 0,
                "A limit was treated as success, retried, or lost its usage accounting");
        }));
        check("The shared request byte budget is checked before an independent provider sends any data", () => Async(async () =>
        {
            var provider = new FakeProvider("independent", (s, n, allowed, token) => throw new InvalidOperationException("Oversized request was sent"),
                AdvisorEngine.MaxTotalRequestBytes + 1);
            try { await new AdvisorEngine(provider).ConsultAsync(AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>()), CancellationToken.None); }
            catch (AdvisorConnectionException error)
            {
                Require(error.Code == "request_budget" && provider.Sessions.Single().Sends == 0 && provider.Sessions.Single().Disposed,
                    "The provider sent an oversized request or its session was not cleaned up");
                return;
            }
            throw new InvalidOperationException("Oversized input was accepted");
        }));
    }

    private static AdvisorToolCall Call(string id, string query) => new(id, ToolName,
        new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) });
    private static AdvisorReply Reply(params AdvisorToolCall[] calls) => new("", calls, 7);

    private class FakeProvider : IAdvisorProvider, IAdvisorLegacyToolProvider
    {
        public string Model { get; }
        public virtual bool UsesLegacyTools => false;
        private readonly Func<FakeSession, int, bool, CancellationToken, Task<AdvisorReply>> send;
        private readonly int bytes;
        internal List<FakeSession> Sessions { get; } = new();
        internal FakeProvider(string model, Func<FakeSession, int, bool, CancellationToken, Task<AdvisorReply>> send, int bytes = 100)
        { Model = model; this.send = send; this.bytes = bytes; }
        public IAdvisorProviderSession Start(AdvisorProviderInput input, AdvisorContinuation? continuation)
        {
            var session = new FakeSession(input, continuation, send, bytes);
            Sessions.Add(session); return session;
        }
    }

    private sealed class LegacyProvider : FakeProvider
    {
        public override bool UsesLegacyTools => true;
        internal LegacyProvider(string model, Func<FakeSession, int, bool, CancellationToken, Task<AdvisorReply>> send, int bytes = 100)
            : base(model, send, bytes) { }
    }

    private sealed class FakeSession : IAdvisorProviderSession
    {
        internal AdvisorProviderInput Input { get; }
        internal AdvisorContinuation? Continuation { get; }
        internal IReadOnlyList<AdvisorToolResult> Results { get; private set; } = Array.Empty<AdvisorToolResult>();
        internal int Sends { get; private set; }
        internal bool Disposed { get; private set; }
        private readonly Func<FakeSession, int, bool, CancellationToken, Task<AdvisorReply>> send;
        private readonly int bytes;
        internal FakeSession(AdvisorProviderInput input, AdvisorContinuation? continuation,
            Func<FakeSession, int, bool, CancellationToken, Task<AdvisorReply>> send, int bytes)
        { Input = input; Continuation = continuation; this.send = send; this.bytes = bytes; }
        public AdvisorProviderRequest Prepare(bool allowTools) => new(bytes, cancellation => send(this, ++Sends, allowTools, cancellation));
        public void AcceptResults(IReadOnlyList<AdvisorToolResult> results) => Results = results;
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
            ["friendly_units"] = new JArray(), ["observed_contacts"] = new JArray(), ["public_objectives"] = new JArray(),
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
