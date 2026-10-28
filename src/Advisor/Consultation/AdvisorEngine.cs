using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Optional standard retrieval strategy. The host owns deadlines; providers own API formats and conversation state.
internal sealed class AdvisorEngine : IAdvisorBackend
{
    internal const int MaxTotalRequestBytes = 1_000_000;
    internal const int MaxToolCallsPerResponse = AdvisorToolSession.MaxCallsPerResponse;
    private readonly IAiProvider main;
    private readonly IAiProvider? sub;
    private readonly Action<AiConnectionException>? switched;
    private readonly AiContinuation? continuation;
    private readonly AdvisorSituationDelivery delivery, fallbackDelivery;
    private readonly string connectionPrompt, fallbackPrompt;
    internal AdvisorEngine(IAiProvider main, IAiProvider? sub = null, Action<AiConnectionException>? switched = null,
        AiContinuation? continuation = null, AdvisorSituationDelivery delivery = AdvisorSituationDelivery.OnDemand,
        AdvisorSituationDelivery? fallbackDelivery = null, string connectionPrompt = "", string fallbackPrompt = "")
    { this.main = main; this.sub = sub; this.switched = switched; this.continuation = continuation;
        this.delivery = delivery; this.fallbackDelivery = fallbackDelivery ?? delivery;
        this.connectionPrompt = connectionPrompt; this.fallbackPrompt = fallbackPrompt; }

    public Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation) => RunAsync(context, cancellation);

    private async Task<AdviceResult> RunAsync(AdvisorContext context, CancellationToken cancellation)
    {
        var session = context.Knowledge;
        var tools = context.Tools;
        var maxExchanges = Math.Max(0, session.MaxRounds - (continuation == null ? 0 : session.RoundsUsed));
        var bytesSent = 0;
        long totalTokens = 0;
        var usageKnown = true;
        AdvisorCompletionReason? limit = null;
        var queriesUsed = new List<string>();
        var lastQueries = Array.Empty<string>();
        var lastApiRequest = 0;
        var retrievals = continuation == null ? new JArray() : (JArray)continuation.Retrievals.DeepClone();
        using var connection = new AiConnection(main, sub, fallback => context.PrepareStandardInput(fallback ? fallbackDelivery : delivery, fallback ? fallbackPrompt : connectionPrompt), error =>
        {
            usageKnown = false; // A lost response may have consumed tokens that we cannot measure.
            switched?.Invoke(error);
            context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.SwitchingKey, lastApiRequest, session.RoundsUsed,
                session.MaxRounds, lastQueries, true, sub!.Model));
        }, continuation);
        for (var api = 0; api < maxExchanges; api++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (limit.HasValue || session.RoundsUsed >= session.MaxRounds)
                return Result("", limit ?? AdvisorCompletionReason.RoundLimit);
            var reply = await connection.GenerateAsync(true, new AiContinuation(retrievals, session.RoundsUsed, session.MaxRounds, context.Operations?.History), cancellation,
                (requestNumber, fallback, model, size) =>
                {
                    if (size > MaxTotalRequestBytes - bytesSent) throw new AiConnectionException("request_budget");
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
            var dispatch = await tools.ExecuteAsync(calls, cancellation, (pending, situation) =>
                context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.Retrieving, connection.ApiRequests,
                    session.RoundsUsed + (situation ? 1 : 0), session.MaxRounds, pending, connection.UsingFallback, connection.Model)),
                (request, response) => retrievals.Add(new JObject { ["request"] = request.DeepClone(), ["response"] = response.DeepClone() })).ConfigureAwait(false);
            if (dispatch.CompletedQueries.Length > 0) lastQueries = dispatch.CompletedQueries;
            queriesUsed.AddRange(dispatch.Queries);
            limit = dispatch.Limit;
            connection.AcceptResults(dispatch.Results);
        }
        cancellation.ThrowIfCancellationRequested();
        return Result("", limit ?? AdvisorCompletionReason.RoundLimit);

        AdviceResult Result(string answer, AdvisorCompletionReason reason) => new(answer, connection.ApiRequests,
            session.RoundsUsed, usageKnown ? totalTokens : (long?)null, queriesUsed, connection.Model, reason);
    }
}
