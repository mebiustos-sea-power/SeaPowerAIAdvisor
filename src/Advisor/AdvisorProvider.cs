using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Standard-engine providers receive detached inputs, never the game or the knowledge gateway.
internal interface IAdvisorProvider
{
    string Model { get; }
    IAdvisorProviderSession Start(AdvisorProviderInput input, AdvisorContinuation? continuation);
}

internal interface IAdvisorProviderSession : IDisposable
{
    AdvisorProviderRequest Prepare(bool allowTools);
    void AcceptResults(IReadOnlyList<AdvisorToolResult> results);
}

// Preparing a request performs no network I/O. The host checks its byte budget before sending it.
internal sealed class AdvisorProviderRequest
{
    internal int Bytes { get; }
    private readonly Func<CancellationToken, Task<AdvisorReply>> send;
    internal AdvisorProviderRequest(int bytes, Func<CancellationToken, Task<AdvisorReply>> send)
    { if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes)); Bytes = bytes; this.send = send; }
    internal Task<AdvisorReply> SendAsync(CancellationToken cancellation) => send(cancellation);
}

internal sealed class AdvisorProviderInput
{
    internal JObject Data { get; }
    internal string SystemInstruction { get; }
    internal JObject? Tool { get; }
    internal JObject? OperationTool { get; }
    internal AdvisorProviderInput(JObject data, string systemInstruction, JObject? tool, JObject? operationTool = null)
    { Data = (JObject)data.DeepClone(); SystemInstruction = systemInstruction; Tool = (JObject?)tool?.DeepClone(); OperationTool = (JObject?)operationTool?.DeepClone(); }
    internal AdvisorProviderInput Copy() => new(Data, SystemInstruction, Tool, OperationTool);
}

// Only host-executed query requests/results cross a connection boundary; provider IDs/signatures do not.
internal sealed class AdvisorContinuation
{
    internal JArray Retrievals { get; }
    internal int RoundsUsed { get; }
    internal int MaxRounds { get; }
    internal JArray Operations { get; }
    internal AdvisorContinuation(JArray retrievals, int roundsUsed, int maxRounds, JArray? operations = null)
    { Retrievals = (JArray)retrievals.DeepClone(); RoundsUsed = roundsUsed; MaxRounds = maxRounds; Operations = (JArray?)operations?.DeepClone() ?? new JArray(); }
}

internal sealed class AdvisorToolCall
{
    internal string Id { get; }
    internal string Name { get; }
    internal JObject Arguments { get; }
    internal AdvisorToolCall(string id, string name, JObject arguments)
    { Id = id; Name = name; Arguments = (JObject)arguments.DeepClone(); }
}

internal sealed class AdvisorToolResult
{
    internal string Id { get; }
    internal string Name { get; }
    internal JObject Data { get; }
    internal AdvisorToolResult(AdvisorToolCall call, JObject data)
    { Id = call.Id; Name = call.Name; Data = (JObject)data.DeepClone(); }
}

internal sealed class AdvisorReply
{
    internal string Text { get; }
    internal IReadOnlyList<AdvisorToolCall> Calls { get; }
    internal long? TotalTokens { get; }
    internal AdvisorReply(string text, IEnumerable<AdvisorToolCall> calls, long? totalTokens)
    { Text = text; Calls = Array.AsReadOnly(calls.ToArray()); TotalTokens = totalTokens; }
}
