using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal static class OperationApprovalTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("A twelve-drop unit plan remains pending until approval, appends in order and cannot execute twice", () =>
        {
            var unit = new Helicopter(); var service = Buoys(unit); service.BeginProposal("turn");
            var requests = new[] { DropPlan(service, "turn", 0, Enumerable.Range(0, 12).ToArray()) };
            foreach (var request in requests)
                Require((string?)OperationSubmissionFixture.Submit(service, "turn", request)["status"] == "proposed", "Valid buoy proposal rejected");
            Require(unit.Latitudes.Count == 0 && unit.Queue.Count == 1, "Proposal changed the game before approval");
            var proposal = service.FinishProposal("turn", true)!;
            Require(proposal.State == AdvisorProposalState.Pending && proposal.Orders.Count == 1, "Requests were not held together");
            foreach (var language in new[] { "en", "ja" })
            {
                var description = service.DescribeProposal(proposal, language);
                Require(description.Contains("Hobby 1", StringComparison.Ordinal) && description.Contains("12", StringComparison.Ordinal) &&
                    description.Contains("Custom buoy", StringComparison.Ordinal) && description.Contains("26.51100", StringComparison.Ordinal),
                    "The command summary lost the target, total, ammunition or last destination");
            }
            // Detached payloads cannot change the eventual approved work.
            requests[0]["arguments"]!["drops"]![0]!["latitude"] = -80;
            proposal.Orders[0].Arguments["drops"]![0]!["latitude"] = 80;
            unit.Depth = "below_layer";
            var preview = service.PreviewProposal(proposal, "en");
            var points = preview.Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Point).ToArray();
            Require(points.Length == 12 && points.All(point => point.Label.StartsWith("Hobby 1 / ", StringComparison.Ordinal)),
                "The sonobuoy preview lost accepted drops or their target labels");
            var route = preview.Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line);
            Require(route.Coordinates.Count == 13 && route.Coordinates[0].Latitude == 26.4 && route.Coordinates[0].Longitude == 127.4 &&
                route.Coordinates.Skip(1).Select(p => (p.Latitude, p.Longitude)).SequenceEqual(points.Select(p => (p.Coordinates[0].Latitude, p.Coordinates[0].Longitude))),
                "The preview omitted the queued endpoint or changed the proposed drop order");
            service.Approve(proposal); service.Approve(proposal);
            Require(points.Select(point => point.Coordinates[0].Latitude).SequenceEqual(unit.Latitudes) &&
                points.All(point => point.Coordinates[0].Longitude == 127.5),
                "The approval preview differed from the registered coordinates or used caller-mutated arguments");
            Require(proposal.State == AdvisorProposalState.Registered && proposal.RegisteredCount == 1 && unit.Queue.Count == 13 &&
                unit.Queue[0] == "player waypoint" && unit.Latitudes.SequenceEqual(Enumerable.Range(0, 12).Select(i => 26.5 + i * .001)) &&
                unit.Depths.All(depth => depth == "below_layer"), "Approval lost order, repeated a task, cleared the player route or overwrote depth");
            Require((string?)OperationSubmissionFixture.Submit(service, "turn", DropPlan(service, "turn", 0, Enumerable.Range(0, 12).ToArray()))["status"] == "registered" && unit.Latitudes.Count == 12,
                "Repeating the same AI request registered it twice");
        });
        check("Sonobuoy route previews keep each helicopter's live append point and proposal order without changing reservations", () =>
        {
            var first = new Helicopter();
            var second = new Helicopter { Id = 43, Name = "Hobby 2",
                ReservationRoute = new ReservationRoute(new AdvisorMapCoordinate(28, 128), null) };
            second.Queue.Clear();
            var units = new List<IBattleSonobuoyUnit> { first, second };
            var operation = SonobuoyFixture.Create(() => units); var service = new AdvisorOperations(() => true);
            service.Register(operation); service.BeginProposal("routes");
            var values = new[] { DropPlan(service, "routes", 0, 2, 0), DropPlan(service, "routes", 1, 9, 7) };
            values[1]["target_ref"] = "unit:43";
            OperationSubmissionFixture.Submit(service, "routes", values);
            var proposal = service.FinishProposal("routes", true)!;
            var preview = service.PreviewProposal(proposal, "en");
            var lines = preview.Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Line).ToArray();
            Require(lines.Length == 2 && lines[0].Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 26.4, 26.502, 26.5 }) &&
                lines[1].Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 28d, 26.509, 26.507 }),
                "Different helicopters were joined together or their drop order was optimized without permission");
            Require(lines.All(d => d.Color == 0xFFFFE060 && d.Width == 2) && preview.Drawings.Count == 6 &&
                first.Queue.Count == 1 && second.Queue.Count == 0,
                "Routes added an outline, changed focus or registered previewed orders");
            first.ReservationRoute = new ReservationRoute(new AdvisorMapCoordinate(27, 127), new AdvisorMapCoordinate(29, 129));
            var refreshed = service.PreviewProposal(proposal, "en");
            Require(refreshed.Drawings.First(d => d.Kind == AdvisorMapDrawingKind.Line).Coordinates[0].Latitude == 29,
                "Reopening a preview reused an old current position or queued endpoint");
            units.Remove(second);
            Expect(() => service.PreviewProposal(proposal, "en"), "target_unavailable");
            Require(first.Latitudes.Count == 0 && second.Latitudes.Count == 0, "A stale preview registered an order");
        });
        check("Sonobuoy previews respect the movement-plan disclosure setting without disabling the proposed drops", () =>
        {
            var unit = new Helicopter();
            var information = AdvisorInformation.Default.With(InformationKind.BattleMovementPlans, false);
            var service = new AdvisorOperations(() => true);
            service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit }, () => information));
            service.BeginProposal("hidden-route");
            OperationSubmissionFixture.Submit(service, "hidden-route", DropPlan(service, "hidden-route", 0, 0, 1));
            var proposal = service.FinishProposal("hidden-route", true)!;
            var line = service.PreviewProposal(proposal, "en").Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line);
            Require(line.Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 26.5, 26.501 }) && unit.Queue.Count == 1,
                "An excluded reservation endpoint was disclosed or previewing changed the queue");
            information = AdvisorInformation.Default;
            Require(service.PreviewProposal(proposal, "en").Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line).Coordinates[0].Latitude == 26.4,
                "Restoring movement disclosure kept using an old permission or route");
            service.Approve(proposal);
            Require(proposal.RegisteredCount == 1 && unit.Queue.Count == 3, "Movement disclosure changed reservation acceptance");
            service.Retire();
        });
        check("One drop still draws an arrival leg, while an unavailable start never becomes a zero coordinate", () =>
        {
            var unit = new Helicopter { ReservationRoute = new ReservationRoute(new AdvisorMapCoordinate(27, 128), null) };
            unit.Queue.Clear();
            var service = Buoys(unit); service.BeginProposal("one"); OperationSubmissionFixture.Submit(service, "one", Request(service, "one", 0));
            var proposal = service.FinishProposal("one", true)!;
            var preview = service.PreviewProposal(proposal, "en");
            Require(preview.Drawings.Count == 2 && preview.Drawings[0].Kind == AdvisorMapDrawingKind.Line &&
                preview.Drawings[0].Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 27d, 26.5 }), "A single drop omitted its arrival leg");
            unit.ReservationRoute = null;
            var unavailable = service.PreviewProposal(proposal, "en");
            Require(unavailable.Drawings.Count == 1 && unavailable.Drawings[0].Kind == AdvisorMapDrawingKind.Point && unit.Latitudes.Count == 0,
                "An unavailable start produced a false origin or changed the game");
        });
        check("Proposal controls stay compact under the original answer and become disabled after registration", () =>
        {
            var service = Buoys(new Helicopter()); var chat = new AdvisorConversation();
            var turn = chat.Begin(new AdvisorQuestion("Place buoys")); service.BeginProposal(turn.OperationId);
            OperationSubmissionFixture.Submit(service, turn.OperationId, DropPlan(service, turn.OperationId, 0, Enumerable.Range(0, 12).ToArray()));
            turn.Finish(new AdviceResult("Original AI answer", 2, 0, null), service);
            var text = AdvisorText.Load("en");
            XElement Button() => XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"))
                .Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ReviewProposal0");
            var button = Button();
            Require((string?)button.Attribute("Content") == text["operation.review"] && (string?)button.Attribute("IsEnabled") == "true" &&
                !AdvisorTranscript.Render(chat, text, "Adviser", "empty").Contains("26.51100", StringComparison.Ordinal),
                "The history omitted approval or expanded all operation details into the answer");
            var waiting = chat.Begin(new AdvisorQuestion("Another question"));
            Require((string?)Button().Attribute("IsEnabled") == "false", "Approval stayed active during another consultation");
            waiting.Cancel(); service.Approve(turn.Proposal!);
            Require((string?)Button().Attribute("Content") == text["operation.accept"] &&
                (string?)Button().Attribute("IsEnabled") == "false" && turn.Exchange!.Answer == "Original AI answer",
                "Approval added another answer, modified the AI text or left a repeated-execution button active");
        });
        check("Incomplete turns expire every unapproved operation without affecting earlier registrations or offering approval", () =>
        {
            foreach (var approval in new[] { true, false })
            foreach (var reason in new[] { AdvisorCompletionReason.RoundLimit, AdvisorCompletionReason.PayloadLimit })
            {
                var log = new List<string>(); var service = new AdvisorOperations(() => true);
                var operation = new Extension("custom.limit", approval, log); service.Register(operation);
                service.BeginProposal("earlier"); OperationSubmissionFixture.Submit(service, "earlier", Request(service, "earlier", 0, operation.Id));
                var previous = service.FinishProposal("earlier", true)!;
                if (approval) service.Approve(previous);
                var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("New plan"));
                var run = new AdvisorLocalRun(); run.Attach(turn); service.BeginProposal(turn.OperationId);
                OperationSubmissionFixture.Submit(service, turn.OperationId, Request(service, turn.OperationId, 1, operation.Id));
                var result = new AdviceResult("", 3, 1, 42, model: "model", completionReason: reason);
                run.Complete(result, "Connection"); turn.Finish(result, service); run.Finish();
                service.Approve(turn.Proposal!);
                Require(turn.State == AdvisorTurnState.Incomplete && turn.Proposal!.State == AdvisorProposalState.Cancelled &&
                    turn.Exchange == null && chat.Exchanges.Count == 0 && !chat.CanRetry(turn) &&
                    previous.State == AdvisorProposalState.Registered && log.SequenceEqual(new[] { "custom.limit:0" }),
                    "An incomplete turn auto-executed, could be approved, entered history or changed earlier reservations");
                foreach (var language in new[] { "en", "ja" })
                {
                    var text = AdvisorText.Load(language); var markup = AdvisorTranscript.Render(chat, text, "Officer", "empty");
                    Require(markup.Contains(text[reason == AdvisorCompletionReason.PayloadLimit ? "transcript.payload_limit" : "transcript.round_limit"], StringComparison.Ordinal) &&
                        markup.Contains(text["transcript.proposal_expired"], StringComparison.Ordinal) &&
                        !markup.Contains("ReviewProposal", StringComparison.Ordinal) && !markup.Contains("CopyAnswer", StringComparison.Ordinal),
                        "Incomplete history offered a partial plan or misreported its termination");
                }
                chat.Clear(); var saved = run.Snapshot();
                Require((string?)saved["state"] == "incomplete" && (string?)saved["answer"] == null &&
                    (string?)saved["result"]?["completion_reason"] == result.CompletionCode && (long?)saved["result"]?["reported_total_tokens"] == 42,
                    "Developer diagnostics lost the incomplete outcome or changed with history clearing");
            }
        });
        check("Mixed operations are held together, use their own summaries and retain original execution order", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(() => true);
            var first = new Extension("custom.first", false, log); var second = new Extension("custom.second", true, log);
            service.Register(first); service.Register(second); service.BeginProposal("mixed");
            OperationSubmissionFixture.Submit(service, "mixed", Request(service, "mixed", 0, first.Id),
                Request(service, "mixed", 1, second.Id), Request(service, "mixed", 2, first.Id));
            var proposal = service.FinishProposal("mixed", true)!;
            Require(log.Count == 0 && proposal.State == AdvisorProposalState.Pending, "An unchecked operation ran ahead of the required approval");
            var summary = service.DescribeProposal(proposal, "en");
            Require(summary.Contains("custom.first:2", StringComparison.Ordinal) && summary.Contains("custom.second:1", StringComparison.Ordinal),
                "The common layer assumed a unit type or constructed extension-specific text");
            service.Approve(proposal);
            Require(log.SequenceEqual(new[] { "custom.first:0", "custom.second:1", "custom.first:2" }), "Grouping summaries reordered execution");
            service.BeginProposal("automatic"); OperationSubmissionFixture.Submit(service, "automatic", Request(service, "automatic", 3, first.Id));
            Require(log.Count == 3, "An automatic operation executed before the full proposal was collected");
            Require(service.FinishProposal("automatic", true)!.State == AdvisorProposalState.Registered && log.Count == 4,
                "A proposal without approval-required operations demanded a user confirmation");
        });
        check("Approval revalidates combined inventory, ownership, disclosure and command permission before any native write", () =>
        {
            foreach (var change in new[] { "inventory", "target", "permission", "information" })
            {
                var enabled = true; var information = AdvisorInformation.Default; var unit = new Helicopter();
                var units = new List<IBattleSonobuoyUnit> { unit }; var service = new AdvisorOperations(() => enabled);
                service.Register(SonobuoyFixture.Create(() => units, () => information)); service.BeginProposal("check");
                OperationSubmissionFixture.Submit(service, "check", DropPlan(service, "check", 0, 0, 1, 2));
                var proposal = service.FinishProposal("check", true)!;
                if (change == "inventory") unit.Inventory = 2;
                if (change == "target") units.Clear();
                if (change == "permission") enabled = false;
                if (change == "information") information = information.With(InformationKind.BattleWeapons, false);
                service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode != null && unit.Queue.Count == 1 && unit.Latitudes.Count == 0,
                    "Stale " + change + " was applied or preflight partially registered the batch");
            }
        });
        check("Consultation failure, history removal and battle retirement invalidate proposals and late calls", () =>
        {
            foreach (var cause in new[] { "failure", "history", "battle" })
            {
                var unit = new Helicopter(); var service = Buoys(unit); service.BeginProposal("old");
                OperationSubmissionFixture.Submit(service, "old", Request(service, "old", 0)); var proposal = service.Proposal("old")!;
                if (cause == "failure") service.FinishProposal("old", false);
                else
                {
                    service.FinishProposal("old", true);
                    if (cause == "history") service.DiscardProposalsExcept(Array.Empty<string>()); else service.Retire();
                }
                Require(proposal.State == AdvisorProposalState.Cancelled && unit.Latitudes.Count == 0, "Invalidated proposal changed the game");
                try { service.Approve(proposal); } catch (AdvisorOperationException) { }
                try { OperationSubmissionFixture.Submit(service, "old", Request(service, "old", 1)); throw new Exception("Late proposal was accepted"); }
                catch (AdvisorOperationException) { }
                Require(unit.Queue.Count == 1, "A late worker or stale button revived discarded work");
            }
        });
        check("A proposal with known registration failure stops at the first failure without replaying completed orders", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(() => true);
            var operation = new Extension("custom.failure", true, log) { RejectAt = 1 }; service.Register(operation); service.BeginProposal("partial");
            OperationSubmissionFixture.Submit(service, "partial", Enumerable.Range(0, 3).Select(i => Request(service, "partial", i, operation.Id)).ToArray());
            var proposal = service.FinishProposal("partial", true)!; service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Partial && proposal.RegisteredCount == 1 && log.Count == 1 && proposal.ErrorCode == "native_rejected",
                "A failed batch claimed full success, continued with dependent work or repeated earlier work");
            var prior = service.PriorOrders("partial");
            Require(prior.Count == 1 && (string?)prior[0]?["response"]?["status"] == "partial" &&
                proposal.ExecutionOrders.Count == 3, "Retry history lost the partial submission");
        });
        check("Unknown native side effects remain visible and cannot be approved or submitted again", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(() => true);
            var operation = new Extension("custom.unknown", true, log) { ThrowAt = 1 }; service.Register(operation); service.BeginProposal("unknown");
            OperationSubmissionFixture.Submit(service, "unknown", Enumerable.Range(0, 3).Select(i => Request(service, "unknown", i, operation.Id)).ToArray());
            var proposal = service.FinishProposal("unknown", true)!;
            try { service.Approve(proposal); throw new Exception("Unexpected native error hidden"); } catch (IOException) { }
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Unknown && proposal.RegisteredCount == 1 && log.Count == 1, "Unknown results were replayed");
            Require((string?)OperationSubmissionFixture.Submit(service, "unknown",
                Enumerable.Range(0, 3).Select(i => Request(service, "unknown", i, operation.Id)).ToArray())["status"] == "unknown" && log.Count == 1,
                "Unknown batch was replayed or lost its outcome");
            Expect(() => OperationSubmissionFixture.Submit(service, "unknown", Request(service, "unknown", 1, operation.Id)), "request_id_conflict");
        });
        check("Staging validates aggregate stock across all segments before accepting any work", () =>
        {
            var unit = new Helicopter { Inventory = 2 }; var service = Buoys(unit); service.BeginProposal("inventory");
            Expect(() => OperationSubmissionFixture.Submit(service, "inventory", DropPlan(service, "inventory", 0, 0, 1),
                DropPlan(service, "inventory", 1, 2)), "no_available_sonobuoy");
            Require(service.Proposal("inventory")!.Orders.Count == 0 && unit.Queue.Count == 1,
                "A rejected batch retained executable work or reserved inventory before approval");
        });
        check("Each alternative carries independent per-unit append or replace plans, descriptions and route origins", () =>
        {
            var first = new Helicopter(); var second = new Helicopter { Id = 43, Name = "Hobby 2" };
            var service = new AdvisorOperations(() => true);
            service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { first, second }));
            service.BeginProposal("placement");
            JObject Order(Helicopter unit, string placement, params int[] drops)
            {
                var request = DropPlan(service, "placement", unit.Id, drops);
                request["arguments"]!["placement"] = placement;
                return new JObject { ["operation_id"] = SonobuoyDropExtension.OperationId,
                    ["target_ref"] = "unit:" + unit.Id, ["arguments"] = request["arguments"]!.DeepClone() };
            }
            JObject Plan(string id, params JObject[] orders) => SonobuoyFixture.Queued(new JObject { ["plan_id"] = id, ["title"] = id,
                ["summary"] = "Alternative", ["operations"] = new JArray(orders) });
            service.SubmitPlans("placement", new JObject { ["request_id"] = "placement",
                ["plans"] = new JArray(Plan("a", Order(first, "replace", 0, 1), Order(second, "append", 2, 3)),
                    Plan("b", Order(first, "append", 4, 5), Order(second, "replace", 6, 7))) });
            var proposal = service.FinishProposal("placement", true)!;
            foreach (var language in new[] { "en", "ja" })
            {
                var text = service.DescribeProposal(proposal, language, "a");
                Require(text.Contains("Hobby 1", StringComparison.Ordinal) && text.Contains("Hobby 2", StringComparison.Ordinal) &&
                    text.Contains(language == "en" ? "Existing reservations: replace" : "既存予約: 置換", StringComparison.Ordinal) &&
                    text.Contains(language == "en" ? "Existing reservations: append" : "既存予約: 追記", StringComparison.Ordinal),
                    "Approval hid a unit's reservation policy");
            }
            var lines = service.PreviewProposal(proposal, "en", "a").Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Line).ToArray();
            Require(lines.Select(l => l.Coordinates[0].Latitude).SequenceEqual(new[] { 26.2, 26.4 }) && first.ClearCalls == 0 && second.ClearCalls == 0,
                "Replacement preview used the old endpoint, append preview ignored it, or preview cleared reservations");
            var other = service.PreviewProposal(proposal, "en", "b").Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Line).ToArray();
            Require(other.Select(l => l.Coordinates[0].Latitude).SequenceEqual(new[] { 26.4, 26.2 }), "Alternative placement leaked into another plan");
            service.Approve(proposal, new[] { "a" }); service.Approve(proposal, new[] { "b" });
            Require(proposal.State == AdvisorProposalState.Registered && first.ClearCalls == 1 && first.Queue.Count == 2 &&
                second.ClearCalls == 0 && second.Queue[0] == "player waypoint" && second.Queue.Count == 3 &&
                first.Latitudes.SequenceEqual(new[] { 26.5, 26.501 }) && second.Latitudes.SequenceEqual(new[] { 26.502, 26.503 }) &&
                first.Depth == "alternating" && second.Depth == "alternating", "Selection cleared repeatedly, mixed unit policies or executed another alternative");
        });
        check("Replacement reclaims only cancelled reservations and never ammunition in active use", () =>
        {
            var unit = new Helicopter { Inventory = 6, Reserved = 5, Active = 1 }; var service = Buoys(unit);
            var candidate = service.Candidates(SonobuoyDropExtension.OperationId)["candidates"]![0]!;
            Require((bool?)candidate["available"] == true && (int?)candidate["ammunition"]![0]!["available_count"] == 0 &&
                (int?)candidate["ammunition"]![0]!["available_after_replacement_count"] == 5, "Replacement candidates lost reusable stock or reclaimed active work");
            JObject Request(string id, string placement, int count)
            { var request = DropPlan(service, id, 0, Enumerable.Range(0, count).ToArray()); request["arguments"]!["placement"] = placement; return request; }
            Expect(() => OperationSubmissionFixture.Adopt(service, Request("append-short", "append", 1)), "no_available_sonobuoy");
            Expect(() => OperationSubmissionFixture.Adopt(service, Request("replace-short", "replace", 6)), "no_available_sonobuoy");
            Require(unit.ClearCalls == 0, "A shortage erased a reservation");
            var result = OperationSubmissionFixture.Adopt(service, Request("replace-valid", "replace", 5));
            Require((string?)result["status"] == "registered" && unit.ClearCalls == 1 && unit.Queue.Count == 5 && unit.Active == 1,
                "Valid replacement did not reclaim the cancelled reservations exactly once");
        });
        check("Every point is validated before replacement and adoption rechecks inventory without erasing the old queue", () =>
        {
            var unit = new Helicopter(); var service = Buoys(unit);
            var invalid = DropPlan(service, "invalid", 0, 0, 1); invalid["arguments"]!["placement"] = "replace";
            invalid["arguments"]!["drops"]![1]!["latitude"] = 91;
            Expect(() => OperationSubmissionFixture.Adopt(service, invalid), "invalid_sonobuoy_arguments");
            Require(unit.ClearCalls == 0 && unit.Queue.Single() == "player waypoint", "Invalid input erased the queue");
            var valid = DropPlan(service, "stale", 0, 0, 1); valid["arguments"]!["placement"] = "replace";
            service.BeginProposal("stale"); OperationSubmissionFixture.Submit(service, "stale", valid); var proposal = service.FinishProposal("stale", true)!;
            unit.Inventory = 1; service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "no_available_sonobuoy" && unit.ClearCalls == 0 && unit.Queue.Single() == "player waypoint",
                "A stale accepted replacement removed existing work before revalidation");
        });
        check("A failure after clearing or appending stays unknown and cannot repeat or run the next unit", () =>
        {
            foreach (var placement in new[] { "append", "replace" })
            {
                var unit = new Helicopter { RejectAt = 1 }; var next = new Helicopter { Id = 43 };
                var service = new AdvisorOperations(() => true); service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit, next }));
                service.BeginProposal("failure"); var first = DropPlan(service, "failure", 0, 0, 1, 2); first["arguments"]!["placement"] = placement;
                var second = Request(service, "failure", 1); second["target_ref"] = "unit:43";
                OperationSubmissionFixture.Submit(service, "failure", first, second);
                var proposal = service.FinishProposal("failure", true)!;
                try { service.Approve(proposal); throw new Exception("Incomplete execution was reported as success"); }
                catch (InvalidOperationException error) when (error.InnerException is OperationException) { }
                service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Unknown && proposal.ErrorCode == "operation_outcome_unknown" &&
                    unit.Latitudes.Count == 1 && next.Latitudes.Count == 0 && unit.ClearCalls == (placement == "replace" ? 1 : 0),
                    "Partial unit work was reported as unchanged, replayed, or followed by another unit");
            }
        });
        check("The drop limit counts points across units and invalid nested policies never change existing reservations", () =>
        {
            var first = new Helicopter { Inventory = 40 }; var second = new Helicopter { Id = 43, Inventory = 40 };
            var service = new AdvisorOperations(() => true); service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { first, second }));
            service.BeginProposal("limits");
            var other = DropPlan(service, "limits", 1, Enumerable.Range(0, 11).ToArray()); other["target_ref"] = "unit:43";
            Expect(() => OperationSubmissionFixture.Submit(service, "limits",
                DropPlan(service, "limits", 0, Enumerable.Range(0, 30).ToArray()), other), "too_many_sonobuoy_drops");
            var malformed = DropPlan(service, "nested", 0, 0); malformed["arguments"]!["drops"]![0]!["placement"] = "replace";
            Expect(() => OperationSubmissionFixture.Adopt(service, malformed), "invalid_sonobuoy_arguments");
            Require(first.ClearCalls == 0 && second.ClearCalls == 0, "Validation changed a queue");
        });
    }
    private static AdvisorOperations Buoys(Helicopter unit)
    { var service = new AdvisorOperations(() => true); service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit })); return service; }
    private static JObject Request(AdvisorOperations service, string owner, int index, string operation = SonobuoyDropExtension.OperationId)
    {
        var args = new JObject { ["latitude"] = 26.5 + index * .001, ["longitude"] = 127.5, ["ammunition"] = "mod_buoy" };
        if (operation != SonobuoyDropExtension.OperationId) args["index"] = index;
        else args = SonobuoyFixture.Arguments("mod_buoy", 26.5 + index * .001);
        return new JObject { ["scope_id"] = service.ScopeId, ["request_id"] = owner + "." + index,
            ["operation"] = operation, ["target_ref"] = "unit:42", ["arguments"] = args };
    }
    private static JObject DropPlan(AdvisorOperations service, string owner, int id, params int[] indices)
    {
        var request = Request(service, owner, id);
        request["arguments"]!["drops"] = new JArray(indices.Select(i => new JObject
        { ["latitude"] = 26.5 + i * .001, ["longitude"] = 127.5, ["ammunition"] = "mod_buoy" }));
        return request;
    }
    private static void Expect(Action action, string code)
    {
        try { action(); throw new Exception("Expected rejection: " + code); }
        catch (AdvisorOperationException error) when (error.Code == code) { }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Helicopter : IBattleSonobuoyUnit
    {
        public int Id { get; set; } = 42;
        public string Name { get; set; } = "Hobby 1";
        public string? UnavailableReason => null;
        public string Depth { get; set; } = "alternating";
        public JObject Position => new();
        public ReservationRoute? ReservationRoute { get; set; } =
            new(new AdvisorMapCoordinate(26.2, 127.2), new AdvisorMapCoordinate(26.4, 127.4));
        internal int Inventory = 12, Reserved, Active, ClearCalls, RejectAt = -1;
        internal List<double> Latitudes = new();
        internal List<string> Depths = new();
        internal List<string> Queue = new() { "player waypoint" };
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("mod_buoy", "Custom buoy", Inventory, Math.Max(0, Inventory - Reserved - Active - Latitudes.Count), true, Math.Max(0, Inventory - Active)) };
        public void ClearReservations() { ClearCalls++; Queue.Clear(); Latitudes.Clear(); Depths.Clear(); Reserved = 0; }
        public void Append(string ammunition, double latitude, double longitude) { if (Latitudes.Count == RejectAt) throw new OperationException(409, "reservation_rejected"); Latitudes.Add(latitude); Depths.Add(Depth); Queue.Add("one buoy"); }
    }
    private sealed class Extension : IAdvisorOperation
    {
        public string Id { get; }
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Custom operation";
        public bool RequiresApproval { get; }
        private readonly List<string> log;
        internal int RejectAt = -1, ThrowAt = -1;
        internal Extension(string id, bool approval, List<string> log) { Id = id; RequiresApproval = approval; this.log = log; }
        public JObject Definition() => new() { ["id"] = Id };
        public JArray Candidates() => new();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Id + ":" + orders.Count;
        public JObject Execute(string target, JObject arguments)
        {
            var index = (int)arguments["index"]!;
            if (index == ThrowAt) throw new IOException("native fault");
            if (index == RejectAt) throw new AdvisorOperationException(409, "native_rejected");
            log.Add(Id + ":" + index); return new JObject { ["target_ref"] = target };
        }
    }
}
