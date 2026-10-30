using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Crew;

internal static class CrewConnectionTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Reports uses its own selected saved profile and one submission request without adviser prompts, history or operations", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data());
            var sends = 0;
            var connections = new ReportsConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace => new AiHttpTransport(new Handler(async (request, _) =>
            {
                sends++; var body = JObject.Parse(await request.Content!.ReadAsStringAsync()); var system = (string)body["messages"]![0]!["content"]!;
                Require((string?)body["model"] == "deepseek/reports" && request.Headers.GetValues("Authorization").Single() == "Bearer mock_reports", "Reports used another feature's model or credentials");
                Require(system.Contains("REPORTS_SUPPLEMENT", StringComparison.Ordinal) && !system.Contains("ADVISOR_SUPPLEMENT", StringComparison.Ordinal) && !system.Contains("FALLBACK_SUPPLEMENT", StringComparison.Ordinal), "Another connection's prompt leaked into Reports");
                Require(((JArray)body["messages"]!).Count == 2 && ((JArray)body["tools"]!).Count == 1 && (string?)body["tools"]![0]!["function"]!["name"] == CrewBatch.ToolName, "A tactical history, retrieval or operation tool entered Reports");
                return Response(HttpStatusCode.OK);
            }), trace));
            var result = connections.GenerateAsync(Batch(), "reports", CancellationToken.None).GetAwaiter().GetResult();
            Require(sends == 1 && result.Reports.Single().Id == "unit:1", "The terminal report submission caused extra exchanges");
        }));
        check("Missing Reports connections fail before transport creation and server failure never uses the adviser's fallback", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "api.json"); AdvisorSettings.WriteBytes(path, Settings().Data()); var created = 0; var sends = 0;
            var connections = new ReportsConnections(path, directory, new CrewSettingsStore(Path.Combine(directory, "crew.json")), () => AdvisorText.Load("en"), transportFactory: trace =>
            { created++; return new AiHttpTransport(new Handler((_, _) => { sends++; return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)); }), trace); });
            try { _ = connections.GenerateAsync(Batch(), "removed-id", CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("Missing connection was accepted"); }
            catch (CrewException e) { Require(e.Code == "connection_missing" && created == 0, "Missing configuration opened a transport or was hidden"); }
            try { _ = connections.GenerateAsync(Batch(), "reports", CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("Server failure was accepted"); }
            catch (AiConnectionException e) { Require(e.HttpStatus == 503 && sends == 1 && created == 1, "Reports silently tried another assigned model"); }
        }));
    }
    private static CrewBatch Batch() => new(new JObject { ["reply_language"] = "en", ["appointments"] = new JArray(new JObject { ["id"] = "unit:1", ["nation"] = "US", ["role"] = "captain" }), ["reporters"] = new JArray(new JObject { ["id"] = "unit:1" }) }, new Dictionary<string, CrewPerson?> { ["unit:1"] = null }, new[] { "unit:1" });
    private static AdvisorSettingsDraft Settings()
    {
        var saved = new AdvisorSettingsDraft(new JObject());
        foreach (var id in new[] { "advisor", "reports", "fallback" })
            saved.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, id, "nanogpt", new JObject { ["api_key"] = "mock_" + id, ["model"] = "deepseek/" + id }, prompt: id.ToUpperInvariant() + "_SUPPLEMENT")));
        saved.MainId = "advisor"; saved.SubId = "fallback"; return saved;
    }
    private static HttpResponseMessage Response(HttpStatusCode status)
    {
        var rows = new JArray(new JObject { ["id"] = "unit:1", ["text"] = "Maintaining station." });
        var appointments = new JArray(new JObject { ["id"] = "unit:1", ["person_name"] = "Fictional", ["personality"] = "Calm" });
        var result = new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = "tool_calls", ["message"] = new JObject { ["role"] = "assistant", ["tool_calls"] = new JArray(new JObject { ["id"] = "reports", ["type"] = "function", ["function"] = new JObject { ["name"] = CrewBatch.ToolName, ["arguments"] = new JObject { ["appointments"] = appointments, ["reports"] = rows }.ToString(Newtonsoft.Json.Formatting.None) } }) } }), ["usage"] = new JObject { ["total_tokens"] = 10 } };
        return new HttpResponseMessage(status) { Content = new StringContent(result.ToString(), Encoding.UTF8, "application/json") };
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-connections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
