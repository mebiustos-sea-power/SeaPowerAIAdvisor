using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class ProviderTests
{
    private static void RunLocalConnections(Action<string, Action> check)
    {
        check("A developer-selected connection sends only its key and can fall back only to an allowed saved sub", () => Async(async () =>
        {
            var source = new AdvisorSettingsDraft(new JObject());
            foreach (var id in new[] { "normal", "chosen", "sub" })
                source.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, id, "mock-key-" + id, "gemini-" + id)));
            source.MainId = "normal"; source.SubId = "sub";
            var document = source.Data();
            foreach (var allowSub in new[] { false, true })
            foreach (var allowFallback in new[] { false, true })
            {
                var policy = new AdvisorLocalConnections(allowSub ? new[] { "chosen", "sub" } : new[] { "chosen" }, "chosen");
                var draft = new AdvisorSettingsDraft(JObject.Parse(System.Text.Encoding.UTF8.GetString(document)));
                policy.Select(draft, "chosen", allowFallback);
                var keys = new List<string>();
                var setup = AdvisorServices.Prepare(draft, allowFallback: allowFallback, transportFactory: trace => new AdvisorHttpTransport(new BackendHttpHandler((request, _) =>
                {
                    var key = request.Headers.GetValues("x-goog-api-key").Single(); keys.Add(key);
                    return Task.FromResult(key == "mock-key-chosen"
                        ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"quota\"}") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InteractionFixture.Reply(new JObject { ["text"] = "allowed sub" }).ToString()) });
                }), trace));
                if (allowSub && allowFallback)
                    Require((await RunPrepared(setup)).Text == "allowed sub" && keys.SequenceEqual(new[] { "mock-key-chosen", "mock-key-sub" }), "An approved sub was not used after the chosen connection failed");
                else
                {
                    try { await RunPrepared(setup); throw new InvalidOperationException("An unapproved sub concealed the failure"); }
                    catch (AdvisorConnectionException error)
                    { Require(error.HttpStatus == 429 && keys.SequenceEqual(new[] { "mock-key-chosen" }), "A normal main or forbidden sub was contacted"); }
                }
                Require(source.MainId == "normal" && source.SubId == "sub" && source.Data().SequenceEqual(document),
                    "Developer selection changed the normal stored main/sub assignments");
            }
        }));
    }
}
