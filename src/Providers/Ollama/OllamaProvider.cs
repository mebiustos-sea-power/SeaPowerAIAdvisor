using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Ollama owns its messages, tool wire format and thinking history; no Gemini continuation IDs cross this boundary.
internal sealed class OllamaProvider : IAdvisorProvider
{
    private readonly AdvisorHttpTransport transport;
    private readonly AdvisorApiConfiguration configuration;
    private readonly AdvisorConnectionOptions options;
    private readonly bool fallback;
    public string Model => configuration.Model;
    internal OllamaProvider(AdvisorHttpTransport transport, AdvisorApiConfiguration configuration, AdvisorConnectionOptions options, bool fallback)
    { this.transport = transport; this.configuration = configuration; this.options = options; this.fallback = fallback; }
    public IAdvisorProviderSession Start(AdvisorProviderInput input, AdvisorContinuation? continuation) =>
        new Session(this, input, continuation);

    internal static JObject Request(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options, JArray messages, JObject? tool = null)
    {
        var request = new JObject
        {
            ["model"] = configuration.Model, ["messages"] = messages.DeepClone(), ["stream"] = false,
            ["options"] = new JObject { ["num_ctx"] = OllamaService.ContextLength(configuration.Settings), ["num_predict"] = options.MaxOutputTokens }
        };
        if (tool != null) request["tools"] = new JArray(new JObject
        {
            ["type"] = "function", ["function"] = new JObject { ["name"] = tool["name"]!.DeepClone(),
                ["description"] = tool["description"]!.DeepClone(), ["parameters"] = tool["parameters"]!.DeepClone() }
        });
        return request;
    }
    internal static AdvisorReply Read(JObject response, int sequence)
    {
        if (response["error"] != null) throw new AdvisorConnectionException("api_error");
        if (response["done"]?.Type != JTokenType.Boolean || (bool)response["done"]! != true ||
            response["message"] is not JObject message || message["role"]?.Type != JTokenType.String || (string?)message["role"] != "assistant" ||
            (response["done_reason"] != null && response["done_reason"]!.Type != JTokenType.String) ||
            (message["content"] != null && message["content"]!.Type != JTokenType.String) ||
            (message["thinking"] != null && message["thinking"]!.Type != JTokenType.String))
            throw new AdvisorConnectionException("invalid_response");
        if ((string?)response["done_reason"] == "length") throw new AdvisorConnectionException("output_limit");
        var calls = new List<AdvisorToolCall>();
        if (message["tool_calls"] != null)
        {
            if (message["tool_calls"] is not JArray items) throw new AdvisorConnectionException("invalid_function_call");
            foreach (var item in items)
            {
                if (item is not JObject call || call["function"] is not JObject function ||
                    function["name"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)function["name"]) ||
                    function["arguments"] is not JObject arguments) throw new AdvisorConnectionException("invalid_function_call");
                calls.Add(new AdvisorToolCall("ollama-" + sequence + "-" + calls.Count, (string)function["name"]!, arguments));
            }
        }
        var text = (string?)message["content"] ?? "";
        if (calls.Count == 0 && string.IsNullOrWhiteSpace(text)) throw new AdvisorConnectionException("empty_response");
        long? Count(string key) => response[key]?.Type == JTokenType.Integer && long.TryParse(response[key]!.ToString(), out var n) && n >= 0 ? n : (long?)null;
        var prompt = Count("prompt_eval_count"); var output = Count("eval_count");
        var total = prompt.HasValue && output.HasValue && prompt <= long.MaxValue - output ? prompt + output : null;
        return new AdvisorReply(text, calls, total);
    }

    private sealed class Session : IAdvisorProviderSession
    {
        private readonly OllamaProvider owner;
        private readonly JObject tool;
        private readonly JArray messages;
        private IReadOnlyList<AdvisorToolCall> pending = Array.Empty<AdvisorToolCall>();
        private int sequence;
        internal Session(OllamaProvider owner, AdvisorProviderInput input, AdvisorContinuation? continuation)
        {
            this.owner = owner; tool = input.Tool;
            var data = (JObject)input.Data.DeepClone();
            if (continuation != null) data["continuation"] = new JObject
            {
                ["reason"] = "fallback_connection_changed", ["query_rounds_used"] = continuation.RoundsUsed,
                ["remaining_query_rounds"] = Math.Max(0, continuation.MaxRounds - continuation.RoundsUsed),
                ["completed_retrievals"] = continuation.Retrievals.DeepClone()
            };
            messages = new JArray(new JObject { ["role"] = "system", ["content"] = input.SystemInstruction },
                new JObject { ["role"] = "user", ["content"] = data.ToString(Formatting.None) });
        }
        public AdvisorProviderRequest Prepare(bool allowTools)
        {
            if (pending.Count != 0) throw new InvalidOperationException("Ollama tool results have not been accepted.");
            var body = Request(owner.configuration, owner.options, messages, allowTools ? tool : null);
            return new AdvisorProviderRequest(AdvisorQueryLimits.Bytes(body), async cancellation =>
            {
                var response = await owner.transport.SendAsync(OllamaService.Endpoint(owner.configuration.Settings), body,
                    owner.configuration.RequestTimeoutSeconds, owner.fallback, owner.Model, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var reply = Read(response, ++sequence);
                // Preserve thinking alongside calls as required by thinking-capable models; never display it as the answer.
                messages.Add(response["message"]!.DeepClone()); pending = reply.Calls;
                return reply;
            });
        }
        public void AcceptResults(IReadOnlyList<AdvisorToolResult> results)
        {
            if (results.Count != pending.Count || results.Where((r, i) => r.Id != pending[i].Id || r.Name != pending[i].Name).Any())
                throw new InvalidOperationException("Ollama tool results do not match the pending calls.");
            foreach (var result in results) messages.Add(new JObject
            { ["role"] = "tool", ["tool_name"] = result.Name, ["content"] = result.Data.ToString(Formatting.None) });
            pending = Array.Empty<AdvisorToolCall>();
        }
        public void Dispose() { }
    }
}
