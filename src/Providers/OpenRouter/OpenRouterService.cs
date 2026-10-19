using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class OpenRouterService : AdvisorService
{
    internal const string DefaultModel = "google/gemini-3.5-flash-lite";
    internal override string Id => "openrouter";
    internal override string Name => "OpenRouter";
    internal override string Protocol => "openrouter_chat";
    internal override string Description(AdvisorText text) => text["connection.openrouter_description"];
    internal override IReadOnlyList<AdvisorConnectionField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new AdvisorConnectionField("api_key", "connection.api_key", secret: true),
        new AdvisorConnectionField("model", "connection.model", DefaultModel)
    });
    internal override void Validate(JObject settings)
    {
        ValidateFields(settings);
        var key = (string)settings["api_key"]!;
        if (string.IsNullOrWhiteSpace(key)) throw new AdvisorConnectionException("api_key_missing");
        if (key.Length > 512 || key.Any(c => c < '!' || c > '~')) throw new AdvisorConnectionException("settings_invalid");
        var model = (string)settings["model"]!;
        var slash = model.IndexOf('/');
        if (model.Length > 200 || model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || slash <= 0 || slash == model.Length - 1)
            throw new AdvisorConnectionException("openrouter_model_invalid");
    }
    internal override IAdvisorProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        bool fallback, AdvisorHttpTransport transport) => new OpenRouterProvider(transport, configuration, options, fallback);
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, string request, CancellationToken cancellation)
    {
        // This adapter tests its tool round trip; independent adapters keep their own TestAsync workflow.
        const string name = "connection_check";
        var tool = new JObject { ["name"] = name, ["parameters"] = new JObject
        { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false } };
        var messages = new JArray(new JObject { ["role"] = "system", ["content"] = AdvisorPrompts.ToolConnectionTestRequest() },
            new JObject { ["role"] = "user", ["content"] = request });
        var provider = new OpenRouterProvider(transport, configuration, options, false);
        var forced = new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = name } };
        var firstResponse = await provider.SendAsync(OpenRouterProvider.Request(configuration, options, messages, tool, forced), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var first = OpenRouterProvider.Read(firstResponse);
        if (first.Calls.Count != 1 || first.Calls[0].Name != name || first.Calls[0].Arguments.Count != 0)
            throw new AdvisorConnectionException("tool_call_test_failed");
        var confirmation = Guid.NewGuid().ToString("N");
        messages.Add(firstResponse["choices"]![0]!["message"]!.DeepClone());
        messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = first.Calls[0].Id,
            ["content"] = new JObject { ["confirmation"] = confirmation }.ToString(Formatting.None) });
        var second = OpenRouterProvider.Read(await provider.SendAsync(
            OpenRouterProvider.Request(configuration, options, messages, tool, new JValue("none")), cancellation).ConfigureAwait(false));
        cancellation.ThrowIfCancellationRequested();
        if (second.Calls.Count != 0 || second.Text.Trim() != confirmation) throw new AdvisorConnectionException("tool_call_test_failed");
        var total = first.TotalTokens.HasValue && second.TotalTokens.HasValue && first.TotalTokens <= long.MaxValue - second.TotalTokens
            ? first.TotalTokens + second.TotalTokens : null;
        return new AdviceResult(second.Text, 2, 0, total, model: configuration.Model);
    }
}
