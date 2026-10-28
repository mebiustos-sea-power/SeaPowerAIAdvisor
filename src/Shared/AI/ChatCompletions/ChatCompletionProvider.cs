using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// OpenAI-compatible sessions own chat history, call IDs and opaque reasoning/signature blocks.
internal abstract class ChatCompletionProvider : IAiProvider
{
    protected abstract Uri Endpoint { get; }
    protected virtual string PaymentErrorCode => "insufficient_credits";
    protected virtual void ConfigureRequest(JObject body) { }
    private readonly AiHttpTransport transport;
    private readonly AiConnectionSettings configuration;
    public string Model => configuration.Model;
    protected ChatCompletionProvider(AiHttpTransport transport, AiConnectionSettings configuration)
    { this.transport = transport; this.configuration = configuration; }
    public IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation) => new Session(this, input, continuation);

    internal JObject Request(JArray messages, IEnumerable<JObject> definitions, JToken choice)
    {
        var body = new JObject
        {
            ["model"] = configuration.Model, ["messages"] = messages.DeepClone(), ["stream"] = false,
            ["max_tokens"] = configuration.MaxOutputTokens
        };
        var tools = new JArray();
        foreach (var definition in definitions)
            tools.Add(new JObject { ["type"] = "function", ["function"] = definition!.DeepClone() });
        if (tools.Count > 0) { body["tools"] = tools; body["tool_choice"] = choice.DeepClone(); }
        ConfigureRequest(body);
        return body;
    }
    internal AiReply ReadReply(JObject response) => Read(response, PaymentErrorCode);
    internal async Task<JObject> SendAsync(JObject body, CancellationToken cancellation)
    {
        try
        {
            return await transport.SendAsync(Endpoint, body, configuration.RequestTimeoutSeconds, configuration.IsSubConnection, Model, cancellation,
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + configuration.ApiKey }).ConfigureAwait(false);
        }
        catch (AiConnectionException error) when (error.HttpStatus == 402)
        { throw new AiConnectionException(PaymentErrorCode, 402); }
    }
    internal static AiReply Read(JObject response, string paymentErrorCode = "insufficient_credits")
    {
        CheckError(response["error"], paymentErrorCode);
        if (response["choices"] is not JArray choices || choices.Count != 1 || choices[0] is not JObject choice)
            throw new AiConnectionException("invalid_response");
        CheckError(choice["error"], paymentErrorCode);
        if (choice["finish_reason"]?.Type != JTokenType.String) throw new AiConnectionException("invalid_response");
        var finish = (string?)choice["finish_reason"];
        if (finish == "length") throw new AiConnectionException("output_limit");
        if (finish == "content_filter") throw new AiConnectionException("response_blocked");
        if (finish == "error") throw new AiConnectionException("api_error");
        if (finish is not ("stop" or "tool_calls") || choice["message"] is not JObject message ||
            message["role"]?.Type != JTokenType.String || (string?)message["role"] != "assistant" || (message["content"] != null && message["content"]!.Type is not (JTokenType.String or JTokenType.Null)))
            throw new AiConnectionException("invalid_response");
        var calls = new List<AiToolCall>();
        if (message["tool_calls"] != null && message["tool_calls"]!.Type != JTokenType.Null)
        {
            if (message["tool_calls"] is not JArray items) throw new AiConnectionException("invalid_function_call");
            foreach (var item in items)
            {
                if (item is not JObject call || call["type"]?.Type != JTokenType.String || (string?)call["type"] != "function" || call["id"]?.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace((string?)call["id"]) || call["function"] is not JObject function ||
                    function["name"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)function["name"]) ||
                    function["arguments"]?.Type != JTokenType.String)
                    throw new AiConnectionException("invalid_function_call");
                calls.Add(new AiToolCall((string)call["id"]!, (string)function["name"]!, Arguments((string)function["arguments"]!)));
            }
        }
        if ((finish == "tool_calls") != (calls.Count > 0) || calls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count)
            throw new AiConnectionException("invalid_function_call");
        var text = (string?)message["content"] ?? "";
        if (calls.Count == 0 && string.IsNullOrWhiteSpace(text)) throw new AiConnectionException("empty_response");
        if (response["usage"] != null && response["usage"]!.Type is not (JTokenType.Object or JTokenType.Null))
            throw new AiConnectionException("invalid_response");
        var usage = (response["usage"] as JObject)?["total_tokens"];
        long? total = usage?.Type == JTokenType.Integer && long.TryParse(usage.ToString(), out var n) && n >= 0 ? n : (long?)null;
        return new AiReply(text, calls, total);
    }
    private static JObject Arguments(string json)
    {
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException();
            return result;
        }
        catch (JsonException) { throw new AiConnectionException("invalid_function_call"); }
    }
    private static void CheckError(JToken? error, string paymentErrorCode)
    {
        if (error == null || error.Type == JTokenType.Null) return;
        int? status = error is JObject details && details["code"]?.Type == JTokenType.Integer &&
            int.TryParse(details["code"]!.ToString(), out var n) && n is >= 400 and <= 599 ? n : (int?)null;
        throw new AiConnectionException(status switch
        {
            400 => "api_bad_request", 401 or 403 => "api_access_denied", 402 => paymentErrorCode,
            404 => "model_unavailable", 429 => "rate_limited", _ => "api_error"
        }, status);
    }

    private sealed class Session : IAiProviderSession
    {
        private readonly ChatCompletionProvider owner;
        private readonly IReadOnlyList<JObject> tools;
        private readonly JArray messages;
        private IReadOnlyList<AiToolCall> pending = Array.Empty<AiToolCall>();
        internal Session(ChatCompletionProvider owner, AiProviderInput input, AiContinuation? continuation)
        {
            this.owner = owner; tools = input.Tools;
            var data = (JObject)input.Data.DeepClone();
            if (continuation != null) data["continuation"] = new JObject
            {
                ["reason"] = "fallback_connection_changed", ["query_rounds_used"] = continuation.RoundsUsed,
                ["remaining_query_rounds"] = Math.Max(0, continuation.MaxRounds - continuation.RoundsUsed),
                ["completed_retrievals"] = continuation.Retrievals.DeepClone(), ["completed_operations"] = continuation.Operations.DeepClone()
            };
            messages = new JArray(new JObject { ["role"] = "system", ["content"] = input.SystemInstruction },
                new JObject { ["role"] = "user", ["content"] = data.ToString(Formatting.None) });
        }
        public AiProviderRequest Prepare(bool allowTools)
        {
            if (pending.Count != 0) throw new InvalidOperationException("Chat-completion tool results have not been accepted.");
            var body = owner.Request(messages, tools, new JValue(allowTools ? "auto" : "none"));
            return new AiProviderRequest(JsonPayload.Bytes(body), async cancellation =>
            {
                var response = await owner.SendAsync(body, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var reply = owner.ReadReply(response);
                // Replay the complete assistant message to preserve provider-specific reasoning signatures.
                messages.Add(response["choices"]![0]!["message"]!.DeepClone()); pending = reply.Calls;
                return reply;
            });
        }
        public void AcceptResults(IReadOnlyList<AiToolResult> results)
        {
            if (results.Count != pending.Count || results.Where((r, i) => r.Id != pending[i].Id || r.Name != pending[i].Name).Any())
                throw new InvalidOperationException("Chat-completion tool results do not match the pending calls.");
            foreach (var result in results) messages.Add(new JObject
            { ["role"] = "tool", ["tool_call_id"] = result.Id, ["content"] = result.Data.ToString(Formatting.None) });
            pending = Array.Empty<AiToolCall>();
        }
        public void Dispose() { }
    }
}
