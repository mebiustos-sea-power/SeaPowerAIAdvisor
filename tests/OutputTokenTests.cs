using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunOutputTokens(Action<string, Action> check)
    {
        check("Former NanoGPT limits move to connection metadata without changing the original file or keeping shared overrides", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var entry = NanoConfiguration().Json();
                entry.Remove("max_output_tokens");
                entry["settings"]!["max_output_tokens"] = "32768";
                var source = new JObject { ["schema_version"] = 3, ["main_api_id"] = "nano", ["sub_api_id"] = JValue.CreateNull(),
                    ["max_output_tokens"] = 2048, ["api_configurations"] = new JArray(entry) };
                var path = Path.Combine(directory, "migration.json"); File.WriteAllText(path, source.ToString());
                var before = File.ReadAllBytes(path); var store = new AdvisorApiSettingsStore(path, () => "API");
                var read = store.Read();
                Require(read.Main.MaxOutputTokens == 32768 && read.Main.Settings["max_output_tokens"] == null &&
                    File.ReadAllBytes(path).SequenceEqual(before), "Reading a NanoGPT limit lost the explicit value or rewrote storage");
                store.SaveEntry(read.Main, false);
                var saved = JObject.Parse(File.ReadAllText(path)); var item = saved["api_configurations"]![0]!;
                Require(saved["max_output_tokens"] == null && (int?)item["max_output_tokens"] == 32768 && item["settings"]!["max_output_tokens"] == null &&
                    store.Read().Main.MaxOutputTokens == 32768, "Saving kept conflicting output limits or lost the connection value");
                entry["max_output_tokens"] = 16384;
                Require(new AdvisorSettingsDraft(source).Main.MaxOutputTokens == 16384, "An obsolete NanoGPT field overrode current connection metadata");
                foreach (var bad in new JToken[] { new JValue(0), new JValue(-1), new JValue("8192"), JValue.CreateNull(), new JValue(1.5), new JValue(2147483648L) })
                {
                    var invalid = (JObject)saved.DeepClone(); invalid["api_configurations"]![0]!["max_output_tokens"] = bad;
                    ExpectServiceError("output_tokens_invalid", () => new AdvisorSettingsDraft(invalid));
                }
                await Task.CompletedTask;
            });
        }));
        check("All built-in providers use edited connection output limits in both probes and consultations", () => Async(async () =>
        {
            foreach (var provider in new[] { "gemini", "ollama", "openrouter", "nanogpt" })
            {
                await InServiceDirectory(async directory =>
                {
                    var service = AdvisorServiceCatalog.BuiltIn.Require(provider);
                    var settings = service.Defaults();
                    if (provider != "ollama") settings["api_key"] = "mock_output_limit_secret";
                    var entry = new AdvisorApiConfiguration("test", "Output limit", provider, settings);
                    var path = Path.Combine(directory, "settings.json"); var store = new AdvisorApiSettingsStore(path, () => "API");
                    store.SaveEntry(new AdvisorApiConfigurationDraft(entry), true); store.Assign(entry.Id, null);
                    Require(store.Read().Main.MaxOutputTokens == 8192, "A provider used a different default output limit");
                    var before = File.ReadAllBytes(path); var pending = store.Read().Main; pending.MaxOutputTokens = 32768;
                    System.Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> send = async (request, token) =>
                    {
                        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                        CheckOutputBudget(provider, body, 32768);
                        if (provider == "gemini") return JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Output limit applied" }));
                        if (provider == "ollama") return JsonResponse(LocalReply("Output limit applied"));
                        if (body["tool_choice"] is JObject forced && (string?)forced["function"]?["name"] == "connection_check")
                            return JsonResponse(NanoReply("", NanoCall("check", "", "connection_check", new JObject())));
                        if (body["tool_choice"]?.Type == JTokenType.String && (string?)body["tool_choice"] == "none")
                        {
                            var messages = (JArray)body["messages"]!;
                            return JsonResponse(NanoReply((string)JObject.Parse((string)messages.Last!["content"]!)["confirmation"]!));
                        }
                        return JsonResponse(NanoReply("Output limit applied"));
                    };
                    await AdvisorServices.TestAsync("unused-output-limit-probe", pending.Build(), store.TestOptions(), "Test", CancellationToken.None,
                        transportFactory: trace => new AdvisorHttpTransport(new BackendHttpHandler(send), trace));
                    Require(File.ReadAllBytes(path).SequenceEqual(before), "Testing an edited output limit changed saved settings");
                    store.SaveEntry(pending, false);
                    var result = await RunPrepared(AdvisorServices.Prepare(store.Read(), transportFactory: trace => new AdvisorHttpTransport(new BackendHttpHandler(send), trace)));
                    Require(result.Text == "Output limit applied" && store.Read().Main.MaxOutputTokens == 32768,
                        "A saved output limit was not applied to the consultation");
                });
            }
        }));
        check("Switching connections and public extension bridges retain each connection's output budget", () => Async(async () =>
        {
            var draft = LocalDraft(); draft.Main.MaxOutputTokens = 1024;
            var remote = new AdvisorApiConfiguration("remote", "Remote", "mock_output_sub_key", "gemini-sub", maxOutputTokens: 16384);
            draft.Put(new AdvisorApiConfigurationDraft(remote)); draft.SubId = remote.Id;
            var bridged = AdvisorExtensionBridge.Configuration(AdvisorServiceCatalog.BuiltIn.Require("gemini"), remote, true);
            Require(bridged.MaxOutputTokens == 16384, "Public connection services received a shared output limit");
            var sends = 0;
            var handler = new BackendHttpHandler(async (request, token) =>
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); sends++;
                if (request.RequestUri!.Host == "localhost")
                {
                    CheckOutputBudget("ollama", body, 1024);
                    return new System.Net.Http.HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new System.Net.Http.StringContent("busy") };
                }
                CheckOutputBudget("gemini", body, 16384);
                return JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Sub limit applied" }));
            });
            var result = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(result.Text == "Sub limit applied" && sends == 2, "Fallback inherited the main output budget or retried unexpectedly");
        }));
    }
    private static void CheckOutputBudget(string provider, JObject body, int expected)
    {
        var value = provider == "gemini" ? body["generation_config"]?["max_output_tokens"]
            : provider == "ollama" ? body["options"]?["num_predict"] : body["max_tokens"];
        Require((int?)value == expected, "A provider sent the wrong connection output limit");
    }
}
