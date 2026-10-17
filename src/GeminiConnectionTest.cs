using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A standalone generation check. It has no dependency on game sources, profiles or chat history.
internal static class GeminiConnectionTest
{
    internal static async Task<AdviceResult> RunAsync(string logDirectory, AdvisorSettings settings, CancellationToken cancellation,
        Func<AdvisorTrace?, GeminiTransport>? transportFactory = null, string? requestText = null)
    {
        using var trace = AdvisorTrace.Start(logDirectory, "connection-test-" + Guid.NewGuid().ToString("N"), settings,
            new AdvisorQuestion("Connection test"));
        try
        {
            using var transport = transportFactory == null ? new GeminiTransport(trace) : transportFactory(trace);
            var body = GeminiInteraction.Request(settings, new JArray(new JObject
            {
                ["type"] = "user_input", ["content"] = new JArray(new JObject
                { ["type"] = "text", ["text"] = requestText ?? AdvisorPrompts.ConnectionTestRequest() })
            }), store: false);
            var response = await transport.GenerateAsync(settings, body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var interaction = GeminiInteraction.Read(response, requireStoredId: false);
            if (interaction.Calls.Length != 0) throw new AdvisorConnectionException("unexpected_function_call");
            var result = new AdviceResult(interaction.Text, 1, 0, interaction.TotalTokens, model: settings.RequestModel);
            trace?.Finish("completed", result);
            return result;
        }
        catch (OperationCanceledException) { trace?.Finish("cancelled"); throw; }
        catch (AdvisorConnectionException error) { trace?.Finish("failed", errorCode: error.Code, status: error.HttpStatus); throw; }
        catch (Exception error) { trace?.Finish("unexpected_error", errorType: error.GetType().FullName); throw; }
    }
}
