using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class GeminiTests
{
    private const string MockKey = "mock_key_never_used_for_real_requests";
    internal static void Run(string campaignPath, Action<string, Action> check)
    {
        var assembly = Assembly.LoadFrom(campaignPath);
        check("Local Gemini settings reject malformed data without including secret values in errors", () =>
        {
            Require(Settings().Model == AdvisorSettings.DefaultModel, "Default Lite model missing");
            foreach (var json in new[] {
                "{\"api_key\":\"" + MockKey + "\",\"api_key\":\"duplicate\"}", "{\"api_key\":\"" + MockKey + "\",",
                "{\"api_key\":\"" + MockKey + "\",\"model\":\"https://other-host\"}",
                "{\"api_key\":\"" + MockKey + "\",\"model\":\"other-provider-model\"}",
                "{\"api_key\":\"" + MockKey + "\",\"unexpected\":true}",
                "{\"api_key\":\"" + MockKey + "\",\"request_timeout_seconds\":0}",
                "{\"api_key\":\"" + MockKey + "\",\"max_output_tokens\":999999999999999999999999999}"
            })
            {
                try { AdvisorSettings.Parse(json); } catch (AdvisorConnectionException error)
                { Require(!error.ToString().Contains(MockKey, StringComparison.Ordinal), "Config error leaked a key"); continue; }
                throw new InvalidOperationException("Malformed settings accepted");
            }
            ThrowsCode("api_key_missing", () => AdvisorSettings.Parse("{\"api_key\":\"\"}"));
            var path = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Require(!AdvisorSettings.LoadLogging(path), "Missing settings enabled logs");
                AdvisorSettings.SaveLogging(path, true);
                Require(AdvisorSettings.LoadLogging(path), "Logging cannot be configured before an API key");
                ThrowsCode("api_key_missing", () => AdvisorSettings.Load(path));
                var original = new JObject
                {
                    ["api_key"] = MockKey, ["model"] = AdvisorSettings.DefaultModel,
                    ["request_timeout_seconds"] = 45, ["consultation_timeout_seconds"] = 90,
                    ["max_output_tokens"] = 2048
                };
                File.WriteAllText(path, original.ToString());
                Require(AdvisorSettings.Load(path).Model == AdvisorSettings.DefaultModel &&
                    !AdvisorSettings.LoadLogging(path), "Local config did not load with logging off by default");
                foreach (var enabled in new[] { true, false })
                {
                    AdvisorSettings.SaveLogging(path, enabled);
                    var expected = (JObject)original.DeepClone();
                    expected["log_ai_json"] = enabled;
                    Require(JToken.DeepEquals(expected, JObject.Parse(File.ReadAllText(path))) &&
                        AdvisorSettings.LoadLogging(path) == enabled && AdvisorSettings.Load(path).LogAiJson == enabled,
                        "Saving the log option changed credentials/provider settings or was not reloaded");
                }
                File.WriteAllText(path, "{broken");
                var invalid = File.ReadAllBytes(path);
                ThrowsCode("settings_invalid", () => AdvisorSettings.SaveLogging(path, true));
                Require(File.ReadAllBytes(path).SequenceEqual(invalid), "A malformed configuration was overwritten");
                File.WriteAllText(path, "{\"api_key\":\"" + MockKey + "\",\"model\":\"gemini-3.1-flash-lite\"}");
                Require(AdvisorSettings.Load(path).Model == "gemini-3.1-flash-lite", "Settings were cached across requests");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        check("Advisor preferences reach Gemini in both modes without replacing core rules, language or image privacy", () => Async(async () =>
        {
            foreach (var useDefault in new[] { false, true })
            foreach (var personality in new[] { "", "親しみのある率直な口調。\n必要なら詳しく説明する。" })
            foreach (var userName in new[] { "", "山田 <>& 𠮷" })
            {
                const string commonPrompt = "全員共通の希望：距離は海里で説明する。";
                const string inactivePersonality = "INACTIVE_STYLE_MUST_REMAIN_LOCAL";
                var profile = new AdvisorProfile("カスタム士官", @"C:\Private\local-portrait.png", useDefault,
                    useDefault ? personality : inactivePersonality, useDefault ? inactivePersonality : personality);
                var snapshot = QueryTests.Capture(new SituationTests.Fixture(assembly));
                var session = new AdvisorQuerySession(snapshot, "fr", "What should I do next?");
                var expectedRules = session.InitialInput()["instructions"]!.ToString(Formatting.None);
                var fake = new Fake((n, body, token) =>
                {
                    var initial = InteractionFixture.Initial(body);
                    var preferences = (JObject)initial["advisor_preferences"]!;
                    Require((string?)preferences["name"] == profile.DisplayName &&
                        (string?)preferences["personality_and_tone"] == personality &&
                        preferences.Properties().Count() == 2 &&
                        !body.ToString().Contains(inactivePersonality, StringComparison.Ordinal),
                        "Wrong active personality or inactive/private profile settings were sent");
                    Require((string?)initial["reply_language"] == "fr" && (string?)initial["common_prompt"] == commonPrompt &&
                        (string?)initial["user_name"] == userName, "Style changed the reply language or omitted shared preferences or user name");
                    var system = (string)body["system_instruction"]!;
                    Require(system.Contains(expectedRules, StringComparison.Ordinal) &&
                        system.Contains(session.Prompts.PreferencesPolicy, StringComparison.Ordinal) &&
                        (personality.Length == 0 || !system.Contains(personality, StringComparison.Ordinal)) &&
                        !system.Contains(commonPrompt, StringComparison.Ordinal) && (userName.Length == 0 || !system.Contains(userName, StringComparison.Ordinal)),
                        "User style replaced core rules or was elevated into the system instruction");
                    return Reply(new JObject { ["text"] = "Réponse." });
                });
                var result = await new GeminiAdvisor(fake).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None, profile, commonPrompt, userName: userName);
                Require(result.Text == "Réponse." && fake.Calls == 1 &&
                    session.InitialInput()["advisor_preferences"] == null, "Preferences changed the knowledge session or caused extra API calls");
            }
        }));
        check("HTTP requests use only the official HTTPS endpoint and keep the key out of URLs and JSON", () => Async(async () =>
        {
            var handler = new Handler(async (request, cancellation) =>
            {
                Require(request.RequestUri!.Host == "generativelanguage.googleapis.com" && request.RequestUri.Scheme == "https" &&
                    request.Method == HttpMethod.Post && request.RequestUri.Query == "" && request.RequestUri.AbsolutePath == "/v1beta/interactions", "Wrong Gemini endpoint");
                Require(request.Headers.GetValues("x-goog-api-key").Single() == MockKey, "API key header missing");
                Require(!(await request.Content!.ReadAsStringAsync()).Contains(MockKey, StringComparison.Ordinal), "Key entered request JSON");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"failed\"}") };
            });
            using var transport = new GeminiTransport(handler);
            await transport.GenerateAsync(Settings(), new JObject { ["model"] = Settings().RequestModel, ["input"] = "Test" }, CancellationToken.None);
            Require(handler.Calls == 1, "Unexpected HTTP retry");
        }));
        check("HTTP failures and malformed responses expose bounded error codes without response-body secrets", () => Async(async () =>
        {
            foreach (var (status, expected) in new[] { (400, "api_bad_request"), (403, "api_access_denied"), (404, "model_unavailable"), (429, "rate_limited"), (500, "api_error") })
            {
                using var transport = new GeminiTransport(new Handler((r, c) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                    { Content = new StringContent(MockKey) })));
                await ThrowsCodeAsync(expected, () => transport.GenerateAsync(Settings(), new JObject(), CancellationToken.None));
            }
            using var invalid = new GeminiTransport(new Handler((r, c) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("bad JSON " + MockKey) })));
            await ThrowsCodeAsync("invalid_response", () => invalid.GenerateAsync(Settings(), new JObject(), CancellationToken.None));
            using var large = new GeminiTransport(new Handler((r, c) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', GeminiTransport.MaxResponseBytes + 1)) })));
            await ThrowsCodeAsync("response_too_large", () => large.GenerateAsync(Settings(), new JObject(), CancellationToken.None));
        }));
        check("Each consultation chains interactions and sends only the latest function results, preserving call IDs and usage", () => Async(async () =>
        {
            var session = Session(assembly);
            string? previous = null;
            var transport = new Fake((n, body, cancellation) =>
            {
                if (n == 5)
                {
                    Require(body["previous_interaction_id"] == null && InteractionFixture.Initial(body)["continuation"] == null,
                        "A later consultation inherited the earlier server conversation");
                    return Reply(new JObject { ["text"] = "Fresh answer" });
                }
                Require(body["tools"]![0]!["parameters"] != null && (bool?)body["store"] == true, "Function schema or stored conversation missing");
                Require((string?)body["previous_interaction_id"] == previous && !body.ToString().Contains("opaque-", StringComparison.Ordinal),
                    "The wrong interaction was continued or signed model history was resent");
                JObject response;
                if (n == 1)
                {
                    Require(InteractionFixture.Initial(body)["situation"] != null, "Initial evidence missing");
                    response = Reply(new JObject { ["functionCall"] = Call(session, "list_forces", "c1"), ["thoughtSignature"] = "opaque-first" });
                }
                else
                {
                    Require(body["input"]!.Count() == 1 && (string?)body["input"]![0]!["type"] == "function_result" &&
                        (string?)body["input"]![0]!["call_id"] == "c" + (n - 1), "Old inputs were resent or a function result lost its call ID");
                    var data = InteractionFixture.Result(body)["results"]![0]!["data"]!;
                    if (n == 2)
                        response = Reply(new JObject { ["functionCall"] = Call(session, "list_ships", "c2", "force_ids", new JArray(data["items"]![0]!["id"]!.DeepClone())) });
                    else if (n == 3)
                        response = Reply(new JObject { ["functionCall"] = Call(session, "get_ships", "c3", "ids", new JArray(data["items"]![0]!["id"]!.DeepClone())) });
                    else response = Reply(new JObject { ["thought"] = true, ["text"] = "hidden reasoning" }, new JObject { ["text"] = "自軍艦0：燃料60%、弾薬40%、損傷20%。" });
                }
                previous = (string)response["id"]!;
                return response;
            });
            var advisor = new GeminiAdvisor(transport);
            var result = await advisor.ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(result.ApiRequests == 4 && result.QueryRounds == 3 && result.TotalTokens == 40 &&
                !result.Text.Contains("hidden reasoning", StringComparison.Ordinal) &&
                result.Queries.SequenceEqual(new[] { "list_forces", "list_ships", "get_ships" }), "Hierarchical consultation or usage changed");
            var next = await advisor.ConsultAsync(Session(assembly), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(next.Text == "Fresh answer" && next.ApiRequests == 1 && next.QueryRounds == 0, "A new consultation did not start independently");
        }));
        check("The adapter binds the active snapshot and rejects model attempts to choose a consultation", () => Async(async () =>
        {
            var session = Session(assembly);
            var transport = new Fake((n, body, cancellation) =>
            {
                var schema = body["tools"]![0]!["parameters"]!;
                Require(schema["properties"]!["consultation_id"] == null, "The model still has to echo host-owned identity");
                if (n == 1)
                {
                    var call = Call(session, "list_forces", "bad-snapshot");
                    call["args"]!["consultation_id"] = "a-prior-consultation";
                    return Reply(new JObject { ["functionCall"] = call });
                }
                var result = InteractionFixture.Result(body);
                Require((string?)result["consultation_id"] == session.Id, "The adapter selected a different snapshot");
                if (n == 2)
                {
                    Require((string?)result["status"] == "error" && (string?)result["error"]!["code"] == "invalid_arguments" &&
                        result["results"] == null, "A model-supplied snapshot override was accepted or hidden");
                    return Reply(new JObject { ["functionCall"] = Call(session, "list_forces", "current") });
                }
                Require((string?)result["status"] == "ok" && result["results"]![0]!["data"]!["items"]!.Count() == 1,
                    "A valid query without an echoed consultation ID did not execute");
                return Reply(new JObject { ["text"] = "現在の艦隊情報を確認しました。" });
            });
            var advice = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(advice.ApiRequests == 3 && advice.QueryRounds == 2, "Binding introduced an extra request or failed to count rejection");
        }));
        check("Parallel Gemini calls are grouped into one bounded round with exact response IDs", () => Async(async () =>
        {
            var session = Session(assembly);
            var modelParts = new[] {
                new JObject { ["functionCall"] = Call(session, "list_forces", "parallel-1"), ["thoughtSignature"] = "first-signature" },
                new JObject { ["functionCall"] = Call(session, "list_contacts", "parallel-2") }
            };
            var transport = new Fake((n, body, cancellation) =>
            {
                if (n == 1) return Reply(modelParts);
                var replies = body["input"]!;
                Require(body["previous_interaction_id"] != null && !body.ToString().Contains("first-signature", StringComparison.Ordinal), "Parallel continuation resent model history");
                Require(replies.Count() == 2 && (string?)replies[0]!["call_id"] == "parallel-1" &&
                    (string?)replies[1]!["call_id"] == "parallel-2" &&
                    (string?)InteractionFixture.Result(body, 1)["results"]![0]!["query"] == "list_contacts", "Parallel result mapping changed");
                return Reply(new JObject { ["text"] = "観測情報を確認しました。" });
            });
            var result = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(result.QueryRounds == 1 && result.ApiRequests == 2, "Parallel calls consumed extra rounds");
        }));
        check("The final API request disables tools after five query rounds and missing usage stays unknown", () => Async(async () =>
        {
            var session = Session(assembly);
            var transport = new Fake((n, body, cancellation) =>
            {
                if (n <= AdvisorQueryLimits.DefaultMaxRounds) return Reply(new JObject { ["functionCall"] = Call(session, "catalog", "round-" + n) });
                Require((string?)body["generation_config"]!["tool_choice"] == "none", "Tools still enabled after query limit");
                var response = Reply(new JObject { ["text"] = "取得上限の範囲で回答します。" }); response.Remove("usage"); return response;
            });
            var result = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None);
            Require(result.ApiRequests == (AdvisorQueryLimits.DefaultMaxRounds + 1) && result.QueryRounds == 5 && result.TotalTokens == null, "Wrong API cap or fabricated usage");
            var bad = Session(assembly);
            var repeated = new Fake((n, b, c) => Reply(new JObject { ["functionCall"] = Call(bad, "catalog", "again-" + n) }));
            await ThrowsCodeAsync("unexpected_function_call", () => new GeminiAdvisor(repeated).ConsultAsync(bad, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
            Require(repeated.Calls == (AdvisorQueryLimits.DefaultMaxRounds + 1), "Extra billable retry after disabled tools");
        }));
        check("Saved history limits send only the latest complete exchanges, including zero, without deleting visible conversation", () => Async(async () =>
        {
            var chat = new AdvisorConversation();
            for (var i = 0; i < 20; i++) chat.Begin(new AdvisorQuestion("質問" + i), "Advisor " + i).Complete("回答" + i);
            var history = chat.Exchanges.ToArray();
            foreach (var count in new int?[] { null, 0, 1, 3, 20 })
            {
                var options = AdvisorOptions.Parse(System.Text.Encoding.UTF8.GetString(new AdvisorOptions(historyExchanges: count ?? 3).Data()));
                var handler = new Handler(async (request, token) =>
                {
                    var body = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
                    var input = InteractionFixture.Initial(body);
                    var sent = input["prior_dialogue"]!.ToArray();
                    var expected = history.TakeLast(options.HistoryExchanges).ToArray();
                    Require(sent.Length == expected.Length && sent.Select((row, i) =>
                        (string?)row["question"] == expected[i].Question && (string?)row["answer"] == expected[i].Answer &&
                        (string?)row["advisor_name"] == expected[i].AdvisorName).All(x => x),
                        "The configured limit sent too many, too few, out-of-order or mismatched past exchanges");
                    Require((int)input["omitted_prior_exchange_count"]! == history.Length - sent.Length &&
                        input["situation"]!["victory_conditions"] != null, "History selection changed current evidence or omission counts");
                    if (options.HistoryExchanges == 0)
                        Require(!input.ToString().Contains("質問", StringComparison.Ordinal) && !input.ToString().Contains("回答", StringComparison.Ordinal),
                            "Past dialogue was embedded elsewhere despite disabling history");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply(new JObject { ["text"] = "Answer" }).ToString(Formatting.None)) };
                });
                var session = Session(assembly);
                if (count.HasValue)
                    await AdvisorConsultation.RunAsync("unused-history-test", session, Settings(), new AdvisorQuestion("Current question"), history,
                        CancellationToken.None, _ => new GeminiTransport(handler), historyExchanges: options.HistoryExchanges);
                else
                    await AdvisorConsultation.RunAsync("unused-history-test", session, Settings(), new AdvisorQuestion("Current question"), history,
                        CancellationToken.None, _ => new GeminiTransport(handler));
                Require(handler.Calls == 1 && chat.Turns.Count == 20 && chat.Exchanges.SequenceEqual(history),
                    "Selecting history caused extra API calls or removed visible dialogue");
            }
        }));
        check("Shortcut questions and history use internal English while display and reply languages stay independent", () => Async(async () =>
        {
            foreach (var language in new[] { "ja", "fr" })
            {
                var ui = AdvisorText.Load(language);
                var chat = new AdvisorConversation();
                foreach (var shortcut in new[] { AdvisorShortcut.NextSteps, AdvisorShortcut.MoreDetail, AdvisorShortcut.KeyRisks })
                {
                    var request = shortcut.Prepare(ui[shortcut.LabelKey]);
                    var prior = chat.Exchanges.ToArray();
                    var session = new AdvisorQuerySession(QueryTests.Capture(new SituationTests.Fixture(assembly)), language, request.Text);
                    var transport = new Fake((n, body, c) =>
                    {
                        var input = InteractionFixture.Initial(body);
                        Require((string?)input["question"] == request.Text && (string?)input["reply_language"] == language,
                            "The button caption or UI fallback locale replaced the actual question or reply language");
                        var history = input["prior_dialogue"]!.ToArray();
                        Require(history.Length == prior.Length && history.Select((h, i) =>
                            (string?)h["question"] == prior[i].Question && (string?)h["question"] != prior[i].DisplayQuestion).All(x => x),
                            "Display-only captions entered Gemini history");
                        return Reply(new JObject { ["text"] = "Reply / 回答" });
                    });
                    var answer = await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), prior, CancellationToken.None);
                    Require(transport.Calls == 1 && chat.TryAdd(request, answer.Text, out _), "The shortcut did not complete through the normal provider");
                }
            }
        }));
        check("Cumulative request budget prevents a network call before oversized history is transmitted", () => Async(async () =>
        {
            var transport = new Fake((n, b, c) => throw new InvalidOperationException("Network should not be called"));
            var history = new[] { new ChatExchange("以前の質問", new string('報', 360_000)) };
            await ThrowsCodeAsync("request_budget", () => new GeminiAdvisor(transport).ConsultAsync(Session(assembly), Settings(), history, CancellationToken.None));
            Require(transport.Calls == 0, "Oversized request was sent");
        }));
        check("Incomplete or blocked generations cannot be displayed as successful advice", () => Async(async () =>
        {
            foreach (var (finish, code) in new[] { ("MAX_TOKENS", "output_limit"), ("SAFETY", "generation_failed") })
            {
                var transport = new Fake((n, b, c) => { var r = Reply(new JObject { ["text"] = "partial" }); r["status"] = finish == "MAX_TOKENS" ? "incomplete" : "failed"; return r; });
                await ThrowsCodeAsync(code, () => new GeminiAdvisor(transport).ConsultAsync(Session(assembly), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
            }
            var blocked = new Fake((n, b, c) => new JObject { ["status"] = "failed" });
            await ThrowsCodeAsync("generation_failed", () => new GeminiAdvisor(blocked).ConsultAsync(Session(assembly), Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
        }));
        check("Missing or duplicate function-call identities are rejected before any situation query executes", () => Async(async () =>
        {
            foreach (var malformed in new[] { "missing_id", "duplicate_id", "invalid_arguments" })
            {
                var session = Session(assembly);
                var call = new JObject { ["type"] = "function_call", ["id"] = "call-1", ["name"] = "query_situation",
                    ["arguments"] = new JObject { ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) } };
                if (malformed == "missing_id") call.Remove("id");
                if (malformed == "invalid_arguments") call["arguments"] = "not an argument object";
                var steps = new JArray(call);
                if (malformed == "duplicate_id") steps.Add(call.DeepClone());
                var fake = new Fake((_, _, _) => new JObject { ["id"] = "interaction-1", ["status"] = "requires_action", ["steps"] = steps.DeepClone() });
                await ThrowsCodeAsync("invalid_function_call", () => new GeminiAdvisor(fake).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), CancellationToken.None));
                Require(fake.Calls == 1 && session.RoundsUsed == 0, "An ambiguous function call executed a query or triggered another billable request");
            }
        }));
        check("Cancellation after an API response prevents query execution and subsequent requests", () => Async(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var session = Session(assembly);
            var transport = new Fake((n, b, c) => { cancellation.Cancel(); return Reply(new JObject { ["functionCall"] = Call(session, "catalog", "cancel") }); });
            try { await new GeminiAdvisor(transport).ConsultAsync(session, Settings(), Array.Empty<ChatExchange>(), cancellation.Token); }
            catch (OperationCanceledException)
            { Require(transport.Calls == 1 && session.RoundsUsed == 0, "Cancelled work continued"); return; }
            throw new InvalidOperationException("Cancellation was ignored");
        }));
        check("Retired background jobs discard late answers and expected errors remain recoverable", () => Async(async () =>
        {
            var source = new TaskCompletionSource<AdviceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new AdvisorJob(c => source.Task, _ => { });
            job.Dispose();
            source.SetResult(new AdviceResult("late answer", 1, 0, null));
            var done = await job.Completion;
            Require(done.IsCancelled && done.Result == null, "Late answer survived cancellation");
            try { job.TakeCompleted(); } catch (InvalidOperationException) { goto rejected; }
            throw new InvalidOperationException("Retired job could still update UI");
            rejected:
            using var failed = new AdvisorJob(c => throw new AdvisorConnectionException("rate_limited", 429), _ => { });
            Require((await failed.Completion).Error?.Code == "rate_limited", "Recoverable provider error became fatal");
        }));
        check("Unexpected background source errors remain visible as unexpected failures", () => Async(async () =>
        {
            using var failed = new AdvisorJob(c => throw new InvalidOperationException("source classification error"), _ => { });
            var result = await failed.Completion;
            Require(result.UnexpectedError is InvalidOperationException && result.Error == null, "Unexpected error was disguised as provider failure");
            failed.TakeCompleted();
        }));
    }

    private static AdvisorSettings Settings() => AdvisorSettings.Parse("{\"api_key\":\"" + MockKey + "\"}");
    private static AdvisorQuerySession Session(Assembly assembly) => new(QueryTests.Capture(new SituationTests.Fixture(assembly)), "ja", "護衛隊の状態は？");
    private static JObject Call(AdvisorQuerySession session, string name, string id, string? key = null, JToken? value = null) => new()
    {
        ["id"] = id, ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(QueryTests.Q(name, key, value)) }
    };
    private static JObject Reply(params JObject[] parts) => InteractionFixture.Reply(parts);
    private static void Async(Func<Task> run) => run().GetAwaiter().GetResult();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ThrowsCode(string code, Action run)
    {
        try { run(); } catch (AdvisorConnectionException e) { Require(e.Code == code && !e.ToString().Contains(MockKey, StringComparison.Ordinal), "Wrong or unsafe error"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task ThrowsCodeAsync(string code, Func<Task> run)
    {
        try { await run(); } catch (AdvisorConnectionException e) { Require(e.Code == code && !e.ToString().Contains(MockKey, StringComparison.Ordinal), "Wrong or unsafe error: " + e.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private sealed class Fake : IGeminiTransport
    {
        private readonly Func<int, JObject, CancellationToken, JObject> generate;
        internal int Calls;
        internal Fake(Func<int, JObject, CancellationToken, JObject> generate) { this.generate = generate; }
        public Task<JObject> GenerateAsync(AdvisorSettings s, JObject b, CancellationToken c) => Task.FromResult(generate(++Calls, b, c));
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal int Calls;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
