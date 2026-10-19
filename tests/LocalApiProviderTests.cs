using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;

internal static partial class ProviderTests
{
    private static void RunLocalFallbackPolicy(Action<string, Action> check)
    {
        check("Main-only preparation never sends to a metered sub, while the normal default and explicit permission keep fallback", () => Async(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-local-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, new JObject
                {
                    ["api_key"] = "mock_free_key", ["model"] = "gemini-free-model",
                    ["fallback_api_key"] = "mock_metered_key", ["fallback_model"] = "gemini-metered-model"
                }.ToString());
                var saved = File.ReadAllBytes(path);
                foreach (var policy in new bool?[] { false, true, null })
                {
                    var keys = new List<string>();
                    var registry = AdvisorServices.CreateRegistry(path, transportFactory: trace => new AdvisorHttpTransport(new BackendHttpHandler((request, _) =>
                    {
                        var key = request.Headers.GetValues("x-goog-api-key").Single(); keys.Add(key);
                        return Task.FromResult(key == "mock_free_key"
                            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"quota\"}") }
                            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InteractionFixture.Reply(new JObject { ["text"] = "sub allowed" }).ToString()) });
                    }), trace));
                    var setup = policy.HasValue ? registry.Prepare(policy.Value) : registry.Prepare();
                    if (policy == false)
                    {
                        try { await RunPrepared(setup); throw new Exception("The main failure was hidden"); }
                        catch (AdvisorConnectionException error)
                        { Require(error.HttpStatus == 429 && !error.UsingFallback && keys.SequenceEqual(new[] { "mock_free_key" }), "A forbidden metered sub was used"); }
                    }
                    else
                    {
                        var result = await RunPrepared(setup);
                        Require(result.Text == "sub allowed" && keys.SequenceEqual(new[] { "mock_free_key", "mock_metered_key" }), "Explicit permission or the existing UI default disabled fallback");
                    }
                    Require(File.ReadAllBytes(path).SequenceEqual(saved), "Per-request fallback policy rewrote saved connections");
                }
                var unsupported = new AdvisorBackendRegistry(); var opened = false;
                unsupported.Register("custom", () => { opened = true; return Setup(new CustomBackend((_, _) => Task.FromResult(Answer("wrong")))); }); unsupported.Select("custom");
                try { unsupported.Prepare(false); throw new Exception("An unverified workflow was permitted"); }
                catch (AdvisorConnectionException error) { Require(error.Code == "fallback_policy_unavailable" && !opened, "A backend without policy support was executed"); }
            }
            finally { Directory.Delete(directory, true); }
        }));
        check("Main-only policy also prevents host fallback to an independent public workflow", () => Async(async () =>
        {
            var main = IndependentClient(); var sub = IndependentClient(); using var host = new PublicHost(main, sub);
            var subCalls = 0;
            main.Consult = (_, _, _, _) => throw new Api.AdvisorException("rate_limited", 429);
            sub.Consult = (configuration, _, _, _) => { subCalls++; return Task.FromResult(new Api.AdvisorAnswer("independent sub allowed", configuration.Model)); };
            var draft = ExtensionDraft(main); var subConfiguration = ExternalConfiguration(sub, "sub");
            draft.Put(new AdvisorApiConfigurationDraft(subConfiguration)); draft.SubId = subConfiguration.Id;
            try { await RunPrepared(AdvisorServices.Prepare(draft, allowFallback: false)); throw new Exception("Main error was hidden"); }
            catch (AdvisorConnectionException error) { Require(error.HttpStatus == 429 && !error.UsingFallback && subCalls == 0, "The independent sub ran without permission"); }
            var result = await RunPrepared(AdvisorServices.Prepare(draft, allowFallback: true));
            Require(result.Text == "independent sub allowed" && subCalls == 1, "An explicitly allowed independent sub was rejected");
        }));
    }
}
