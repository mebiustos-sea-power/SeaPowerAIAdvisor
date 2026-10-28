using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;

internal static partial class ProviderTests
{
    private static void RunOperationPermission(Action<string, Action> check)
    {
        check("Advisory shortcuts never open operation services and retain situation advice across delivery modes", () => Async(async () =>
        {
            var opened = 0; var operation = new LookupOperation(); var registry = new AdvisorOperations(_ => true); registry.Register(operation);
            using var gateway = new AdvisorOperationGateway(registry);
            var source = new BattleAdvisorSource(() => throw new InvalidOperationException("Unexpected capture"), () => { opened++; return gateway; });
            foreach (var shortcut in new[] { AdvisorShortcut.NextSteps, AdvisorShortcut.KeyRisks, AdvisorShortcut.RadarOperations, AdvisorShortcut.OwnUnits, AdvisorShortcut.Contacts, AdvisorShortcut.MoreDetail })
            foreach (var delivery in new[] { AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnRequest })
            {
                var request = shortcut.Prepare("Advice"); var knowledge = DeliveryKnowledge();
                using var operations = AdvisorConsultation.CreateOperations(request, source, "advisory", knowledge.Prompts, AdvisorOperationReferences.Empty);
                Require(operations == null && opened == 0, "An advisory shortcut opened a command gateway");
                Task<AiReply> Send(FakeSession session, int n, bool _, CancellationToken __)
                {
                    Require(!session.Input.Tools.Any(t => (string?)t["name"] is "list_operations" or "describe_operation" or "get_operation_candidates" or "propose_operation_plans" or "game_operations"),
                        "An operation tool was advertised in an advisory consultation");
                    if (n == 1 && delivery != AdvisorSituationDelivery.Upfront)
                        return Task.FromResult(Reply(delivery == AdvisorSituationDelivery.OnRequest ? FullSituationCall("situation") :
                            Call("briefing", "get_briefing")));
                    Require(delivery == AdvisorSituationDelivery.Upfront ? session.Input.Data["situation_data"] != null :
                        session.Results.Single().Data["results"]?[0]?["data"] != null, "Disabling commands also disabled situation evidence");
                    return Task.FromResult(new AiReply("Situation advice", Array.Empty<AiToolCall>(), 1));
                }
                var provider = new FakeProvider("standard", Send);
                var answer = await new AdvisorEngine(provider, delivery: delivery).ConsultAsync(AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None);
                Require(answer.Completed && operation.Writes == 0 && opened == 0, "Advice was blocked or a command escaped the disabled policy");
            }
            var denied = AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>());
            denied.PrepareStandardInput();
            try
            {
                await denied.Tools.ExecuteAsync(new[] { new AiToolCall("forged", "list_operations", new JObject()) }, CancellationToken.None, (_, _) => { }, (_, _) => { });
                throw new InvalidOperationException("An unadvertised operation was accepted");
            }
            catch (AiConnectionException error) when (error.Code == "invalid_function_call") { }
            Require(opened == 0 && operation.Writes == 0, "A forged command opened operation access");
        }));
        check("Permitted custom shortcuts retain approval, manual requests remain permitted and neither can override WIP or force execution", () =>
        {
            var enabled = false; var unit = new OperationHelicopter();
            var registry = new AdvisorOperations(_ => enabled); registry.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit }));
            using var gateway = new AdvisorOperationGateway(registry);
            var source = new BattleAdvisorSource(() => throw new InvalidOperationException("Unexpected capture"), () => gateway);
            var custom = new AdvisorShortcut("custom.drop", "next_steps", allowOperations: true).Prepare("Drop buoys");
            var manual = new AdvisorDraft(); manual.Add(42, unit.Name); var manualRequest = manual.Prepare("Drop one buoy");
            var knowledge = DeliveryKnowledge();
            foreach (var request in new[] { custom, manualRequest })
                Require(AdvisorConsultation.CreateOperations(request, source, "wip-off", knowledge.Prompts, AdvisorOperationReferences.Empty) == null,
                    "A permitted request enabled WIP implicitly");
            enabled = true;
            using (var idle = AdvisorConsultation.CreateOperations(manualRequest, source, "manual", knowledge.Prompts, AdvisorOperationReferences.Empty))
            {
                Require(idle != null, "Manual requests lost operation access");
                var provider = new FakeProvider("optional", (session, _, _, _) =>
                {
                    Require(session.Input.Tools.Any(t => (string?)t["name"] == "propose_operation_plans"), "Permitted tools are missing");
                    return Task.FromResult(new AiReply("A normal answer without commands", Array.Empty<AiToolCall>(), 1));
                });
                var answer = OperationGatewayTests.Finish(gateway, new AdvisorEngine(provider).ConsultAsync(
                    AdvisorContext.Create(knowledge, Array.Empty<ChatExchange>(), operations: idle), CancellationToken.None));
                var idleProposal = registry.FinishProposal("manual", true);
                Require(answer.Completed && idleProposal != null && idleProposal.Orders.Count == 0 &&
                    idleProposal.State == AdvisorProposalState.Rejected && unit.Calls == 0,
                    "Permission forced an operation or created a reviewable proposal");
            }
            using var operations = AdvisorConsultation.CreateOperations(custom, source, "custom", knowledge.Prompts, AdvisorOperationReferences.Empty);
            Require(operations != null, "A custom shortcut could not opt into operations");
            var executor = new FakeProvider("custom", (session, n, _, _) => Task.FromResult(n switch {
                1 => OperationReply("definition", new JObject { ["action"] = "describe", ["operation_id"] = SonobuoyDropExtension.OperationId }),
                2 => ProposalReply("drop", BuoyRequest()),
                _ => new AiReply("Awaiting approval", Array.Empty<AiToolCall>(), 1) }));
            var result = OperationGatewayTests.Finish(gateway, Task.Run(() => new AdvisorEngine(executor).ConsultAsync(
                AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None)));
            Require(result.Completed && unit.Calls == 0, "Permitted shortcut bypassed approval");
            var proposal = registry.FinishProposal("custom", true)!; Require(proposal.Plans.Count == 2 && proposal.Orders.Count == 2, "The custom proposal was lost");
            registry.Approve(proposal, new[] { "a" }); Require(unit.Calls == 1, "Approval did not register exactly one order");
        });
        check("Disabled operation policy survives retry, standard provider switching and independent fallback", () => Async(async () =>
        {
            var original = new AdvisorQuestion("Question with target", "Visible question", false, new JObject { ["track_number"] = 7001 },
                draftText: "Question", units: new[] { (42, "Helicopter") });
            var request = original.WithOperations(false);
            Require(original.AllowOperations && !request.AllowOperations && request.Text == original.Text && request.DisplayBody == original.DisplayBody &&
                request.Units.SequenceEqual(original.Units) && JToken.DeepEquals(request.Context, original.Context), "Changing permission lost question or badge context");
            var chat = new AdvisorConversation(); var turn = chat.Begin(request); turn.Fail(AdvisorNotice.Connection(new AiConnectionException("request_timeout"))); chat.Retry(turn);
            Require(ReferenceEquals(turn.Request, request) && !turn.Request.AllowOperations, "Retry changed the accepted operation policy");
            var opened = 0;
            var source = new BattleAdvisorSource(() => throw new Exception("Unexpected capture"), () => { opened++; throw new InvalidOperationException("Disabled operations were opened"); });
            using var operations = AdvisorConsultation.CreateOperations(turn.Request, source, turn.OperationId, AdvisorPrompts.Current, AdvisorOperationReferences.Empty);
            var main = new FakeProvider("main", (_, _, _, _) => throw new AiConnectionException("rate_limited", 429));
            var sub = new FakeProvider("sub", (s, _, _, _) =>
            {
                Require(s.Input.Tools.All(t => (string?)t["name"] != "game_operations") &&
                    !s.Input.Tools.Any(t => t.ToString().Contains("\"operation_id\"", StringComparison.Ordinal)), "A provider switch restored operation access");
                return Task.FromResult(new AiReply("Fallback advice", Array.Empty<AiToolCall>(), 1));
            });
            var answer = await new AdvisorEngine(main, sub).ConsultAsync(AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>(), operations: operations), CancellationToken.None);
            Require(answer.Completed && opened == 0, "Fallback opened a disabled operation service");
            var external = IndependentClient(); using var host = new PublicHost(external);
            external.Consult = (_, _, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                Require(context.Operations == null &&
                    context.Input.ToolsJson.All(t => !t.Contains("propose_operation_plans", StringComparison.Ordinal)), "An independent fallback received disabled commands");
                return Task.FromResult(new Api.AdvisorAnswer("Independent advice"));
            };
            var settings = NanoDraft(); var ext = ExternalConfiguration(external, "sub"); settings.Put(new AdvisorApiConfigurationDraft(ext)); settings.SubId = ext.Id;
            var handler = new BackendHttpHandler((_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) {
                Content = new System.Net.Http.StringContent("busy") }));
            answer = await AdvisorConsultation.RunAsync(AdvisorServices.Prepare(settings, transportFactory: trace => new AiHttpTransport(handler, trace)),
                AdvisorContext.Create(DeliveryKnowledge(), Array.Empty<ChatExchange>(), operations: operations), "unused", request, CancellationToken.None);
            Require(answer.Completed && answer.Text == "Independent advice" && opened == 0, "Independent fallback changed command policy");
        }));
    }
}
