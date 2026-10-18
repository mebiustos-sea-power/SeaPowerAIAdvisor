using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunRequestTimeouts(Action<string, Action> check)
    {
        check("Per-connection deadlines migrate legacy defaults, preserve existing context lengths, and save independently", () => Async(async () =>
        {
            await InServiceDirectory(async directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                var source = new JObject { ["schema_version"] = 3, ["main_api_id"] = "local", ["sub_api_id"] = "remote",
                    ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90,
                    ["api_configurations"] = new JArray(
                        new JObject { ["id"] = "local", ["name"] = "Local", ["provider"] = "ollama", ["settings"] = new JObject
                            { ["base_url"] = OllamaService.DefaultUrl, ["model"] = "qwen3:8b", ["num_ctx"] = "32768" } },
                        new JObject { ["id"] = "remote", ["name"] = "Remote", ["provider"] = "gemini", ["settings"] = new JObject
                            { ["api_key"] = "mock_timeout_key", ["model"] = "gemini-timeout" } }) };
                File.WriteAllText(path, source.ToString()); var original = File.ReadAllBytes(path);
                var store = new AdvisorApiSettingsStore(path, () => "Connection {0}"); var read = store.Read();
                Require(read.Main.RequestTimeoutSeconds == 45 && read.Sub!.RequestTimeoutSeconds == 45 &&
                    File.ReadAllBytes(path).SequenceEqual(original), "Reading migration lost the legacy deadline or rewrote storage");
                read.Main.RequestTimeoutSeconds = 600; store.SaveEntry(read.Main, false);
                var edited = store.Read().Sub!; edited.RequestTimeoutSeconds = 17; store.SaveEntry(edited, false);
                var saved = store.Read();
                Require(saved.Main.RequestTimeoutSeconds == 600 && saved.Sub!.RequestTimeoutSeconds == 17 &&
                    (string?)saved.Main.Settings["num_ctx"] == "32768" && saved.Sub.ApiKey == "mock_timeout_key" &&
                    (int?)JObject.Parse(File.ReadAllText(path))["consultation_timeout_seconds"] == 90,
                    "Editing one deadline reset another entry, changed existing context length or discarded legacy data");
                var beforeInvalid = File.ReadAllBytes(path); var invalid = store.Read().Main; invalid.RequestTimeoutSeconds = 0;
                ExpectServiceError("request_timeout_invalid", () => store.SaveEntry(invalid, false));
                Require(File.ReadAllBytes(path).SequenceEqual(beforeInvalid), "Invalid deadline changed stored settings");
                foreach (var value in new[] { "", "0", "-1", "86401", "9999999999999999999", "1.5" })
                    ExpectServiceError("request_timeout_invalid", () => AdvisorRequestTimeout.Parse(value));
                var legacy = new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock_legacy_key", ["fallback_api_key"] = "mock_legacy_sub", ["request_timeout_seconds"] = 45 });
                Require(legacy.Main.RequestTimeoutSeconds == 45 && legacy.Sub!.RequestTimeoutSeconds == 45, "Legacy key pairs did not inherit their old deadline");
                legacy.Main.RequestTimeoutSeconds = 15; legacy.Sub!.RequestTimeoutSeconds = 240;
                var compatibility = AdvisorSettings.Parse(Encoding.UTF8.GetString(legacy.Data()));
                Require(compatibility.RequestTimeoutSeconds == 15 && compatibility.ForFallback().RequestTimeoutSeconds == 240,
                    "Legacy Gemini entry point lost independent main/sub deadlines");
                var newLocal = LocalConfiguration("new");
                Require((string?)newLocal.Settings["num_ctx"] == "8192" && newLocal.RequestTimeoutSeconds == 60,
                    "A new local connection did not receive the intended usable defaults");
                await Task.CompletedTask;
            });
        }));
        check("Each provider attempt uses its own request deadline when switching from Ollama to Gemini", () => Async(async () =>
        {
            var draft = LocalDraft(); draft.Main.RequestTimeoutSeconds = 1;
            var remote = new AdvisorApiConfiguration("remote", "Remote", "mock_deadline_sub_key", "gemini-sub", requestTimeoutSeconds: 4);
            draft.Put(new AdvisorApiConfigurationDraft(remote)); draft.SubId = remote.Id;
            var requests = 0;
            var handler = new BackendHttpHandler(async (request, token) =>
            {
                requests++;
                if (request.RequestUri!.Host == "localhost")
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new Exception("An expired local request continued");
                }
                await Task.Delay(1500, token); // Longer than main's deadline, shorter than sub's.
                return JsonResponse(InteractionFixture.Reply(new JObject { ["text"] = "Sub completed" }));
            });
            var result = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(result.Text == "Sub completed" && result.Model == "gemini-sub" && result.ApiRequests == 2 && requests == 2 && handler.Disposed,
                "The sub inherited the main deadline, skipped fallback, or leaked the transport");
        }));
        check("Connection checks honor the edited deadline independently for both providers", () => Async(async () =>
        {
            foreach (var configuration in new[] { new AdvisorApiConfiguration("local", "Local", "ollama", new OllamaService().Defaults(), 1),
                new AdvisorApiConfiguration("remote", "Remote", "mock_test_deadline_key", "gemini-test", 1) })
            {
                var sends = 0; var handler = new BackendHttpHandler(async (_, token) =>
                { sends++; await Task.Delay(Timeout.Infinite, token); throw new Exception("Request deadline was ignored"); });
                try
                {
                    await AdvisorServices.TestAsync("unused-test-deadline-log", configuration, new AdvisorConnectionOptions(new JObject()),
                        "Test", CancellationToken.None, transportFactory: trace => new AdvisorHttpTransport(handler, trace));
                }
                catch (AdvisorConnectionException error)
                { Require(error.Code == "request_timeout" && sends == 1 && handler.Disposed, "A connection check ignored its edited deadline or retried"); continue; }
                throw new InvalidOperationException("An expired connection check reported success");
            }
        }));
        check("A successful multi-request consultation can exceed the retired whole-consultation deadline", () => Async(async () =>
        {
            var draft = new AdvisorSettingsDraft(new JObject { ["request_timeout_seconds"] = 8, ["consultation_timeout_seconds"] = 10 });
            var local = new AdvisorApiConfiguration("local", "Local", "ollama", new OllamaService().Defaults(), 8);
            draft.Put(new AdvisorApiConfigurationDraft(local)); draft.MainId = local.Id;
            var requests = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
            var handler = new BackendHttpHandler(async (_, token) =>
            {
                await Task.Delay(6000, token);
                return JsonResponse(++requests == 1 ? LocalReply("", null, LocalCall("get_briefing")) : LocalReply("Long consultation completed"));
            });
            var result = await RunPrepared(AdvisorServices.Prepare(draft, transportFactory: trace => new AdvisorHttpTransport(handler, trace)));
            Require(clock.Elapsed.TotalSeconds >= 10 && result.Text == "Long consultation completed" && result.ApiRequests == 2 && result.QueryRounds == 1 && handler.Disposed,
                "The obsolete total deadline interrupted valid individual requests");
        }));
    }
}
