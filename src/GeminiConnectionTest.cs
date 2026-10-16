using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A standalone generation check. It has no dependency on game sources, profiles or chat history.
internal static class GeminiConnectionTest
{
    internal static async Task<AdviceResult> RunAsync(string logDirectory, AdvisorSettings settings, CancellationToken cancellation,
        Func<AdvisorTrace?, GeminiTransport>? transportFactory = null)
    {
        using var trace = AdvisorTrace.Start(logDirectory, "connection-test-" + Guid.NewGuid().ToString("N"), settings,
            new AdvisorQuestion("Connection test"));
        try
        {
            using var transport = transportFactory == null ? new GeminiTransport(trace) : transportFactory(trace);
            var body = new JObject
            {
                ["contents"] = new JArray(new JObject { ["role"] = "user",
                    ["parts"] = new JArray(new JObject { ["text"] = "Connection test. Reply only with OK." }) }),
                ["generationConfig"] = new JObject { ["candidateCount"] = 1, ["maxOutputTokens"] = settings.MaxOutputTokens }
            };
            var response = await transport.GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (!(response["candidates"] is JArray candidates) || candidates.Count != 1 || !(candidates[0] is JObject candidate))
                throw new AdvisorConnectionException("response_blocked");
            var finish = (string?)candidate["finishReason"];
            if (finish == "MAX_TOKENS" || finish == "CONTINUATION") throw new AdvisorConnectionException("output_limit");
            if (finish != "STOP") throw new AdvisorConnectionException("generation_failed");
            if (!(candidate["content"] is JObject content) || (string?)content["role"] != "model" || !(content["parts"] is JArray parts))
                throw new AdvisorConnectionException("invalid_response");
            var answer = string.Concat(parts.OfType<JObject>().Where(p => (bool?)p["thought"] != true)
                .Select(p => p["text"]?.Type == JTokenType.String ? (string)p["text"]! : ""));
            if (string.IsNullOrWhiteSpace(answer)) throw new AdvisorConnectionException("empty_response");
            long? tokens = response["usageMetadata"]?["totalTokenCount"]?.Type == JTokenType.Integer &&
                long.TryParse(response["usageMetadata"]!["totalTokenCount"]!.ToString(), out var count) && count >= 0 ? count : (long?)null;
            var result = new AdviceResult(answer, 1, 0, tokens);
            trace?.Finish("completed", result);
            return result;
        }
        catch (OperationCanceledException) { trace?.Finish("cancelled"); throw; }
        catch (AdvisorConnectionException error) { trace?.Finish("failed", errorCode: error.Code, status: error.HttpStatus); throw; }
        catch (Exception error) { trace?.Finish("unexpected_error", errorType: error.GetType().FullName); throw; }
    }
}
