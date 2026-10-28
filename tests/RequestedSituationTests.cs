using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;

internal static partial class ProviderTests
{
    private static AiToolCall FullSituationCall(string id) => new(id, "get_situation", new JObject());

    private static void RunRequestedSituation(Action<string, Action> check)
    {
        check("Requested delivery withholds automatic situation context and does not export data for a greeting", () => Async(async () =>
        {
            var knowledge = new SnapshotSpy { AutomaticText = new string('x', AdvisorQueryLimits.MaxTotalBytes) };
            var run = new AdvisorLocalRun();
            Task<AiReply> Send(FakeSession session, int _, bool __, CancellationToken ___)
            {
                var data = session.Input.Data;
                Require(data["situation"] == null && data["situation_data"] == null && data["knowledge_catalog"] == null &&
                    data["referenced_units"] == null && data["contact_assessment"] == null && data["aviation_semantics"] == null &&
                    (string?)data["question"] == "Question" && (string?)data["common_prompt"] == "Style" &&
                    data["prior_dialogue"]?.Count() == 1 && (string?)data["interaction_mode"] == "on_request",
                    "Automatic game evidence leaked or user-supplied context was removed");
                Require(session.Input.Tools.Count == 1 && RequestSchemaFixture.Accepts(session.Input.Tools[0]["parameters"]!, FullSituationCall("check").Arguments),
                    "A compatible full-situation request was not available");
                return Task.FromResult(new AiReply("Hello", Array.Empty<AiToolCall>(), 1));
            }
            var provider = new FakeProvider("greeting", Send);
            var context = AdvisorContext.Create(run.Observe(knowledge), new[] { new ChatExchange("Earlier", "Answer") },
                commonPrompt: "Style", preparedInput: run.CaptureInput);
            var result = await new AdvisorEngine(provider, delivery: AdvisorSituationDelivery.OnRequest).ConsultAsync(context, CancellationToken.None);
            Require(result.Completed && result.ApiRequests == 1 && knowledge.Exports == 0 && knowledge.RoundsUsed == 0 &&
                run.Snapshot()["retrievals"]?.Count() == 0, "Greeting caused an eager full export or charged an unused read");
        }));
        check("Requested full data uses permission-filtered projections once, including responses above the partial-read ceiling", () => Async(async () =>
        {
            var policy = AdvisorInformation.All.With(InformationKind.BattleFuel, false).With(InformationKind.BattleNeutralContacts, false)
                .With(InformationKind.BattleDestroyedContacts, false).With(InformationKind.BattleBriefing, false);
            var knowledge = new SnapshotSpy(DeliveryKnowledge(policy, new string('p', 55000)));
            var recording = new AdvisorLocalRun();
            var context = AdvisorContext.Create(recording.Observe(knowledge), Array.Empty<ChatExchange>());
            JObject? full = null;
            var provider = new FakeProvider("full", (session, send, _, _) =>
            {
                if (send == 1) return Task.FromResult(Reply(FullSituationCall("first"), FullSituationCall("same-response")));
                if (send == 2)
                {
                    full = (JObject)session.Results[0].Data["results"]![0]!["data"]!;
                    Require(JsonPayload.Bytes(full) > AdvisorQueryLimits.MaxResponseBytes && full["situation"]?["overview"] != null &&
                        full["referenced_units"] != null && full["contact_assessment"] != null && full["aviation_semantics"] != null,
                        "Full data was truncated, missing its header or missing deferred game context");
                    var rows = (JArray)full["situation_data"]!;
                    Require(rows.Where(r => (string?)r["query"] == "get_units").Sum(r => r["data"]!.Count()) == 25 &&
                        rows.Where(r => (string?)r["query"] == "get_unit_capabilities").SelectMany(r => r["data"]!["units"]!).All(u => u["fuel"] == null) &&
                        rows.Single(r => (string?)r["query"] == "list_contacts")["data"]!["items"]!.Count() == 23 &&
                        rows.All(r => (string?)r["query"] != "get_briefing"), "Requested delivery bypassed filters or lost later pages");
                    Require((string?)session.Results[1].Data["results"]?[0]?["data"]?["status"] == "already_supplied", "Parallel calls duplicated full data");
                    return Task.FromResult(Reply(FullSituationCall("later-response")));
                }
                Require((string?)session.Results.Single().Data["results"]?[0]?["data"]?["status"] == "already_supplied", "A later call duplicated full data");
                return Task.FromResult(new AiReply("Answer", Array.Empty<AiToolCall>(), 1));
            });
            var result = await new AdvisorEngine(provider, delivery: AdvisorSituationDelivery.OnRequest).ConsultAsync(context, CancellationToken.None);
            var reads = (JArray)recording.Snapshot()["retrievals"]!;
            Require(result.Completed && result.ApiRequests == 3 && knowledge.Exports == 1 && knowledge.RoundsUsed == 0 && reads.Count == 1 &&
                JToken.DeepEquals(reads[0]?["response"]?["results"]?[0]?["data"], full), "Successful delivery was not recorded exactly once");
            full!["situation"]!["mission_display_name"] = "Edited by caller";
            Require(!knowledge.InitialInput().ToString().Contains("Edited by caller", StringComparison.Ordinal) &&
                !recording.Snapshot().ToString().Contains("Edited by caller", StringComparison.Ordinal), "Result mutation changed the frozen source or diagnostic evidence");
        }));
        check("Operation lookup does not trigger full delivery and mixed full/operation reads retain their individual results", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("requested-mixed", AdvisorPrompts.Current)!;
            var knowledge = new SnapshotSpy(); var context = AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations);
            context.PrepareStandardInput(AdvisorSituationDelivery.OnRequest);
            AdvisorToolDispatch Dispatch(params AiToolCall[] calls) => OperationGatewayTests.Finish(gateway,
                context.Tools.ExecuteAsync(calls, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            var list = Dispatch(new AiToolCall("list", "list_operations", new JObject()));
            Require(knowledge.Exports == 0 && list.Results.Single().Data["results"]?[0]?["data"] != null, "Catalog inspection exported unrelated situation data");
            var mixed = Dispatch(new AiToolCall("definition", "describe_operation", new JObject { ["operation_id"] = operation.Id }),
                FullSituationCall("situation"), new AiToolCall("candidates", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id }));
            Require(mixed.Results.Select(r => r.Id).SequenceEqual(new[] { "definition", "situation", "candidates" }) &&
                mixed.Results.All(r => (string?)r.Data["status"] == "ok") && mixed.Results[1].Data["results"]?[0]?["data"]?["situation_data"] != null &&
                mixed.Results[2].Data["results"]?[0]?["data"]?["candidates"]?.Count() == 2 && knowledge.Exports == 1 && operation.Writes == 0,
                "Mixed full/operation reads lost identity or executed an order");
        });
        check("Invalid full reads, cancelled mixed reads and rejected payloads do not mark a snapshot as supplied", () =>
        {
            var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            using var operations = gateway.CreateSession("requested-cancel", AdvisorPrompts.Current)!;
            var knowledge = new SnapshotSpy(); var recording = new AdvisorLocalRun();
            var context = AdvisorContext.Create(recording.Observe(knowledge), Array.Empty<ChatExchange>(), operations: operations);
            context.PrepareStandardInput(AdvisorSituationDelivery.OnRequest);
            var malformed = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] {
                new AiToolCall("bad", "get_situation", new JObject { ["ids"] = new JArray("own_unit_1") }) }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            Require((string?)malformed.Results.Single().Data["error"]?["code"] == "invalid_arguments" && knowledge.Exports == 0, "Malformed full read broadened into an export");
            using var cancel = new CancellationTokenSource(); operation.OnCandidates = cancel.Cancel;
            try
            {
                OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { FullSituationCall("full"),
                    new AiToolCall("cancel", "get_operation_candidates", new JObject { ["operation_id"] = operation.Id }) }, cancel.Token, (_, _) => { }, (_, _) => { }));
                throw new InvalidOperationException("Cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            Require(recording.Snapshot()["retrievals"]?.Count() == 0, "Cancelled delivery was recorded as completed");
            operation.OnCandidates = null;
            var retry = OperationGatewayTests.Finish(gateway, context.Tools.ExecuteAsync(new[] { FullSituationCall("retry") }, CancellationToken.None, (_, _) => { }, (_, _) => { }));
            Require(retry.Results.Single().Data["results"]?[0]?["data"]?["situation_data"] != null && recording.Snapshot()["retrievals"]?.Count() == 1,
                "Cancellation consumed the one full delivery");
            var large = new SnapshotSpy(DeliveryKnowledge(referenceText: new string('x', AdvisorQueryLimits.MaxTotalBytes)));
            var largeRun = new AdvisorLocalRun(); var largeContext = AdvisorContext.Create(largeRun.Observe(large), Array.Empty<ChatExchange>());
            largeContext.PrepareStandardInput(AdvisorSituationDelivery.OnRequest);
            for (var i = 0; i < 2; i++)
            {
                var refused = largeContext.Reads.ExecuteAsync(new JObject { ["requests"] = new JArray(new JObject { ["source"] = "situation", ["query"] = "get_situation" }) }, CancellationToken.None).GetAwaiter().GetResult();
                Require((string?)refused["error"]?["code"] == "payload_limit" && largeRun.Snapshot()["retrievals"]?.Count() == 0,
                    "Oversized data was truncated, sent or falsely marked as supplied");
            }
        });
        check("Requested snapshots survive standard provider handoffs without replaying full data or leaking private state", () => Async(async () =>
        {
            var knowledge = new SnapshotSpy(); var recording = new AdvisorLocalRun();
            Task<AiReply> Main(FakeSession session, int n, bool _, CancellationToken __)
            {
                if (n == 1) return Task.FromResult(Reply(FullSituationCall("main")));
                session.Input.Data["private_provider_state"] = "opaque";
                throw new AiConnectionException("rate_limited", 429);
            }
            Task<AiReply> Sub(FakeSession session, int n, bool _, CancellationToken __)
            {
                Require(session.Continuation?.Retrievals.Count == 1 &&
                    session.Continuation.Retrievals[0]?["response"]?["results"]?[0]?["data"]?["situation_data"] != null &&
                    session.Input.Data["situation"] == null && session.Input.Data["private_provider_state"] == null, "Fallback lost executed evidence or copied provider-private state");
                if (n == 1) return Task.FromResult(Reply(FullSituationCall("sub")));
                Require((string?)session.Results.Single().Data["results"]?[0]?["data"]?["status"] == "already_supplied", "Fallback resent the same full data");
                return Task.FromResult(new AiReply("Continued", Array.Empty<AiToolCall>(), 1));
            }
            var main = new FakeProvider("main", Main);
            var sub = new FakeProvider("sub", Sub);
            var answer = await new AdvisorEngine(main, sub, delivery: AdvisorSituationDelivery.OnRequest).ConsultAsync(
                AdvisorContext.Create(recording.Observe(knowledge), Array.Empty<ChatExchange>()), CancellationToken.None);
            Require(answer.Completed && answer.ApiRequests == 4 && answer.TotalTokens == null && knowledge.Exports == 1 &&
                recording.Snapshot()["retrievals"]?.Count() == 1, "Handoff reset delivery state or lost diagnostic evidence");
        }));
        check("An independent fallback receives successfully delivered full evidence in its host retrieval history", () => Async(async () =>
        {
            var independent = IndependentClient(); using var host = new PublicHost(independent);
            var settings = NanoDraft(); settings.Main.SituationDelivery = AdvisorSituationDelivery.OnRequest;
            var sub = ExternalConfiguration(independent, "independent-sub"); settings.Put(new AdvisorApiConfigurationDraft(sub)); settings.SubId = sub.Id;
            independent.Consult = (_, _, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                Require(context.Continuation != null && context.Continuation.RetrievalsJson.Contains("situation_data", StringComparison.Ordinal),
                    "The independent handoff lost the full-read receipt");
                return Task.FromResult(new Api.AdvisorAnswer("Independent answer"));
            };
            var sends = 0;
            var handler = new BackendHttpHandler((_, _) => Task.FromResult(++sends == 1
                ? JsonResponse(NanoReply("", NanoCall("full", "", "get_situation", new JObject())))
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new System.Net.Http.StringContent("busy") }));
            var knowledge = new SnapshotSpy(); var recording = new AdvisorLocalRun();
            var answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(settings, transportFactory: trace => new AiHttpTransport(handler, trace)),
                AdvisorContext.Create(recording.Observe(knowledge), Array.Empty<ChatExchange>()), "unused", new AdvisorQuestion("Question"), CancellationToken.None);
            Require(answer.Completed && answer.Text == "Independent answer" && knowledge.Exports == 1 && recording.Snapshot()["retrievals"]?.Count() == 1,
                "Standard-to-independent fallback duplicated or omitted successfully read evidence");
        }));
    }

    private sealed class SnapshotSpy : IAdvisorQuerySession, IAdvisorSnapshotSource
    {
        private readonly BattleQuerySession inner;
        internal int Exports;
        internal string AutomaticText = "Known context";
        internal SnapshotSpy(BattleQuerySession? inner = null) { this.inner = inner ?? DeliveryKnowledge(); }
        public string Id => inner.Id;
        public int RoundsUsed => inner.RoundsUsed;
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        public JObject InitialInput()
        {
            var result = inner.InitialInput();
            result["referenced_units"] = new JArray(new JObject { ["id"] = "own_unit_1", ["details"] = AutomaticText });
            result["contact_assessment"] = new JObject { ["track"] = "7001" };
            result["aviation_semantics"] = new JObject { ["available"] = true };
            return result;
        }
        public JObject Execute(JObject request) => inner.Execute(request);
        public JArray ExportSituation() { Exports++; return inner.ExportSituation(); }
    }
}
