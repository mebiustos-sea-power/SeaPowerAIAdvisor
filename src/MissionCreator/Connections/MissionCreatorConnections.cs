using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Configuration;

namespace SeaPowerAIMissionCreator.Connections;

// UI and the local API run the same connection test, detached from the editor and its focus state.
internal sealed class MissionCreatorConnections : IDisposable
{
    internal MissionCreatorSettingsStore Store { get; }
    private readonly Func<AiHttpTransport> transport;
    private readonly Action<Exception> report;
    private readonly object gate = new();
    private MissionCreatorConnectionTest? last;
    private bool disposed;
    internal MissionCreatorConnectionTest? Last { get { lock (gate) return last; } }
    internal MissionCreatorConnections(MissionCreatorSettingsStore store, Action<Exception> report, Func<AiHttpTransport>? transport = null)
    { Store = store; this.report = report; this.transport = transport ?? (() => new AiHttpTransport()); }
    internal JObject ConnectionInfo()
    {
        var current = Store.Current;
        return new JObject { ["selected_connection_id"] = current.SelectedId,
            ["connections"] = new JArray(AiConnectionProfile.ByName(current.Connections).Select(c => new JObject
            { ["id"] = c.Id, ["name"] = c.Name, ["provider"] = c.Provider, ["model"] = c.Model,
              ["request_timeout_seconds"] = c.RequestTimeoutSeconds, ["max_output_tokens"] = c.MaxOutputTokens,
              ["has_prompt"] = c.Prompt.Length > 0, ["selected"] = c.Id == current.SelectedId })) };
    }
    internal MissionCreatorConnectionTest StartTest(string? id = null) => StartTest(Store.Current.Require(id));
    internal MissionCreatorConnectionTest StartTest(AiConnectionProfile profile)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(MissionCreatorConnections));
            if (last != null && !last.Completion.IsCompleted) throw new AiConnectionException("connection_test_in_progress");
            var definition = AiProviderDefinition.Require(profile.Provider); definition.Validate(profile.Settings);
            var settings = profile.ToConnectionSettings();
            last?.Dispose();
            return last = new MissionCreatorConnectionTest(profile.Id, async cancellation =>
            {
                using var client = transport();
                try { return await definition.TestAsync(settings, client, "Connection test. Reply only with OK.",
                    "Call connection_check, then reply only with the confirmation value returned by it.", cancellation).ConfigureAwait(false); }
                catch (Exception error) when (error is not AiConnectionException && error is not OperationCanceledException)
                { report(error); throw; }
            });
        }
    }
    internal void CancelTest() { lock (gate) last?.Cancel(); }
    public void Dispose() { lock (gate) { disposed = true; last?.Dispose(); } }
}

internal sealed class MissionCreatorConnectionTest : IDisposable
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string ConnectionId { get; }
    internal Task<AiConnectionTestResult> Completion { get; }
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal MissionCreatorConnectionTest(string connectionId, Func<CancellationToken, Task<AiConnectionTestResult>> run)
    {
        ConnectionId = connectionId;
        Completion = Task.Run(async () => { try { return await run(cancellation.Token).ConfigureAwait(false); } finally { elapsed.Stop(); } });
        // Observe failures even when the initiating window closes before completion.
        _ = Completion.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    internal JObject Status()
    {
        var value = new JObject { ["run_id"] = Id, ["connection_id"] = ConnectionId, ["elapsed_seconds"] = elapsed.Elapsed.TotalSeconds };
        if (!Completion.IsCompleted) { value["state"] = "running"; return value; }
        try
        {
            var result = Completion.GetAwaiter().GetResult(); value["state"] = "completed";
            value["api_requests"] = result.ApiRequests; value["total_tokens"] = result.TotalTokens; value["model"] = result.Model;
        }
        catch (OperationCanceledException) { value["state"] = "cancelled"; }
        catch (AiConnectionException error) { value["state"] = "failed"; value["error"] = error.Code; value["http_status"] = error.HttpStatus; }
        catch (Exception) { value["state"] = "failed"; value["error"] = "unexpected_error"; }
        return value;
    }
    internal void Cancel() { if (!Completion.IsCompleted) cancellation.Cancel(); }
    public void Dispose() { Cancel(); _ = Completion.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default); }
}
