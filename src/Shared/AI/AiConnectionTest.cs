using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal static class AiConnectionTest
{
    internal static async Task<AiConnectionTestResult> RunAsync(ChatCompletionProvider provider, string request, string toolInstruction, CancellationToken cancellation)
    {
        const string name = "connection_check";
        var tool = new JObject { ["name"] = name, ["parameters"] = new JObject
        { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false } };
        var messages = new JArray(new JObject { ["role"] = "system", ["content"] = toolInstruction },
            new JObject { ["role"] = "user", ["content"] = request });
        var forced = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name } };
        var firstResponse = await provider.SendAsync(provider.Request(messages, new[] { tool }, forced), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var first = provider.ReadReply(firstResponse);
        if (first.Calls.Count != 1 || first.Calls[0].Name != name || first.Calls[0].Arguments.Count != 0)
            throw new AiConnectionException("tool_call_test_failed");
        var confirmation = Guid.NewGuid().ToString("N");
        messages.Add(firstResponse["choices"]![0]!["message"]!.DeepClone());
        messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = first.Calls[0].Id,
            ["content"] = new JObject { ["confirmation"] = confirmation }.ToString(Formatting.None) });
        var second = provider.ReadReply(await provider.SendAsync(
            provider.Request(messages, new[] { tool }, new JValue("none")), cancellation).ConfigureAwait(false));
        cancellation.ThrowIfCancellationRequested();
        if (second.Calls.Count != 0 || second.Text.Trim() != confirmation) throw new AiConnectionException("tool_call_test_failed");
        var total = first.TotalTokens.HasValue && second.TotalTokens.HasValue && first.TotalTokens <= long.MaxValue - second.TotalTokens
            ? first.TotalTokens + second.TotalTokens : null;
        return new AiConnectionTestResult(second.Text, 2, total, provider.Model);
    }
}

internal sealed class AiConnectionTestResult
{
    internal string Text { get; }
    internal int ApiRequests { get; }
    internal long? TotalTokens { get; }
    internal string Model { get; }
    internal AiConnectionTestResult(string text, int apiRequests, long? totalTokens, string model)
    { Text = text; ApiRequests = apiRequests; TotalTokens = totalTokens; Model = model; }
}
