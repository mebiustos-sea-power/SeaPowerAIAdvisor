using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Optional standard retrieval strategy. The host owns deadlines; providers own API formats and conversation state.
internal sealed class AdvisorEngine : IAdvisorBackend
{
    internal const int MaxTotalRequestBytes = 1_000_000;
    internal const int MaxToolCallsPerResponse = 40;
    private readonly IAdvisorProvider main;
    private readonly IAdvisorProvider? sub;
    private readonly Action<AdvisorConnectionException>? switched;
    private readonly AdvisorContinuation? continuation;
    internal AdvisorEngine(IAdvisorProvider main, IAdvisorProvider? sub = null, Action<AdvisorConnectionException>? switched = null,
        AdvisorContinuation? continuation = null)
    { this.main = main; this.sub = sub; this.switched = switched; this.continuation = continuation; }

    public Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation) => RunAsync(context, cancellation);

    private async Task<AdviceResult> RunAsync(AdvisorContext context, CancellationToken cancellation)
    {
        var session = context.Knowledge;
        var tool = context.Input.Tool;
        var maxExchanges = Math.Max(0, session.MaxRounds - (continuation == null ? 0 : session.RoundsUsed));
        var bytesSent = 0;
        long totalTokens = 0;
        var usageKnown = true;
        AdvisorCompletionReason? limit = null;
        var queriesUsed = new List<string>();
        var lastQueries = Array.Empty<string>();
        var lastApiRequest = 0;
        var retrievals = continuation == null ? new JArray() : (JArray)continuation.Retrievals.DeepClone();
        using var connection = new AdvisorConnection(main, sub, context.Input, error =>
        {
            usageKnown = false; // A lost response may have consumed tokens that we cannot measure.
            switched?.Invoke(error);
            context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.SwitchingKey, lastApiRequest, session.RoundsUsed,
                session.MaxRounds, lastQueries, true, sub!.Model));
        }, continuation);
        var knownQueries = ((JArray)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!).Values<string>().ToHashSet(StringComparer.Ordinal);
        for (var api = 0; api < maxExchanges; api++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (limit.HasValue || session.RoundsUsed >= session.MaxRounds)
                return Result("", limit ?? AdvisorCompletionReason.RoundLimit);
            var reply = await connection.GenerateAsync(true, new AdvisorContinuation(retrievals, session.RoundsUsed, session.MaxRounds, context.Operations?.History), cancellation,
                (requestNumber, fallback, model, size) =>
                {
                    if (size > MaxTotalRequestBytes - bytesSent) throw new AdvisorConnectionException("request_budget");
                    bytesSent += size;
                    lastApiRequest = requestNumber;
                    context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.Waiting,
                        requestNumber, session.RoundsUsed, session.MaxRounds, lastQueries, fallback, model));
                }).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (reply.TotalTokens.HasValue) totalTokens += reply.TotalTokens.Value;
            else usageKnown = false;
            var calls = reply.Calls;
            if (calls.Count == 0)
            {
                context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.FinalAnswer, connection.ApiRequests, session.RoundsUsed,
                    session.MaxRounds, lastQueries, connection.UsingFallback, connection.Model));
                return Result(reply.Text, AdvisorCompletionReason.Completed);
            }
            if (calls.Count > MaxToolCallsPerResponse) throw new AdvisorConnectionException("tool_call_limit");
            if (calls.Any(call => string.IsNullOrWhiteSpace(call.Id) || (call.Name != (string?)tool["name"] && (context.Operations == null || call.Name != (string?)context.Input.OperationTool?["name"]))) ||
                calls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count)
                throw new AdvisorConnectionException("invalid_function_call");
            var queryCalls = calls.Where(c => c.Name == (string?)tool["name"]).ToArray();
            var results = new Dictionary<string, AdvisorToolResult>(StringComparer.Ordinal);
            lastQueries = Array.Empty<string>();
            if (queryCalls.Length > 0)
            {
                var combined = Combine(session, queryCalls);
                var pendingQueries = (combined["requests"] as JArray ?? new JArray()).OfType<JObject>().Select(q =>
                    q["query"]?.Type == JTokenType.String && knownQueries.Contains((string?)q["query"]) ? (string)q["query"]! : "unknown_query").ToArray();
                context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.Retrieving, connection.ApiRequests, session.RoundsUsed + 1,
                    session.MaxRounds, pendingQueries, connection.UsingFallback, connection.Model));
                var result = session.Execute(combined);
                retrievals.Add(new JObject { ["request"] = combined.DeepClone(), ["response"] = result.DeepClone() });
                lastQueries = (string?)result["status"] == "ok" ? pendingQueries : Array.Empty<string>();
                queriesUsed.AddRange(pendingQueries);
                foreach (var resultItem in SplitResults(queryCalls, result)) results.Add(resultItem.Id, resultItem);
                var errorCode = (string?)result["error"]?["code"];
                if (errorCode == "payload_limit") limit = AdvisorCompletionReason.PayloadLimit;
                else if (errorCode == "round_limit") limit = AdvisorCompletionReason.RoundLimit;
            }
            foreach (var call in calls.Where(c => c.Name != (string?)tool["name"]))
            {
                context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.Retrieving, connection.ApiRequests, session.RoundsUsed,
                    session.MaxRounds, new[] { AdvisorOperationGateway.ToolName }, connection.UsingFallback, connection.Model));
                var result = await context.Operations!.ExecuteAsync(call.Arguments, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                results.Add(call.Id, new AdvisorToolResult(call, result));
                queriesUsed.Add(AdvisorOperationGateway.ToolName);
                lastQueries = lastQueries.Concat(new[] { AdvisorOperationGateway.ToolName }).ToArray();
            }
            connection.AcceptResults(calls.Select(c => results[c.Id]).ToArray());
        }
        cancellation.ThrowIfCancellationRequested();
        return Result("", limit ?? AdvisorCompletionReason.RoundLimit);

        AdviceResult Result(string answer, AdvisorCompletionReason reason) => new(answer, connection.ApiRequests,
            session.RoundsUsed, usageKnown ? totalTokens : (long?)null, queriesUsed, connection.Model, reason);
    }

    private static IReadOnlyList<AdvisorToolResult> SplitResults(IReadOnlyList<AdvisorToolCall> calls, JObject result)
    {
        var replies = new List<AdvisorToolResult>();
        var offset = 0;
        foreach (var call in calls)
        {
            var reply = (JObject)result.DeepClone();
            if (result["results"] is JArray results)
            {
                var length = ((JArray)call.Arguments["requests"]!).Count;
                reply["results"] = new JArray(results.Skip(offset).Take(length).Select(r => r.DeepClone()));
                offset += length;
            }
            replies.Add(new AdvisorToolResult(call, reply));
        }
        return replies;
    }

    private static JObject Combine(IAdvisorQuerySession session, IReadOnlyList<AdvisorToolCall> calls)
    {
        var requests = new JArray();
        foreach (var call in calls)
        {
            var args = call.Arguments;
            // The host owns consultation identity. Malformed requests consume a bounded round through the knowledge gateway.
            if (args.Properties().Any(p => p.Name != "requests"))
                return new JObject { ["consultation_id"] = session.Id, ["unsupported_tool_argument"] = true };
            if (args["requests"] is not JArray batch || batch.Count == 0)
                return new JObject { ["consultation_id"] = session.Id, ["requests"] = args["requests"]?.DeepClone() };
            foreach (var request in batch) requests.Add(request.DeepClone());
        }
        return new JObject { ["consultation_id"] = session.Id, ["requests"] = requests };
    }
}
