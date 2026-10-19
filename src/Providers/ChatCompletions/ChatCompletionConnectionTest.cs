using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal static class ChatCompletionConnectionTest
{
    internal static async Task<AdviceResult> RunAsync(ChatCompletionProvider provider, string request, CancellationToken cancellation)
    {
        const string name = "connection_check";
        var tool = new JObject { ["name"] = name, ["parameters"] = new JObject
        { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false } };
        var messages = new JArray(new JObject { ["role"] = "system", ["content"] = AdvisorPrompts.ToolConnectionTestRequest() },
            new JObject { ["role"] = "user", ["content"] = request });
        var forced = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name } };
        var firstResponse = await provider.SendAsync(provider.Request(messages, tool, forced), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var first = provider.ReadReply(firstResponse);
        if (first.Calls.Count != 1 || first.Calls[0].Name != name || first.Calls[0].Arguments.Count != 0)
            throw new AdvisorConnectionException("tool_call_test_failed");
        var confirmation = Guid.NewGuid().ToString("N");
        messages.Add(firstResponse["choices"]![0]!["message"]!.DeepClone());
        messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = first.Calls[0].Id,
            ["content"] = new JObject { ["confirmation"] = confirmation }.ToString(Formatting.None) });
        var second = provider.ReadReply(await provider.SendAsync(
            provider.Request(messages, tool, new JValue("none")), cancellation).ConfigureAwait(false));
        cancellation.ThrowIfCancellationRequested();
        if (second.Calls.Count != 0 || second.Text.Trim() != confirmation) throw new AdvisorConnectionException("tool_call_test_failed");
        var total = first.TotalTokens.HasValue && second.TotalTokens.HasValue && first.TotalTokens <= long.MaxValue - second.TotalTokens
            ? first.TotalTokens + second.TotalTokens : null;
        return new AdviceResult(second.Text, 2, 0, total, model: provider.Model);
    }
}
