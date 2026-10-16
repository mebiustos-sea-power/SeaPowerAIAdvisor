using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class GeminiSmoke
{
    internal static void Run(string campaign, string save, string definition, string settingsPath, string output)
    {
        // Secrets are read only inside the process; neither request bodies nor settings are logged.
        var settings = AdvisorSettings.Load(settingsPath);
        var snapshot = SituationTests.CheckSave(Assembly.LoadFrom(campaign), save, definition);
        var question = "接続試験です。自軍艦隊の一覧を取得し、先頭の艦隊から艦を1隻選んで燃料・弾薬・損傷を確認し、その艦名と状態を日本語で短く答えてください。実セーブの読み取り試験で、兵装サービスにはスタブを使用しています。";
        using var transport = new GeminiTransport();
        var result = new GeminiAdvisor(transport).ConsultAsync(new AdvisorQuerySession(snapshot, "ja", question),
            settings, Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
        var report = new JObject
        {
            ["mode"] = "live_gemini_smoke_test", ["model"] = settings.Model,
            ["notice"] = "One live consultation using a read-only saved campaign and stubbed own catalog/ammunition services. Not a live-game accuracy or UI test.",
            ["api_requests"] = result.ApiRequests, ["query_rounds"] = result.QueryRounds,
            ["queries"] = new JArray(result.Queries), ["total_tokens"] = result.TotalTokens.HasValue ? new JValue(result.TotalTokens.Value) : JValue.CreateNull(),
            ["answer"] = result.Text
        };
        File.WriteAllText(output, report.ToString(Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine("Gemini smoke completed: model=" + settings.Model + ", API requests=" + result.ApiRequests +
            ", query rounds=" + result.QueryRounds + ", total tokens=" + result.TotalTokens);
        Console.WriteLine("Queries: " + string.Join(", ", result.Queries));
        Console.WriteLine("Result saved: " + output);
    }
}
