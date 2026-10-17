using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class FallbackModelTests
{
    private const string Primary = "gemini-main-model";
    private const string Secondary = "gemini-backup-model";
    private const string MainKey = "mock_main_model_key_no_network";
    private const string BackupKey = "mock_backup_model_key_no_network";
    internal static void Run(Action<string, Action> check)
    {
        check("Independent fallback models preserve legacy choices, save separately and require valid Gemini IDs only when configured", () => InTemp(directory =>
        {
            var path = Path.Combine(directory,"settings.json");
            File.WriteAllText(path,new JObject { ["api_key"] = MainKey, ["fallback_api_key"] = BackupKey, ["model"] = Primary }.ToString());
            var before = File.ReadAllBytes(path);
            var legacy = AdvisorSettings.Load(path);
            Require(legacy.FallbackModel == Primary && legacy.ForFallback().RequestModel == Primary && File.ReadAllBytes(path).SequenceEqual(before),
                "Reading a legacy shared model reset or rewrote user settings");
            var draft = AdvisorSettings.Edit(path); draft.FallbackModel = Secondary; _ = draft.Data();
            Require(AdvisorSettings.Edit(path).FallbackModel == Primary && File.ReadAllBytes(path).SequenceEqual(before), "A pending model edit was saved immediately");
            AdvisorSettings.WriteBytes(path,draft.Data());
            draft = AdvisorSettings.Edit(path); draft.Model = "gemini-renamed-main"; AdvisorSettings.WriteBytes(path,draft.Data());
            var loaded = AdvisorSettings.Load(path);
            Require(loaded.RequestModel == "gemini-renamed-main" && loaded.ForFallback().RequestModel == Secondary &&
                draft.TestSettings().RequestModel == "gemini-renamed-main" && draft.TestSettings(true).RequestModel == Secondary,
                "Editing or testing one connection changed the other's model");
            foreach (var bad in new JToken[] { new JValue(""), new JValue("other-provider"), new JValue("https://example.invalid/gemini-x"), JValue.CreateNull() })
            {
                var data = new JObject { ["api_key"] = MainKey, ["fallback_api_key"] = BackupKey, ["model"] = Primary, ["fallback_model"] = bad };
                Throws("fallback_model_invalid", () => AdvisorSettings.Parse(data.ToString()));
            }
            var optional = AdvisorSettings.Parse(new JObject { ["api_key"] = MainKey, ["model"] = Primary, ["fallback_api_key"] = "", ["fallback_model"] = "" }.ToString());
            Require(optional.RequestModel == Primary, "An unused optional fallback model blocked the main connection");
        }));
        check("Switching models carries retrieved evidence and limits into a fresh model context and reports the actual model", () => InTemp(directory => Async(async () =>
        {
            var settings = AdvisorSettings.Parse(new JObject { ["api_key"] = MainKey, ["fallback_api_key"] = BackupKey,
                ["model"] = Primary, ["fallback_model"] = Secondary, ["log_ai_json"] = true }.ToString());
            var session = Session(); var paths = new List<string>(); var keys = new List<string>(); var progress = new List<AdvisorProgress>();
            JObject? initial = null;
            var handler = new Handler(async (request,token) =>
            {
                paths.Add(request.RequestUri!.AbsolutePath); keys.Add(request.Headers.GetValues("x-goog-api-key").Single());
                var json = await request.Content!.ReadAsStringAsync(token); var body = JObject.Parse(json);
                switch (paths.Count)
                {
                    case 1:
                        initial = JObject.Parse((string)body["contents"]![0]!["parts"]![0]!["text"]!);
                        return Response(200,Reply(Tool("get_contact_threats","PRIMARY_SIGNATURE","observed_contact_1")));
                    case 2:
                        Require(json.Contains("PRIMARY_SIGNATURE",StringComparison.Ordinal) && json.Contains("Frozen weapon",StringComparison.Ordinal), "The original tool exchange was lost before fallback");
                        return Response(429,"{}");
                    case 3:
                        var contents = (JArray)body["contents"]!;
                        var input = JObject.Parse((string)contents[0]!["parts"]![0]!["text"]!);
                        var continuation = (JObject)input["continuation"]!;
                        Require(contents.Count == 1 && (string?)contents[0]!["role"] == "user" && !json.Contains("PRIMARY_SIGNATURE",StringComparison.Ordinal) &&
                            (int)continuation["query_rounds_used"]! == 1 && (int)continuation["remaining_query_rounds"]! == 1 &&
                            continuation["completed_retrievals"]!.Count() == 1 && continuation.ToString().Contains("Frozen weapon",StringComparison.Ordinal) && session.RoundsUsed == 1,
                            "A different model received foreign signed context, lost evidence or reset retrieval limits");
                        input.Remove("continuation");
                        Require(JToken.DeepEquals(input,initial), "Model switching changed the question, language, objectives, history or preferences");
                        return Response(200,Reply(Tool("get_contact_threats","BACKUP_SIGNATURE","observed_contact_1")));
                    default:
                        Require(!json.Contains("PRIMARY_SIGNATURE",StringComparison.Ordinal) && json.Contains("BACKUP_SIGNATURE",StringComparison.Ordinal) &&
                            (string?)body["toolConfig"]!["functionCallingConfig"]!["mode"] == "NONE", "Later backup calls lost their own signatures or exceeded the retrieval limit");
                        var carried = JObject.Parse((string)body["contents"]![0]!["parts"]![0]!["text"]!)["continuation"]!["completed_retrievals"]![0]!["response"]!["results"]![0]!["data"]!;
                        var latest = body["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["results"]![0]!["data"]!;
                        Require(carried["weapon_references"]!.Count() == 1 && !latest["weapon_references"]!.HasValues &&
                            (string?)latest["contacts"]![0]!["possible_weapon_reference_ids"]![0] == (string?)carried["weapon_references"]![0]!["id"],
                            "Fallback lost the original definition or resent it instead of resolving the current reference");
                        return Response(200,Reply(new JObject { ["text"] = "Backup answer" }));
                }
            });
            var result = await AdvisorConsultation.RunAsync(directory,session,settings,new AdvisorQuestion("Question"),new[] { new ChatExchange("Earlier","Earlier answer") },
                CancellationToken.None,trace => new GeminiTransport(handler,trace),profile:new AdvisorProfile("Officer","portrait.png",customPersonality:"Style"),
                commonPrompt:"Shared preference",progress:progress.Add);
            Require(paths.SequenceEqual(new[] { Primary,Primary,Secondary,Secondary }.Select(m => "/v1beta/models/"+m+":generateContent")) &&
                keys.SequenceEqual(new[] { MainKey,MainKey,BackupKey,BackupKey }) && result.Model == Secondary && result.QueryRounds == 2 && result.ApiRequests == 4 &&
                progress.Last().Model == Secondary && progress.First().Model == Primary, "The selected key/model pair or final model report was incorrect");
            var records = File.ReadAllLines(Directory.GetFiles(directory,"*.jsonl").Single()).Select(JObject.Parse).ToArray();
            Require(records.Where(r => (string?)r["event"] == "request").Select(r => (string?)r["model"]).SequenceEqual(new[] { Primary,Primary,Secondary,Secondary }) &&
                (string?)records.Last()["model"] == Secondary, "The actual requested model is absent from logs");
        })));
    }
    private static BattleQuerySession Session() => new(new BattleSnapshot(new JObject {
        ["captured_at_utc"]="1985-06-01T00:00:00Z",["mission_display_name"]="Mission",["friendly_bases"] = new JArray(), ["hosted_air_units"] = new JArray(), ["aviation_hosts"] = new JArray(),
        ["public_objectives"]=new JArray(),
        ["friendly_units"]=new JArray(),["unit_capabilities"]=new JArray(),
        ["observed_contacts"]=new JArray(new JObject { ["id"]="observed_contact_1",["posture"]="enemy",["kind"]="surface" }),
        ["contact_threat_references"]=new JArray(new JObject { ["id"]="observed_contact_1",["possible_weapon_reference_ids"]=new JArray("weapon_reference_1") }),
        ["weapon_references"]=new JArray(new JObject { ["id"]="weapon_reference_1",["source"]="game_encyclopedia",["display_name"]="Frozen weapon",["maximum_launch_range_nm"]=70 }),["published_reports"]=new JArray(),["mission_briefing"]="Frozen briefing"
    }),"ja","Question",maxRounds:2);
    private static JObject Tool(string name,string signature,string? contactId=null)
    {
        var query=new JObject { ["query"]=name };
        if (contactId!=null) query["ids"]=new JArray(contactId);
        return new JObject { ["thoughtSignature"]=signature,["functionCall"]=new JObject {
            ["name"]="query_situation",["args"]=new JObject { ["requests"]=new JArray(query) } } };
    }
    private static string Reply(params JObject[] parts) => new JObject { ["candidates"]=new JArray(new JObject {
        ["finishReason"]="STOP",["content"]=new JObject { ["role"]="model",["parts"]=new JArray(parts) } }) }.ToString(Formatting.None);
    private static HttpResponseMessage Response(int status,string body) => new((HttpStatusCode)status) { Content=new StringContent(body) };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) { this.send=send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => send(request,token);
    }
    private static void Throws(string code,Action action)
    { try { action(); } catch (AdvisorConnectionException error) { Require(error.Code==code,"Wrong model error: "+error.Code);return; } throw new InvalidOperationException("Expected "+code); }
    private static void InTemp(Action<string> action)
    {
        var directory=Path.Combine(Path.GetTempPath(),"SeaPowerAIAdvisor-fallback-model-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);try { action(directory); } finally { Directory.Delete(directory,true); }
    }
    private static void Async(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
}
