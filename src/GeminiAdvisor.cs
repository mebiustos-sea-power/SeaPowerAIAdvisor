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
    internal IReadOnlyList<string> Queries { get; }
    internal AdviceResult(string text, int apiRequests, int queryRounds, long? totalTokens, IEnumerable<string>? queries = null)
    { Text = text; ApiRequests = apiRequests; QueryRounds = queryRounds; TotalTokens = totalTokens; Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).ToArray()); }
}

internal sealed class GeminiAdvisor
{
    internal const int MaxApiRequests = AdvisorQueryLimits.MaxRounds + 1;
    internal const int MaxTotalRequestBytes = 1_000_000;
    internal const int ContextExchanges = 3;
    private readonly IGeminiTransport transport;
    internal GeminiAdvisor(IGeminiTransport transport) { this.transport = transport; }

    internal async Task<AdviceResult> ConsultAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.ConsultationTimeoutSeconds));
        try { return await RunAsync(session, settings, history, deadline.Token, profile).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new AdvisorConnectionException("consultation_timeout"); }
    }

    private async Task<AdviceResult> RunAsync(IAdvisorQuerySession session, AdvisorSettings settings,
        IReadOnlyList<ChatExchange> history, CancellationToken cancellation, AdvisorProfile? profile)
    {
        var initial = session.InitialInput();
        profile?.AddAnswerPreferences(initial);
        var tool = (JObject)initial["query_tool"]!;
        var instructions = initial["instructions"]!.DeepClone();
        initial.Remove("instructions"); initial.Remove("query_tool");
        initial["prior_dialogue"] = new JArray(history.Skip(Math.Max(0, history.Count - ContextExchanges))
            .Select(h => new JObject { ["question"] = h.Question, ["answer"] = h.Answer }));
        initial["omitted_prior_exchange_count"] = Math.Max(0, history.Count - ContextExchanges);
        var contents = new JArray(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = initial.ToString(Formatting.None) }) });
        var system = "You are an adviser for a fictional Sea Power game. Respond to the user's message and give game advice when requested. " +
            "Only current player-known evidence in the supplied snapshot and query results is authoritative. Prior dialogue is historical context, not a current observation; never reuse a prior consultation's IDs. " +
            "Before recommending actions for specific units, request their relevant current details using the supplied knowledge catalogue. Explain material missing information. " +
            "All evidence and aviation rules in evidence_policy apply equally to follow-up questions. Recheck earlier adviser claims rather than treating them as sources. " +
            "Use query_situation once per turn with a batch of independent queries when useful. Respect all retrieval limits; when tools are disabled, give a final answer using gathered evidence and disclose remaining uncertainty. " +
            "Use the requested reply_language and preserve supplied display names. Do not expose internal IDs, tool syntax or hidden reasoning to the player. " +
            instructions.ToString(Formatting.None) + (profile == null ? "" : " " + AdvisorAnswerPolicy.Personality);
        var bytesSent = 0;
        long totalTokens = 0;
        var usageKnown = true;
        var toolsAllowed = true;
        var queriesUsed = new List<string>();
        for (var api = 1; api <= MaxApiRequests; api++)
        {
            cancellation.ThrowIfCancellationRequested();
            var allow = toolsAllowed && session.RoundsUsed < AdvisorQueryLimits.MaxRounds && api < MaxApiRequests;
            var body = new JObject
            {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = system }) },
                ["contents"] = contents.DeepClone(),
                ["tools"] = new JArray(new JObject { ["functionDeclarations"] = new JArray(new JObject
                {
                    ["name"] = tool["name"]!.DeepClone(), ["description"] = tool["description"]!.DeepClone(),
                    ["parametersJsonSchema"] = tool["parameters"]!.DeepClone()
                }) }),
                ["toolConfig"] = new JObject { ["functionCallingConfig"] = new JObject { ["mode"] = allow ? "AUTO" : "NONE" } },
                ["generationConfig"] = new JObject { ["candidateCount"] = 1, ["maxOutputTokens"] = settings.MaxOutputTokens }
            };
            var size = AdvisorQueryLimits.Bytes(body);
            if (bytesSent + size > MaxTotalRequestBytes) throw new AdvisorConnectionException("request_budget");
            bytesSent += size;
            var response = await transport.GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var count = response["usageMetadata"]?["totalTokenCount"];
            if (count?.Type == JTokenType.Integer && long.TryParse(count.ToString(), out var measured) && measured >= 0)
                totalTokens += measured;
            else usageKnown = false;
            if (!(response["candidates"] is JArray candidates) || candidates.Count != 1 ||
                !(candidates[0] is JObject candidate)) throw new AdvisorConnectionException("response_blocked");
            var finish = (string?)candidate["finishReason"];
            if (finish == "MAX_TOKENS" || finish == "CONTINUATION") throw new AdvisorConnectionException("output_limit");
            if (finish != "STOP") throw new AdvisorConnectionException("generation_failed");
            if (!(candidate["content"] is JObject content) || (string?)content["role"] != "model" ||
                !(content["parts"] is JArray parts)) throw new AdvisorConnectionException("invalid_response");
            var calls = parts.OfType<JObject>().Where(p => p["functionCall"] != null).Select(p => p["functionCall"] as JObject
                ?? throw new AdvisorConnectionException("invalid_response")).ToArray();
            if (calls.Length == 0)
            {
                var answer = string.Concat(parts.OfType<JObject>().Where(p => (bool?)p["thought"] != true)
                    .Select(p => p["text"]?.Type == JTokenType.String ? (string)p["text"]! : ""));
                if (string.IsNullOrWhiteSpace(answer)) throw new AdvisorConnectionException("empty_response");
                return new AdviceResult(answer, api, session.RoundsUsed, usageKnown ? totalTokens : (long?)null, queriesUsed);
            }
            if (!allow || calls.Length > AdvisorQueryLimits.MaxQueriesPerRound) throw new AdvisorConnectionException("unexpected_function_call");
            foreach (var call in calls)
                if ((string?)call["name"] != "query_situation" || !(call["args"] is JObject) ||
                    (call["id"] != null && call["id"]!.Type != JTokenType.String))
                    throw new AdvisorConnectionException("invalid_function_call");
            // Preserve every model part, in its original order, including opaque thought signatures.
            contents.Add(content.DeepClone());
            var combined = Combine(session, calls);
            var result = session.Execute(combined);
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
                    var length = ((JArray)call["args"]!["requests"]!).Count;
                    reply["results"] = new JArray(results.Skip(offset).Take(length).Select(r => r.DeepClone()));
                    offset += length;
                }
                var function = new JObject { ["name"] = "query_situation", ["response"] = reply };
                if (call["id"] != null) function["id"] = call["id"]!.DeepClone();
                replies.Add(new JObject { ["functionResponse"] = function });
            }
            contents.Add(new JObject { ["role"] = "user", ["parts"] = replies });
            var errorCode = (string?)result["error"]?["code"];
            if (errorCode == "payload_limit" || errorCode == "round_limit") toolsAllowed = false;
        }
        throw new AdvisorConnectionException("request_limit");
    }

    private static JObject Combine(IAdvisorQuerySession session, JObject[] calls)
    {
        var requests = new JArray();
        foreach (var call in calls)
        {
            var args = (JObject)call["args"]!;
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
