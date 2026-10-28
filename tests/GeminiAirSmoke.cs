using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class GeminiAirSmoke
{
    // Explicit opt-in only. Synthetic aviation data tests model behavior, not a saved game's aircraft inventory.
    internal static void Run(string campaignPath, string settingsPath, string output)
    {
        var fixture = new AirTests.CarrierFixture(Assembly.LoadFrom(campaignPath));
        SituationTests.Set(fixture.Hull, "Name", "テスト空母");
        var session = new AdvisorQuerySession(fixture.Capture(), "ja",
            "先ほど勧めた艦載機による対艦攻撃について、具体的にどの機種を何機、どの兵装で出せますか？");
        var history = new[] { new ChatExchange("まず何をする？",
            "テスト空母のF-14とF/A-18を発進させ、対艦ミサイルで敵水上艦を攻撃してください。") };
        var settings = AdvisorSettings.Load(settingsPath);
        using var transport = new RecordedTransport();
        var result = new GeminiFixture(transport).ConsultAsync(session, settings, history, CancellationToken.None).GetAwaiter().GetResult();
        var report = new JObject
        {
            ["test_data"] = "synthetic own aviation, real campaign state classes and wing calculations; not the user's aircraft inventory",
            ["model"] = settings.Model, ["api_requests"] = result.ApiRequests, ["query_rounds"] = result.QueryRounds,
            ["queries"] = new JArray(result.Queries), ["total_tokens"] = result.TotalTokens.HasValue ? new JValue(result.TotalTokens.Value) : JValue.CreateNull(),
            ["query_trace"] = transport.Trace,
            ["expected_evidence"] = "Only E-2C and A-6 are present. A-6 is not launch-supported on the campaign map. Carrier is in port. Prior F-14/F-A-18 claim is unsupported. Weapon and sortie feasibility are unknown.",
            ["answer"] = result.Text
        };
        File.WriteAllText(output, report.ToString(Formatting.Indented));
        Console.WriteLine("Aviation model evaluation saved: " + output);
        Console.WriteLine("API requests=" + result.ApiRequests + ", query rounds=" + result.QueryRounds +
            ", total tokens=" + (result.TotalTokens.HasValue ? result.TotalTokens.Value.ToString() : "unknown"));
        Console.WriteLine("Queries: " + string.Join(", ", result.Queries));
        // The Windows console encoding may differ; the UTF-8 report is the authoritative answer artifact.
    }
    private sealed class RecordedTransport : IGeminiTransport, IDisposable
    {
        private readonly GeminiTransport inner = new();
        internal JArray Trace { get; } = new();
        public async System.Threading.Tasks.Task<JObject> GenerateAsync(AiConnectionSettings settings, JObject body, CancellationToken token)
        {
            var response = await inner.GenerateAsync(settings, body, token);
            var calls = new JArray();
            foreach (var step in response["steps"] ?? new JArray())
                if ((string?)step["type"] == "function_call") calls.Add(step.DeepClone());
            var previousResults = new JArray();
            foreach (var step in body["input"] ?? new JArray())
                if ((string?)step["type"] == "function_result")
                {
                    var result = JObject.Parse((string)step["result"]![0]!["text"]!);
                    previousResults.Add(new JObject { ["round"] = result["round"]?.DeepClone(),
                        ["status"] = result["status"]?.DeepClone(), ["error"] = result["error"]?.DeepClone() });
                }
            Trace.Add(new JObject { ["calls"] = calls, ["received_query_results"] = previousResults });
            return response;
        }
        public void Dispose() => inner.Dispose();
    }
}
