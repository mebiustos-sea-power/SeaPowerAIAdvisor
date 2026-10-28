using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;
using Client = SeaPowerAIAdvisor.ExtensionFixture;
using Example = SeaPowerAIAdvisor.Example;

internal static partial class ProviderTests
{
    private static void RunExtensions(Action<string, Action> check)
    {
        check("A separate public-contract client registers providers and rejects inactive, duplicate and incompatible registrations", () =>
        {
            Require(typeof(Client.StandardExtension).Assembly != typeof(ProviderTests).Assembly &&
                typeof(Client.StandardExtension).Assembly.GetReferencedAssemblies().All(a => a.Name is "netstandard" or "SeaPowerAIAdvisor.Api"),
                "The external client depends on host implementation types");
            Require(typeof(Api.AdvisorApi).Assembly.GetReferencedAssemblies().All(a => a.Name == "netstandard"), "Public contracts depend on the game or a JSON library");
            var external = new Client.StandardExtension();
            external.Fields = external.Fields.Concat(new[] { new Api.ConnectionField("context", "Context length", numeric: true, defaultValue: "8192") }).ToArray();
            Require(!Api.AdvisorApi.IsAvailable, "Registration was active before host initialization");
            ThrowsPublic<InvalidOperationException>(() => Api.AdvisorApi.Register(external));
            using var host = new PublicHost(external);
            Require(Api.AdvisorApi.IsAvailable && AdvisorServiceCatalog.BuiltIn.Find(external.Id) != null &&
                AdvisorServiceCatalog.BuiltIn.Require(external.Id).Fields[1].Caption(key => AdvisorText.Load("ja")[key], "ja") == "認証情報",
                "Registered fields did not reach the same catalog used by settings");
            var fields = AdvisorServiceCatalog.BuiltIn.Require(external.Id).Fields;
            Require(!fields[0].Numeric && fields[2].Numeric && fields[2].DefaultValue == "8192" &&
                new BuiltInAdvisorExtension(new BuiltInAdvisorService("ollama")).Fields.Single(f => f.Key == "num_ctx").Numeric,
                "Provider field layout metadata was lost or legacy text fields changed their layout");
            Require(AdvisorServiceCatalog.BuiltIn.Require(external.Id).Description(AdvisorText.Load("ja")) == "" &&
                AdvisorServiceCatalog.BuiltIn.Require(external.Id).Summary(AdvisorText.Load("ja")) == "",
                "An extension without a description acquired a required implementation or a placeholder message");
            var count = AdvisorServiceCatalog.BuiltIn.Services.Count;
            ThrowsPublic<ArgumentException>(() => new Api.AdvisorException("raw service response with credentials"));
            ThrowsPublic<InvalidOperationException>(() => Api.AdvisorApi.Register(external));
            ThrowsPublic<NotSupportedException>(() => Api.AdvisorApi.Register(new Client.StandardExtension { ApiVersion = Api.AdvisorApi.ContractVersion + 1 }));
            ThrowsPublic<ArgumentException>(() => Api.AdvisorApi.Register(new Client.StandardExtension
            { Fields = new[] { new Api.ConnectionField("duplicate", "First"), new Api.ConnectionField("duplicate", "Second") } }));
            Require(AdvisorServiceCatalog.BuiltIn.Services.Count == count, "Invalid registration partially replaced providers");
        });
        check("Registered external settings persist and validate without assuming an API key or a model-named field", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            await InServiceDirectory(directory =>
            {
                var path = Path.Combine(directory, "connections.json"); var draft = ExtensionDraft(external);
                AdvisorSettings.WriteBytes(path, draft.Data()); var original = File.ReadAllBytes(path);
                var store = new AdvisorApiSettingsStore(path, () => "Connection {0}"); var entry = store.Read().Main;
                Require(entry.Provider == external.Id && entry.Model == "" && (string?)entry.Settings["engine"] == "external-engine",
                    "A provider-specific setting was rewritten into a Gemini setting");
                entry.Settings["engine"] = "";
                ExpectServiceError("model_invalid", () => store.SaveEntry(entry, false));
                Require(File.ReadAllBytes(path).SequenceEqual(original), "Rejected external settings changed storage");
                var missing = JObject.Parse(File.ReadAllText(path)); missing["api_configurations"]![0]!["provider"] = "uninstalled.public.extension";
                File.WriteAllText(path, missing.ToString()); store.SaveLogging(true);
                Require((string?)JObject.Parse(File.ReadAllText(path))["api_configurations"]![0]!["settings"]?["engine"] == "external-engine",
                    "Saving another setting erased an unavailable extension");
                ExpectServiceError("provider_unavailable", () => AdvisorServices.Prepare(store.Read()));
                return Task.CompletedTask;
            });
        }));
        check("An external standard provider uses the existing tool batching and response mapping", () => Async(async () =>
        {
            var external = new Client.StandardExtension(); using var host = new PublicHost(external);
            var calls = 0; var disposed = false; IReadOnlyList<Api.ToolResult>? results = null;
            external.Open = (connection, services, input, history) =>
            {
                Require(history == null && JObject.Parse(input.DataJson)["prior_dialogue"]!.Count() == 1 &&
                    !input.DataJson.Contains("external-secret", StringComparison.Ordinal), "Public input exposed credentials or lost conversation");
                var tool = (string)input.ToolsJson.Select(JObject.Parse).Single(t => (string?)t["name"] == "query_situation")["name"]!;
                return new Client.Session(allow => new Api.ProviderRequest(100, token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (++calls == 1) return Task.FromResult(new Api.ProviderReply("", new[] {
                        new Api.ToolCall("a", tool, ReadArguments(QueryTests.Q("get_briefing")).ToString()), new Api.ToolCall("b", tool, ReadArguments(QueryTests.Q("catalog")).ToString()) }, 3));
                    Require(results?.Count == 2 && results[0].Id == "a" && results[1].Id == "b" &&
                        JObject.Parse(results[0].DataJson).ToString().Contains("Public briefing", StringComparison.Ordinal),
                        "Public tool replies lost call identity or player-known data");
                    return Task.FromResult(new Api.ProviderReply("External standard answer", totalTokens: 4));
                }), values => results = values, () => disposed = true);
            };
            var knowledge = new Knowledge(2);
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(ExtensionDraft(external)),
                AdvisorContext.Create(knowledge, new[] { new ChatExchange("Earlier", "Answer") }), "unused-public-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(answer.Text == "External standard answer" && answer.ApiRequests == 2 && answer.QueryRounds == 1 &&
                answer.TotalTokens == 7 && knowledge.Requests.Count == 1 && disposed, "The public standard adapter changed engine accounting or lifecycle");
        }));
        check("External standard requests respect the host byte budget and cannot complete with an empty answer", () => Async(async () =>
        {
            var external = new Client.StandardExtension(); using var host = new PublicHost(external);
            var sent = false; var disposed = false;
            external.Open = (_, _, _, _) => new Client.Session(_ => new Api.ProviderRequest(AdvisorEngine.MaxTotalRequestBytes + 1, _ =>
            { sent = true; return Task.FromResult(new Api.ProviderReply("Should not send")); }), _ => { }, () => disposed = true);
            try { await RunPrepared(AdvisorServices.Prepare(ExtensionDraft(external))); throw new Exception("Oversized external request sent"); }
            catch (AiConnectionException error) { Require(error.Code == "request_budget" && !sent && disposed, "Public request bypassed the standard byte budget or leaked its session"); }
            disposed = false;
            external.Open = (_, _, _, _) => new Client.Session(_ => new Api.ProviderRequest(1, _ => Task.FromResult(new Api.ProviderReply("   "))),
                _ => { }, () => disposed = true);
            try { await RunPrepared(AdvisorServices.Prepare(ExtensionDraft(external))); throw new Exception("Empty public answer succeeded"); }
            catch (AiConnectionException error) { Require(error.Code == "empty_response" && disposed, "Empty answer became a successful conversation"); }
        }));
        check("An independent external workflow makes more requests than standard rounds while host disclosure and retrieval limits remain enforced", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            var disposed = false; external.Dispose = () => disposed = true;
            Api.IAdvisorKnowledge? retained = null; var progress = new List<AdvisorProgress>();
            external.Consult = async (configuration, services, context, token) =>
            {
                retained = context.Knowledge;
                var denied = JObject.Parse(context.Knowledge.Execute(PublicQuery("get_briefing")));
                Require((string?)denied["error"]?["code"] == "information_disabled" && !denied.ToString().Contains("Public briefing", StringComparison.Ordinal),
                    "External workflow bypassed excluded information");
                _ = context.Knowledge.Execute(PublicQuery("catalog"));
                Require((string?)JObject.Parse(context.Knowledge.Execute(PublicQuery("catalog")))["error"]?["code"] == "round_limit",
                    "External workflow reset the retrieval allowance");
                ThrowsPublic<Api.AdvisorException>(() => context.Knowledge.Execute("{\"consultation_id\":\"other\",\"requests\":[]}"));
                for (var i = 0; i < 7; i++)
                {
                    await services.PostJsonAsync(new Uri("https://fixture.invalid/independent"), "{}", token);
                    context.ReportProgress(new Api.AdvisorProgressUpdate(Api.AdvisorPhase.Waiting, i + 1, new[] { "observed_contact_PRIVATE" }));
                }
                return new Api.AdvisorAnswer("Independent answer", configuration.Model, 7, 42);
            };
            var requests = 0; var handler = new BackendHttpHandler((_, _) => { requests++; return Task.FromResult(JsonResponse(new JObject { ["ok"] = true })); });
            var knowledge = new Knowledge(2, AdvisorInformation.Default.With(InformationKind.BattleBriefing, false));
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(ExtensionDraft(external), transportFactory: trace => new AiHttpTransport(handler, trace)),
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), progress: progress.Add), "unused-public-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(answer.Text == "Independent answer" && answer.Model == "external-engine" && answer.ApiRequests == 7 &&
                answer.QueryRounds == 2 && requests == 7 && disposed && handler.Disposed &&
                progress.Count == 7 && progress.All(p => p.Queries.All(q => q == "unknown_query")), "The host forced a standard loop, exposed progress identifiers, or leaked resources");
            ThrowsPublic<ObjectDisposedException>(() => retained!.Execute(PublicQuery("catalog")));
        }));
        check("Independent-to-Gemini handoff transfers only executed facts and preserves exhausted retrieval limits", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external); var disposed = false; external.Dispose = () => disposed = true;
            external.Consult = async (configuration, services, context, token) =>
            {
                _ = context.Knowledge.Execute(PublicQuery("get_briefing"));
                await services.PostJsonAsync(new Uri("https://fixture.invalid/unavailable"), "{}", token);
                throw new InvalidOperationException("Unreachable");
            };
            var draft = ExtensionDraft(external); var sub = new AdvisorApiConfiguration("sub", "Gemini sub", "mock_public_sub_key", "gemini-sub");
            draft.Put(new AdvisorApiConfigurationDraft(sub)); draft.SubId = sub.Id;
            var sends = 0; var handler = new BackendHttpHandler((request, token) =>
            {
                Require(++sends == 1, "An exhausted fallback budget sent an additional model request");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") });
            });
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(draft, transportFactory: trace => new AiHttpTransport(handler, trace)),
                AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>()), "unused-public-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(!answer.Completed && answer.CompletionReason == AdvisorCompletionReason.RoundLimit && answer.Model == "gemini-sub" &&
                answer.ApiRequests == 1 && sends == 1 && answer.QueryRounds == 1 && answer.TotalTokens == null && disposed,
                "Independent fallback changed request counts, asserted unknown usage, or leaked its execution");
        }));
        check("Gemini-to-independent handoff excludes private interaction state and continues without repeating retrieval", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            external.Consult = async (configuration, services, context, token) =>
            {
                Require(configuration.IsSubConnection && context.Continuation.RoundsUsed == 1 && context.Continuation.MaxRounds == 2 &&
                    JArray.Parse(context.Continuation.RetrievalsJson).Count == 1 && context.Continuation.RetrievalsJson.Contains("Public briefing", StringComparison.Ordinal) &&
                    !context.Input.DataJson.Contains("PRIVATE_INTERACTION", StringComparison.Ordinal) && !context.Input.DataJson.Contains("mock_public_main", StringComparison.Ordinal),
                    "Gemini credentials/private state crossed into an independent continuation");
                await services.PostJsonAsync(new Uri("https://fixture.invalid/recover"), "{}", token);
                context.ReportProgress(new Api.AdvisorProgressUpdate(Api.AdvisorPhase.Waiting, 1));
                return new Api.AdvisorAnswer("Independent recovered", configuration.Model, 1, 5);
            };
            var draft = new AdvisorSettingsDraft(new JObject { ["api_key"] = "mock_public_main", ["model"] = "gemini-main" });
            var local = ExternalConfiguration(external, "sub"); draft.Put(new AdvisorApiConfigurationDraft(local)); draft.SubId = local.Id;
            var sends = 0; var handler = new BackendHttpHandler(async (request, token) =>
            {
                if (++sends == 1)
                {
                    var initial = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require(InteractionFixture.Initial(initial)["continuation"] == null, "A first main request was incorrectly labeled as a fallback");
                    var reply = InteractionFixture.Reply(new JObject { ["functionCall"] = new JObject { ["name"] = ToolName,
                        ["args"] = ReadArguments(QueryTests.Q("get_briefing")) } }); reply["id"] = "PRIVATE_INTERACTION"; return JsonResponse(reply);
                }
                if (sends == 2) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                Require(request.RequestUri!.Host == "fixture.invalid" && !request.Headers.Contains("x-goog-api-key"), "Gemini authentication entered the external connection");
                _ = await request.Content!.ReadAsStringAsync(token); return JsonResponse(new JObject { ["ok"] = true });
            });
            var knowledge = new Knowledge(2); var updates = new List<AdvisorProgress>();
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(draft, transportFactory: trace => new AiHttpTransport(handler, trace)),
                AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), progress: updates.Add), "unused-public-log", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(answer.Text == "Independent recovered" && answer.ApiRequests == 3 && knowledge.Requests.Count == 1 && answer.TotalTokens == null && updates.Last().ApiRequest == 3 && updates.Last().UsingFallback,
                "Mixed workflow fallback repeated retrievals or reset usage accounting");
        }));
        check("Public sub failure is terminal and programming or disposal errors never trigger a normal fallback", () => Async(async () =>
        {
            var main = IndependentClient(); var sub = IndependentClient(); using var host = new PublicHost(main, sub);
            var mainCalls = 0; var subCalls = 0;
            main.Consult = (_, _, _, _) => { mainCalls++; throw new Api.AdvisorException("rate_limited", 429); };
            sub.Consult = (_, _, _, _) => { subCalls++; throw new Api.AdvisorException("network_error"); };
            var draft = ExtensionDraft(main); var entry = ExternalConfiguration(sub, "sub"); draft.Put(new AdvisorApiConfigurationDraft(entry)); draft.SubId = entry.Id;
            try { await RunPrepared(AdvisorServices.Prepare(draft)); throw new InvalidOperationException("A failed sub succeeded"); }
            catch (AiConnectionException error) { Require(error.UsingFallback && error.Code == "network_error", "Sub error lost connection identity"); }
            Require(mainCalls == 1 && subCalls == 1, "Independent connections looped between main and sub");
            main.Consult = (_, _, _, _) => throw new InvalidOperationException("Broken extension invariant");
            try { await RunPrepared(AdvisorServices.Prepare(draft)); throw new Exception("Programming error was hidden"); }
            catch (InvalidOperationException error) { Require(error.Message == "Broken extension invariant" && subCalls == 1, "Programming error used fallback"); }
            var standard = new Client.StandardExtension(); var standardSub = new Client.StandardExtension();
            AdvisorServiceCatalog.BuiltIn.Register(standard); AdvisorServiceCatalog.BuiltIn.Register(standardSub);
            var disposals = 0; var subStarts = 0;
            standard.Open = (_, _, _, _) => new Client.Session(_ => new Api.ProviderRequest(1, _ => throw new Api.AdvisorException("network_error")),
                _ => { }, () => { disposals++; throw new InvalidOperationException("Broken session disposal"); });
            standardSub.Open = (_, _, _, _) => { subStarts++; throw new InvalidOperationException("Sub should not start"); };
            var withStandard = ExtensionDraft(standard); var standardEntry = ExternalConfiguration(standardSub, "standard-sub");
            withStandard.Put(new AdvisorApiConfigurationDraft(standardEntry)); withStandard.SubId = standardEntry.Id;
            try { await RunPrepared(AdvisorServices.Prepare(withStandard)); throw new Exception("Disposal error hidden"); }
            catch (InvalidOperationException error) { Require(error.Message == "Broken session disposal" && disposals == 1 && subStarts == 0,
                "A failing external disposal created a sub execution or disposed the same session twice"); }
        }));
        check("Cancellation reaches an independent public client and disposes it without using the sub", () => Async(async () =>
        {
            var main = IndependentClient(); var sub = IndependentClient(); using var host = new PublicHost(main, sub);
            var entered = new TaskCompletionSource(); var disposed = false; var subCalls = 0;
            main.Dispose = () => disposed = true;
            main.Consult = async (_, _, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new Api.AdvisorAnswer("Unreachable"); };
            sub.Consult = (_, _, _, _) => { subCalls++; return Task.FromResult(new Api.AdvisorAnswer("Wrong sub")); };
            var draft = ExtensionDraft(main); var entry = ExternalConfiguration(sub, "sub"); draft.Put(new AdvisorApiConfigurationDraft(entry)); draft.SubId = entry.Id;
            using var cancellation = new CancellationTokenSource();
            var task = AdvisorConsultation.RunAsync(AdvisorServices.Prepare(draft), AdvisorContext.Create(new Knowledge(1), Array.Empty<ChatExchange>()),
                "unused-public-log", new AdvisorQuestion("Question"), cancellation.Token);
            await entered.Task; cancellation.Cancel();
            try { await task; throw new Exception("Cancelled client succeeded"); } catch (OperationCanceledException) { }
            Require(disposed && subCalls == 0, "Cancellation leaked a public execution or selected the sub");
        }));
        check("External connection checks use pending provider settings and redact manual concurrent diagnostics", () => Async(async () =>
        {
            var external = IndependentClient(); using var host = new PublicHost(external);
            external.Test = async (configuration, services, message, token) =>
            {
                Require(message == "Public connection test" && !configuration.IsSubConnection &&
                    (string?)JObject.Parse(configuration.SettingsJson)["secret"] == "external-secret", "Test used another connection's settings");
                await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var id = services.BeginRequest("{\"echo\":\"external-secret\"}");
                    services.EndRequest(id, 200, "{\"echo\":\"external-secret\"}");
                    ThrowsPublic<InvalidOperationException>(() => services.EndRequest(id, 200, "{}"));
                }, token)));
                return new Api.AdvisorAnswer("External test succeeded", configuration.Model, 20);
            };
            await InServiceDirectory(async directory =>
            {
                var draft = ExtensionDraft(external); draft.LogAiJson = true;
                var path = Path.Combine(directory, "connections.json"); AdvisorSettings.WriteBytes(path, draft.Data()); var original = File.ReadAllBytes(path);
                var result = await AdvisorServices.TestAsync(directory, draft.Main.Build(), AdvisorSettings.Edit(path).Options, "Public connection test", CancellationToken.None);
                var raw = File.ReadAllText(Path.Combine(directory, "ai-communication.jsonl")); var records = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();
                Require(result.Text == "External test succeeded" && result.ApiRequests == 20 && !raw.Contains("external-secret", StringComparison.Ordinal) &&
                    records.Select(r => (int)r["sequence"]!).SequenceEqual(Enumerable.Range(1, records.Length)) &&
                    records.Where(r => (string?)r["event"] == "request").Select(r => (int)r["api_request"]!).Distinct().Count() == 20 &&
                    File.ReadAllBytes(path).SequenceEqual(original), "Concurrent extension diagnostics lost identity, exposed a credential, or changed settings");
            });
        }));
        check("The AnchorChain sample uses only public APIs for both execution strategies", () => Async(async () =>
        {
            using var host = new PublicHost();
            new Example.Plugin().TriggerEntryPoint();
            Require(host.Registrations.Count == 2, "The sample entry point did not register both strategies");
            foreach (var extension in host.Registrations)
            {
                var service = AdvisorServiceCatalog.BuiltIn.Require(extension.Id);
                Require(service.Description(AdvisorText.Load("ja")) == "オフラインの実装例です。AIへの通信は行いません。" &&
                    service.Description(AdvisorText.Load("en")) == "Offline implementation example. No AI requests are sent." &&
                    service.Description(AdvisorText.Load("fr")) == service.Description(AdvisorText.Load("en")) &&
                    ((Api.IAdvisorExtensionDescription)extension).GetDescription("fr") == service.Description(AdvisorText.Load("en")),
                    "Provider-wide guidance was lost, translated into the wrong language, or failed its English fallback");
                foreach (var language in new[] { "ja", "en", "fr" })
                {
                    var text = AdvisorText.Load(language);
                    Require(service.Summary(text) == ((Api.IAdvisorExtensionSummary)extension).GetSummary(text.Language) &&
                        service.Summary(text).Length > 0 && service.Summary(text) != service.Description(text),
                        "Adapter overview reused connection setup guidance or lost its translated public metadata");
                }
                Require(service.Summary(AdvisorText.Load("fr")) == service.Summary(AdvisorText.Load("en")),
                    "Adapter overview failed its English fallback");
                var answer = await RunPrepared(AdvisorServices.Prepare(ExtensionDraft(extension)));
                Require(answer.Text.Contains("Offline API example", StringComparison.Ordinal) && answer.QueryRounds == 1,
                    "A separately compiled public sample could not retrieve known information");
            }
        }));
    }

    private static string PublicQuery(string query) => new JObject { ["requests"] = new JArray(new JObject { ["query"] = query }) }.ToString();
    private static Client.IndependentExtension IndependentClient()
    {
        var value = new Client.IndependentExtension { Fields = new[] { new Api.ConnectionField("engine", "Engine", "external-engine"),
            new Api.ConnectionField("secret", "Credential", "", true) } };
        value.Model = json => (string?)JObject.Parse(json)["engine"] ?? "";
        value.Validate = json => { if (string.IsNullOrWhiteSpace(value.Model(json))) throw new Api.AdvisorException("model_invalid"); };
        return value;
    }
    private static AdvisorApiConfiguration ExternalConfiguration(Api.IAdvisorExtension extension, string id = "external")
    {
        var settings = AdvisorServiceCatalog.BuiltIn.Require(extension.Id).Defaults();
        if (settings["secret"] != null) settings["secret"] = "external-secret";
        return new AdvisorApiConfiguration(id, "Connection " + id, extension.Id, settings);
    }
    private static AdvisorSettingsDraft ExtensionDraft(Api.IAdvisorExtension extension)
    {
        var draft = new AdvisorSettingsDraft(new JObject()); var configuration = ExternalConfiguration(extension);
        draft.Put(new AdvisorApiConfigurationDraft(configuration)); draft.MainId = configuration.Id; return draft;
    }
    private sealed class PublicHost : IDisposable
    {
        internal List<Api.IAdvisorExtension> Registrations { get; } = new();
        internal PublicHost(params Api.IAdvisorExtension[] extensions)
        {
            Api.AdvisorApi.Attach(extension =>
            { AdvisorServiceCatalog.BuiltIn.Register(extension); Registrations.Add(extension); return true; });
            try { foreach (var extension in extensions) Require(Api.AdvisorApi.Register(extension), "External registration declined"); }
            catch { Api.AdvisorApi.Detach(); throw; }
        }
        public void Dispose() => Api.AdvisorApi.Detach();
    }
    private static void ThrowsPublic<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
