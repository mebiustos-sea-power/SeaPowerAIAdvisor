using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor.Api;

// Prepare performs no I/O and reports the UTF-8 byte count before SendAsync.
public sealed class ProviderRequest
{
    public int Bytes { get; }
    private readonly Func<CancellationToken, Task<ProviderReply>> send;
    public ProviderRequest(int bytes, Func<CancellationToken, Task<ProviderReply>> send)
    { if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes)); Bytes = bytes; this.send = send ?? throw new ArgumentNullException(nameof(send)); }
    public Task<ProviderReply> SendAsync(CancellationToken cancellation) => send(cancellation);
}

public sealed class ToolCall
{
    public string Id { get; }
    public string Name { get; }
    public string ArgumentsJson { get; }
    public ToolCall(string id, string name, string argumentsJson)
    { Id = id; Name = name; ArgumentsJson = argumentsJson; }
}

public sealed class ToolResult
{
    public string Id { get; }
    public string Name { get; }
    public string DataJson { get; }
    public ToolResult(string id, string name, string dataJson)
    { Id = id; Name = name; DataJson = dataJson; }
}

public sealed class ProviderReply
{
    public string Text { get; }
    public IReadOnlyList<ToolCall> Calls { get; }
    public long? TotalTokens { get; }
    public ProviderReply(string text, IEnumerable<ToolCall>? calls = null, long? totalTokens = null)
    { Text = text; Calls = Array.AsReadOnly((calls ?? Array.Empty<ToolCall>()).ToArray()); TotalTokens = totalTokens; }
}

public interface IAdvisorSession : IDisposable
{
    ProviderRequest Prepare(bool allowTools);
    void AcceptResults(IReadOnlyList<ToolResult> results);
}
