using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Standard-engine providers receive detached inputs, never the game or the knowledge gateway.
internal interface IAiProvider
{
    string Model { get; }
    IAiProviderSession Start(AiProviderInput input, AiContinuation? continuation);
}

internal interface IAiProviderSession : IDisposable
{
    AiProviderRequest Prepare(bool allowTools);
    void AcceptResults(IReadOnlyList<AiToolResult> results);
}

// Preparing a request performs no network I/O. The host checks its byte budget before sending it.
internal sealed class AiProviderRequest
{
    internal int Bytes { get; }
    private readonly Func<CancellationToken, Task<AiReply>> send;
    internal AiProviderRequest(int bytes, Func<CancellationToken, Task<AiReply>> send)
    { if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes)); Bytes = bytes; this.send = send; }
    internal Task<AiReply> SendAsync(CancellationToken cancellation) => send(cancellation);
}

internal sealed class AiProviderInput
{
    internal JObject Data { get; }
    internal string SystemInstruction { get; }
    internal IReadOnlyList<JObject> Tools { get; }
    internal AiProviderInput(JObject data, string systemInstruction, JObject tool)
        : this(data, systemInstruction, new[] { tool }) { }
    internal AiProviderInput(JObject data, string systemInstruction, IEnumerable<JObject> tools)
    { Data = (JObject)data.DeepClone(); SystemInstruction = systemInstruction;
        Tools = Array.AsReadOnly(tools.Select(t => (JObject)t.DeepClone()).ToArray()); }
    internal AiProviderInput Copy() => new(Data, SystemInstruction, Tools);
}

// Only host-executed query requests/results cross a connection boundary; provider IDs/signatures do not.
internal sealed class AiContinuation
{
    internal JArray Retrievals { get; }
    internal int RoundsUsed { get; }
    internal int MaxRounds { get; }
    internal JArray Operations { get; }
    internal AiContinuation(JArray retrievals, int roundsUsed, int maxRounds, JArray? operations = null)
    { Retrievals = (JArray)retrievals.DeepClone(); RoundsUsed = roundsUsed; MaxRounds = maxRounds; Operations = (JArray?)operations?.DeepClone() ?? new JArray(); }
}

internal sealed class AiToolCall
{
    internal string Id { get; }
    internal string Name { get; }
    internal JObject Arguments { get; }
    internal AiToolCall(string id, string name, JObject arguments)
    { Id = id; Name = name; Arguments = (JObject)arguments.DeepClone(); }
}

internal sealed class AiToolResult
{
    internal string Id { get; }
    internal string Name { get; }
    internal JObject Data { get; }
    internal AiToolResult(AiToolCall call, JObject data)
    { Id = call.Id; Name = call.Name; Data = (JObject)data.DeepClone(); }
}

internal sealed class AiReply
{
    internal string Text { get; }
    internal IReadOnlyList<AiToolCall> Calls { get; }
    internal long? TotalTokens { get; }
    internal AiReply(string text, IEnumerable<AiToolCall> calls, long? totalTokens)
    { Text = text; Calls = Array.AsReadOnly(calls.ToArray()); TotalTokens = totalTokens; }
}
