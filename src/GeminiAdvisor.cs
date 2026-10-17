using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdviceResult
{
    internal string Text { get; }
    internal int ApiRequests { get; }
    internal int QueryRounds { get; }
    internal long? TotalTokens { get; }
    internal string Model { get; }
    internal IReadOnlyList<string> Queries { get; }
    internal AdviceResult(string text, int apiRequests, int queryRounds, long? totalTokens, IEnumerable<string>? queries = null, string model = "")
    { Text = text; ApiRequests = apiRequests; QueryRounds = queryRounds; TotalTokens = totalTokens; Model = model; Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).ToArray()); }
}

internal sealed class GeminiAdvisor
{
    internal const int MaxTotalRequestBytes = 1_000_000;
    private readonly IGeminiTransport transport;
    private readonly Action<AdvisorConnectionException>? switched;
    internal GeminiAdvisor(IGeminiTransport transport, Action<AdvisorConnectionException>? switched = null)
    { this.transport = transport; this.switched = switched; }

    internal async Task<AdviceResult> ConsultAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        historyExchanges = AdvisorOptions.ValidateHistoryExchanges(historyExchanges);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.ConsultationTimeoutSeconds));
        try { return await RunAsync(session, settings, history, deadline.Token, profile, commonPrompt, progress, historyExchanges, userName).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new AdvisorConnectionException("consultation_timeout"); }
    }

    private async Task<AdviceResult> RunAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile, string commonPrompt, Action<AdvisorProgress>? progress, int historyExchanges, string userName)
    {
        var maxApiRequests = session.MaxRounds + 1;
        var initial = session.InitialInput();
        profile?.AddAnswerPreferences(initial);
        initial["common_prompt"] = AdvisorOptions.ValidateCommonPrompt(commonPrompt);
        initial["user_name"] = AdvisorOptions.ValidateUserName(userName);
        var tool = (JObject)initial["query_tool"]!;
        var instructions = initial["instructions"]!.DeepClone();
        initial.Remove("instructions"); initial.Remove("query_tool");
        initial["prior_dialogue"] = new JArray(history.Skip(Math.Max(0, history.Count - historyExchanges))
            .Select(h => new JObject { ["question"] = h.Question, ["answer"] = h.Answer, ["advisor_name"] = h.AdvisorName }));
        initial["omitted_prior_exchange_count"] = Math.Max(0, history.Count - historyExchanges);
        var input = GeminiInteraction.UserInput(initial);
        string? previousInteraction = null;
        var system = session.Prompts.SystemInstruction(instructions);
        var bytesSent = 0;
        long totalTokens = 0;
        var usageKnown = true;
        var toolsAllowed = true;
        var queriesUsed = new List<string>();
        var lastQueries = Array.Empty<string>();
        var lastApiRequest = 0;
        var retrievals = new JArray();
        var fallbackContextCreated = false;
        var connection = new GeminiFallback(transport, settings, error =>
        {
            usageKnown = false; // A lost response may have consumed tokens that we cannot measure.
            switched?.Invoke(error);
            progress?.Invoke(new AdvisorProgress(AdvisorStage.SwitchingKey, lastApiRequest, session.RoundsUsed, session.MaxRounds, lastQueries, true, settings.FallbackModel));
        });
        for (var api = 1; api <= maxApiRequests; api++)
        {
            cancellation.ThrowIfCancellationRequested();
            var allow = toolsAllowed && session.RoundsUsed < session.MaxRounds && api < maxApiRequests;
            var response = await connection.GenerateAsync(fallback =>
            {
                // Keys may belong to different projects, even when their model names match.
                if (fallback && !fallbackContextCreated)
                {
                    input = ContinuationInput(initial, retrievals, session.RoundsUsed, session.MaxRounds);
                    previousInteraction = null;
                    fallbackContextCreated = true;
                }
                return GeminiInteraction.Request(fallback ? settings.ForFallback() : settings, input,
                    previousInteraction, system, tool, allow);
            }, cancellation, (requestNumber, fallback, request) =>
            {
                var size = AdvisorQueryLimits.Bytes(request);
                if (bytesSent + size > MaxTotalRequestBytes) throw new AdvisorConnectionException("request_budget");
                bytesSent += size;
                lastApiRequest = requestNumber;
                progress?.Invoke(new AdvisorProgress(allow ? AdvisorStage.Waiting : AdvisorStage.FinalAnswer,
                    requestNumber, session.RoundsUsed, session.MaxRounds, lastQueries, fallback, fallback ? settings.FallbackModel : settings.Model));
            }).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var interaction = GeminiInteraction.Read(response);
            if (interaction.TotalTokens.HasValue) totalTokens += interaction.TotalTokens.Value;
            else usageKnown = false;
            var calls = interaction.Calls;
            if (calls.Length == 0)
                return new AdviceResult(interaction.Text, connection.ApiRequests, session.RoundsUsed,
                    usageKnown ? totalTokens : (long?)null, queriesUsed, connection.UsingFallback ? settings.FallbackModel : settings.Model);
            if (!allow || calls.Length > AdvisorQueryLimits.MaxQueriesPerRound) throw new AdvisorConnectionException("unexpected_function_call");
            if (calls.Any(call => (string?)call["name"] != "query_situation")) throw new AdvisorConnectionException("invalid_function_call");
            previousInteraction = interaction.Id;
            var combined = Combine(session, calls);
            var knownQueries = ((JArray)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!).Values<string>().ToHashSet(StringComparer.Ordinal);
            var pendingQueries = (combined["requests"] as JArray ?? new JArray()).OfType<JObject>().Select(q =>
                q["query"]?.Type == JTokenType.String && knownQueries.Contains((string?)q["query"]) ? (string)q["query"]! : "unknown_query").ToArray();
            progress?.Invoke(new AdvisorProgress(AdvisorStage.Retrieving, connection.ApiRequests, session.RoundsUsed + 1, session.MaxRounds, pendingQueries, connection.UsingFallback, connection.UsingFallback ? settings.FallbackModel : settings.Model));
            var result = session.Execute(combined);
            retrievals.Add(new JObject { ["request"] = combined.DeepClone(), ["response"] = result.DeepClone() });
            lastQueries = (string?)result["status"] == "ok" ? pendingQueries : Array.Empty<string>();
            if (combined["requests"] is JArray requested)
                foreach (var query in requested.OfType<JObject>())
                {
                    var name = (string?)query["query"];
                    queriesUsed.Add(name != null && ((JArray)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!).Values<string>().Contains(name) ? name : "unknown_query");
                }
            var replies = new JArray();
            var offset = 0;
            foreach (var call in calls)
            {
                var reply = (JObject)result.DeepClone();
                if (result["results"] is JArray results)
                {
                    var length = ((JArray)call["arguments"]!["requests"]!).Count;
                    reply["results"] = new JArray(results.Skip(offset).Take(length).Select(r => r.DeepClone()));
                    offset += length;
                }
                replies.Add(GeminiInteraction.FunctionResult(call, reply));
            }
            input = replies;
            var errorCode = (string?)result["error"]?["code"];
            if (errorCode == "payload_limit" || errorCode == "round_limit") toolsAllowed = false;
        }
        throw new AdvisorConnectionException("request_limit");
    }

    // A new key receives retrieved evidence as user data, without depending on access to the previous project's conversation.
    private static JArray ContinuationInput(JObject initial, JArray retrievals, int roundsUsed, int maxRounds)
    {
        var context = (JObject)initial.DeepClone();
        context["continuation"] = new JObject
        {
            ["reason"] = "fallback_connection_changed",
            ["query_rounds_used"] = roundsUsed,
            ["remaining_query_rounds"] = Math.Max(0, maxRounds - roundsUsed),
            ["completed_retrievals"] = retrievals.DeepClone()
        };
        return GeminiInteraction.UserInput(context);
    }

    private static JObject Combine(IAdvisorQuerySession session, JObject[] calls)
    {
        var requests = new JArray();
        foreach (var call in calls)
        {
            var args = (JObject)call["arguments"]!;
            // The adapter owns session identity. The model chooses queries, never the snapshot.
            // Malformed arguments still consume one bounded round and surface as a structured error.
            if (args.Properties().Any(p => p.Name != "requests"))
                return new JObject { ["consultation_id"] = session.Id, ["unsupported_tool_argument"] = true };
            if (!(args["requests"] is JArray batch) || batch.Count == 0)
                return new JObject { ["consultation_id"] = session.Id, ["requests"] = args["requests"]?.DeepClone() };
            foreach (var request in batch) requests.Add(request.DeepClone());
        }
        return new JObject { ["consultation_id"] = session.Id, ["requests"] = requests };
    }
}
