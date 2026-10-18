using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Interactions state is scoped to one connection in one consultation, never shared with another provider.
internal sealed class GeminiProvider : IAdvisorProvider
{
    private readonly IGeminiTransport transport;
    private readonly AdvisorSettings settings;
    public string Model => settings.RequestModel;
    internal GeminiProvider(IGeminiTransport transport, AdvisorSettings settings)
    { this.transport = transport; this.settings = settings; }
    public IAdvisorProviderSession Start(AdvisorProviderInput input, AdvisorContinuation? continuation) => new Session(transport, settings, input, continuation);

    private sealed class Session : IAdvisorProviderSession
    {
        private readonly IGeminiTransport transport;
        private readonly AdvisorSettings settings;
        private readonly string system;
        private readonly JObject tool;
        private JArray input;
        private string? previous;

        internal Session(IGeminiTransport transport, AdvisorSettings settings, AdvisorProviderInput initial, AdvisorContinuation? continuation)
        {
            this.transport = transport; this.settings = settings; system = initial.SystemInstruction; tool = initial.Tool;
            var data = (JObject)initial.Data.DeepClone();
            if (continuation != null)
                data["continuation"] = new JObject
                {
                    ["reason"] = "fallback_connection_changed", ["query_rounds_used"] = continuation.RoundsUsed,
                    ["remaining_query_rounds"] = Math.Max(0, continuation.MaxRounds - continuation.RoundsUsed),
                    ["completed_retrievals"] = continuation.Retrievals.DeepClone()
                };
            input = GeminiInteraction.UserInput(data);
        }

        public AdvisorProviderRequest Prepare(bool allowTools)
        {
            var body = GeminiInteraction.Request(settings, input, previous, system, tool, allowTools);
            return new AdvisorProviderRequest(AdvisorQueryLimits.Bytes(body), async cancellation =>
            {
                var response = await transport.GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var interaction = GeminiInteraction.Read(response);
                previous = interaction.Id;
                return new AdvisorReply(interaction.Text, interaction.Calls.Select(call =>
                    new AdvisorToolCall((string)call["id"]!, (string)call["name"]!, (JObject)call["arguments"]!)), interaction.TotalTokens);
            });
        }

        public void AcceptResults(IReadOnlyList<AdvisorToolResult> results) => input = new JArray(results.Select(result =>
            GeminiInteraction.FunctionResult(new JObject { ["id"] = result.Id, ["name"] = result.Name }, result.Data)));

        // The caller owns the HTTP transport; only connection-specific wire state belongs to this session.
        public void Dispose() { }
    }
}
