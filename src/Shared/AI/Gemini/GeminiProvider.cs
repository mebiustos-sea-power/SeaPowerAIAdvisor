using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Interactions state is scoped to one connection in one consultation, never shared with another provider.
internal sealed class GeminiProvider : IAiProvider
{
    private readonly IGeminiTransport transport;
    private readonly AiConnectionSettings settings;
    public string Model => settings.Model;
    internal GeminiProvider(IGeminiTransport transport, AiConnectionSettings settings)
    { this.transport = transport; this.settings = settings; }
    public IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation) => new Session(transport, settings, input, continuation);

    private sealed class Session : IAiProviderSession
    {
        private readonly IGeminiTransport transport;
        private readonly AiConnectionSettings settings;
        private readonly string system;
        private readonly IReadOnlyList<JObject> tools;
        private JArray input;
        private string? previous;

        internal Session(IGeminiTransport transport, AiConnectionSettings settings, AiProviderInput initial, AiContinuation? continuation)
        {
            this.transport = transport; this.settings = settings; system = initial.SystemInstruction; tools = initial.Tools;
            var data = (JObject)initial.Data.DeepClone();
            if (continuation != null)
                data["continuation"] = new JObject
                {
                    ["reason"] = "fallback_connection_changed", ["query_rounds_used"] = continuation.RoundsUsed,
                    ["remaining_query_rounds"] = Math.Max(0, continuation.MaxRounds - continuation.RoundsUsed),
                    ["completed_retrievals"] = continuation.Retrievals.DeepClone(), ["completed_operations"] = continuation.Operations.DeepClone()
                };
            input = GeminiInteraction.UserInput(data);
        }

        public AiProviderRequest Prepare(bool allowTools)
        {
            var body = GeminiInteraction.Request(settings, input, previous, system, allowTools: allowTools, tools: tools);
            return new AiProviderRequest(JsonPayload.Bytes(body), async cancellation =>
            {
                var response = await transport.GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var interaction = GeminiInteraction.Read(response);
                previous = interaction.Id;
                return new AiReply(interaction.Text, interaction.Calls.Select(call =>
                    new AiToolCall((string)call["id"]!, (string)call["name"]!, (JObject)call["arguments"]!)), interaction.TotalTokens);
            });
        }

        public void AcceptResults(IReadOnlyList<AiToolResult> results) => input = new JArray(results.Select(result =>
            GeminiInteraction.FunctionResult(new JObject { ["id"] = result.Id, ["name"] = result.Name }, result.Data)));

        // The caller owns the HTTP transport; only connection-specific wire state belongs to this session.
        public void Dispose() { }
    }
}
