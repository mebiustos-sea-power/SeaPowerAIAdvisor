using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunCompletion(Action<string, Action> check)
    {
        check("Operation-only exchanges exhaust the API budget without a forced final response or fallback", () =>
        {
            var registry = SonobuoyRegistry(new OperationHelicopter());
            using var gateway = new AdvisorOperationGateway(registry);
            var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Plan"));
            using var operations = gateway.CreateSession(turn.OperationId, AdvisorPrompts.Current)!;
            var provider = new FakeProvider("limited", (_, n, allowed, _) =>
            {
                Require(n <= 2 && allowed, "An extra tools-disabled response was requested");
                return Task.FromResult(OperationReply("list-" + n, new JObject { ["action"] = "list" }));
            });
            var fallback = new FakeProvider("unused", (_, _, _, _) => throw new Exception("Limit triggered fallback"));
            var context = AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>(), operations: operations);
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => AdvisorConsultation.RunAsync(
                new AdvisorEngine(provider, fallback), context, CancellationToken.None)));
            turn.Finish(result, registry);
            Require(!result.Completed && result.CompletionReason == AdvisorCompletionReason.RoundLimit && result.QueryRounds == 0 &&
                result.ApiRequests == 2 && result.TotalTokens == 14 && operations.History.Count == 2 &&
                turn.State == AdvisorTurnState.Incomplete && fallback.Sessions.Count == 0,
                "Operation exchanges bypassed the request budget or completion propagation lost the limit");
        });
        check("An answer on the final allowed exchange completes normally and diagnostics distinguish limits", () => Async(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-completion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                foreach (var completes in new[] { true, false })
                {
                    var provider = new FakeProvider("limited", (_, n, allowed, _) =>
                    {
                        Require(allowed, "The model was forced to answer without tools");
                        return Task.FromResult(n == 2 && completes ? new AdvisorReply("Final answer", Array.Empty<AdvisorToolCall>(), 7)
                            : Reply(Call("query-" + n, "catalog")));
                    });
                    var setup = new AdvisorBackendSetup(new AdvisorTraceOptions(true, "mock", "limited"),
                        _ => new AdvisorBackendHandle(new AdvisorEngine(provider)));
                    var result = await AdvisorConsultation.RunAsync(setup, AdvisorContext.Create(new Knowledge(2), Array.Empty<ChatExchange>()),
                        directory, new AdvisorQuestion("Question"), CancellationToken.None);
                    var record = JObject.Parse(File.ReadAllLines(Path.Combine(directory, "ai-communication.jsonl")).Last());
                    Require(result.Completed == completes && result.ApiRequests == 2 && result.TotalTokens == 14 &&
                        result.Text == (completes ? "Final answer" : "") && (string?)record["outcome"] == (completes ? "completed" : "incomplete") &&
                        (string?)record["completion_reason"] == result.CompletionCode && (int?)record["api_requests"] == 2,
                        "The last allowed answer or incomplete usage was misclassified in the host or trace");
                }
            }
            finally { Directory.Delete(directory, true); }
        }));
        check("An information payload limit ends incomplete without converting it to a round limit", () => Async(async () =>
        {
            var knowledge = new PayloadLimitedKnowledge();
            var provider = new FakeProvider("limited", (_, n, _, _) =>
            {
                Require(n == 1, "The engine requested another response after the payload limit");
                return Task.FromResult(Reply(Call("query", "catalog")));
            });
            var result = await AdvisorConsultation.RunAsync(new AdvisorEngine(provider),
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(!result.Completed && result.CompletionReason == AdvisorCompletionReason.PayloadLimit && result.ApiRequests == 1 &&
                result.QueryRounds == 1 && result.TotalTokens == 7, "The payload exhaustion was retried or labeled as another outcome");
        }));
    }

    private sealed class PayloadLimitedKnowledge : IAdvisorQuerySession
    {
        private readonly Knowledge inner = new(5);
        public string Id => inner.Id;
        public int RoundsUsed { get; private set; }
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        public JObject InitialInput() => inner.InitialInput();
        public JObject Execute(JObject request)
        {
            RoundsUsed++;
            return new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = "payload_limit" } };
        }
    }
}
