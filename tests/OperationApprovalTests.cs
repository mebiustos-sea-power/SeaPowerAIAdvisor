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
        check("Twelve buoy requests remain one proposal until approval, append in order and cannot execute twice", () =>
        {
            var unit = new Helicopter(); var service = Buoys(unit); service.BeginProposal("turn");
            var requests = Enumerable.Range(0, 12).Select(i => Request(service, "turn", i)).ToArray();
            foreach (var request in requests)
                Require((string?)service.Propose("turn", request)["status"] == "proposed", "Valid buoy proposal rejected");
            Require(unit.Latitudes.Count == 0 && unit.Queue.Count == 1, "Proposal changed the game before approval");
            var proposal = service.FinishProposal("turn", true)!;
            Require(proposal.State == AdvisorProposalState.Pending && proposal.Orders.Count == 12, "Requests were not held together");
            foreach (var language in new[] { "en", "ja" })
            {
                var description = service.DescribeProposal(proposal, language);
                Require(description.Contains("Hobby 1", StringComparison.Ordinal) && description.Contains("12", StringComparison.Ordinal) &&
                    description.Contains("Custom buoy", StringComparison.Ordinal) && description.Contains("26.51100", StringComparison.Ordinal),
                    "The command summary lost the target, total, ammunition or last destination");
            }
            // Detached payloads cannot change the eventual approved work.
            requests[0]["arguments"]!["latitude"] = -80;
            proposal.Orders[0].Arguments["latitude"] = 80;
            unit.Depth = "below_layer";
            var preview = service.PreviewProposal(proposal, "en");
            var points = preview.Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Point).ToArray();
            Require(points.Length == 12 && points.All(point => point.Label.StartsWith("Hobby 1 · ", StringComparison.Ordinal)),
                "The sonobuoy preview lost accepted drops or their target labels");
            var route = preview.Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line);
            Require(route.Coordinates.Count == 13 && route.Coordinates[0].Latitude == 26.4 && route.Coordinates[0].Longitude == 127.4 &&
                route.Coordinates.Skip(1).Select(p => (p.Latitude, p.Longitude)).SequenceEqual(points.Select(p => (p.Coordinates[0].Latitude, p.Coordinates[0].Longitude))),
                "The preview omitted the queued endpoint or changed the proposed drop order");
            service.Approve(proposal); service.Approve(proposal);
            Require(points.Select(point => point.Coordinates[0].Latitude).SequenceEqual(unit.Latitudes) &&
                points.All(point => point.Coordinates[0].Longitude == 127.5),
                "The approval preview differed from the registered coordinates or used caller-mutated arguments");
            Require(proposal.State == AdvisorProposalState.Registered && proposal.RegisteredCount == 12 && unit.Queue.Count == 13 &&
                unit.Queue[0] == "player waypoint" && unit.Latitudes.SequenceEqual(Enumerable.Range(0, 12).Select(i => 26.5 + i * .001)) &&
                unit.Depths.All(depth => depth == "below_layer"), "Approval lost order, repeated a task, cleared the player route or overwrote depth");
            Require((string?)service.Propose("turn", Request(service, "turn", 0))["status"] == "registered" && unit.Latitudes.Count == 12,
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
            var values = new[] { Request(service, "routes", 2), Request(service, "routes", 9), Request(service, "routes", 0), Request(service, "routes", 7) };
            values[1]["target_ref"] = "unit:43"; values[3]["target_ref"] = "unit:43";
            foreach (var request in values) service.Propose("routes", request);
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
            var removed = service.PreviewProposal(proposal, "en");
            Require(removed.Drawings.Where(d => d.Kind == AdvisorMapDrawingKind.Point).Count() == 4 &&
                removed.Drawings.Last(d => d.Kind == AdvisorMapDrawingKind.Line).Coordinates.Count == 2,
                "A vanished unit erased retained destinations or invented a route origin");
        });
        check("Sonobuoy previews respect the movement-plan disclosure setting without disabling the proposed drops", () =>
        {
            var unit = new Helicopter();
            var information = AdvisorInformation.Default.With(InformationKind.BattleMovementPlans, false);
            var service = new AdvisorOperations(() => true);
            service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit }, () => information));
            service.BeginProposal("hidden-route");
            service.Propose("hidden-route", Request(service, "hidden-route", 0));
            service.Propose("hidden-route", Request(service, "hidden-route", 1));
            var proposal = service.FinishProposal("hidden-route", true)!;
            var line = service.PreviewProposal(proposal, "en").Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line);
            Require(line.Coordinates.Select(p => p.Latitude).SequenceEqual(new[] { 26.5, 26.501 }) && unit.Queue.Count == 1,
                "An excluded reservation endpoint was disclosed or previewing changed the queue");
            information = AdvisorInformation.Default;
            Require(service.PreviewProposal(proposal, "en").Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line).Coordinates[0].Latitude == 26.4,
                "Restoring movement disclosure kept using an old permission or route");
            service.Approve(proposal);
            Require(proposal.RegisteredCount == 2 && unit.Queue.Count == 3, "Movement disclosure changed reservation acceptance");
            service.Retire();
        });
        check("One drop still draws an arrival leg, while an unavailable start never becomes a zero coordinate", () =>
        {
            var unit = new Helicopter { ReservationRoute = new ReservationRoute(new AdvisorMapCoordinate(27, 128), null) };
            unit.Queue.Clear();
            var service = Buoys(unit); service.BeginProposal("one"); service.Propose("one", Request(service, "one", 0));
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
            for (var i = 0; i < 12; i++) service.Propose(turn.OperationId, Request(service, turn.OperationId, i));
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
                service.BeginProposal("earlier"); service.Propose("earlier", Request(service, "earlier", 0, operation.Id));
                var previous = service.FinishProposal("earlier", true)!;
                if (approval) service.Approve(previous);
                var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("New plan"));
                var run = new AdvisorLocalRun(); run.Attach(turn); service.BeginProposal(turn.OperationId);
                service.Propose(turn.OperationId, Request(service, turn.OperationId, 1, operation.Id));
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
            service.Propose("mixed", Request(service, "mixed", 0, first.Id));
            service.Propose("mixed", Request(service, "mixed", 1, second.Id));
            service.Propose("mixed", Request(service, "mixed", 2, first.Id));
            var proposal = service.FinishProposal("mixed", true)!;
            Require(log.Count == 0 && proposal.State == AdvisorProposalState.Pending, "An unchecked operation ran ahead of the required approval");
            var summary = service.DescribeProposal(proposal, "en");
            Require(summary.Contains("custom.first:2", StringComparison.Ordinal) && summary.Contains("custom.second:1", StringComparison.Ordinal),
                "The common layer assumed a unit type or constructed extension-specific text");
            service.Approve(proposal);
            Require(log.SequenceEqual(new[] { "custom.first:0", "custom.second:1", "custom.first:2" }), "Grouping summaries reordered execution");
            service.BeginProposal("automatic"); service.Propose("automatic", Request(service, "automatic", 3, first.Id));
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
                for (var i = 0; i < 3; i++) service.Propose("check", Request(service, "check", i));
                var proposal = service.FinishProposal("check", true)!;
                if (change == "inventory") unit.Inventory = 2;
                if (change == "target") units.Clear();
                if (change == "permission") enabled = false;
                if (change == "information") information = information.With(InformationKind.BattleWeapons, false);
                service.Approve(proposal);
                Require(proposal.State == AdvisorProposalState.Rejected && proposal.ErrorCode != null && unit.Queue.Count == 1 && unit.Latitudes.Count == 0,
                    "Stale " + change + " was applied or preflight partially registered the batch");
            }
        });
        check("Consultation failure, history removal and battle retirement invalidate proposals and late calls", () =>
        {
            foreach (var cause in new[] { "failure", "history", "battle" })
            {
                var unit = new Helicopter(); var service = Buoys(unit); service.BeginProposal("old");
                service.Propose("old", Request(service, "old", 0)); var proposal = service.Proposal("old")!;
                if (cause == "failure") service.FinishProposal("old", false);
                else
                {
                    service.FinishProposal("old", true);
                    if (cause == "history") service.DiscardProposalsExcept(Array.Empty<string>()); else service.Retire();
                }
                Require(proposal.State == AdvisorProposalState.Cancelled && unit.Latitudes.Count == 0, "Invalidated proposal changed the game");
                try { service.Approve(proposal); } catch (AdvisorOperationException) { }
                try { service.Propose("old", Request(service, "old", 1)); throw new Exception("Late proposal was accepted"); }
                catch (AdvisorOperationException) { }
                Require(unit.Queue.Count == 1, "A late worker or stale button revived discarded work");
            }
        });
        check("A proposal with known registration failure stops at the first failure without replaying completed orders", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(() => true);
            var operation = new Extension("custom.failure", true, log) { RejectAt = 1 }; service.Register(operation); service.BeginProposal("partial");
            for (var i = 0; i < 3; i++) service.Propose("partial", Request(service, "partial", i, operation.Id));
            var proposal = service.FinishProposal("partial", true)!; service.Approve(proposal); service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Partial && proposal.RegisteredCount == 1 && log.Count == 1 && proposal.ErrorCode == "native_rejected",
                "A failed batch claimed full success, continued with dependent work or repeated earlier work");
            var prior = service.PriorOrders("partial");
            Require((string?)prior[0]?["response"]?["status"] == "registered" && (string?)prior[1]?["response"]?["status"] == "rejected" &&
                (string?)prior[2]?["response"]?["status"] == "rejected", "Partial results did not explain which orders ran");
        });
        check("Unknown native side effects remain visible and cannot be approved or submitted again", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(() => true);
            var operation = new Extension("custom.unknown", true, log) { ThrowAt = 1 }; service.Register(operation); service.BeginProposal("unknown");
            for (var i = 0; i < 3; i++) service.Propose("unknown", Request(service, "unknown", i, operation.Id));
            var proposal = service.FinishProposal("unknown", true)!;
            try { service.Approve(proposal); throw new Exception("Unexpected native error hidden"); } catch (IOException) { }
            service.Approve(proposal);
            Require(proposal.State == AdvisorProposalState.Unknown && proposal.RegisteredCount == 1 && log.Count == 1, "Unknown results were replayed");
            try { service.Propose("unknown", Request(service, "unknown", 1, operation.Id)); throw new Exception("Unknown request replayed"); }
            catch (AdvisorOperationException error) when (error.Code == "operation_outcome_unknown") { }
        });
        check("Staging never accepts ammunition already promised in the same proposal", () =>
        {
            var unit = new Helicopter { Inventory = 2 }; var service = Buoys(unit); service.BeginProposal("inventory");
            service.Propose("inventory", Request(service, "inventory", 0)); service.Propose("inventory", Request(service, "inventory", 1));
            var rejected = service.Propose("inventory", Request(service, "inventory", 2));
            Require((string?)rejected["code"] == "no_available_sonobuoy" && service.Proposal("inventory")!.Orders.Count == 2 && unit.Queue.Count == 1,
                "Staged requests oversubscribed inventory or reserved it in the game before approval");
        });
    }
    private static AdvisorOperations Buoys(Helicopter unit)
    { var service = new AdvisorOperations(() => true); service.Register(SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit })); return service; }
    private static JObject Request(AdvisorOperations service, string owner, int index, string operation = SonobuoyDropExtension.OperationId)
    {
        var args = new JObject { ["latitude"] = 26.5 + index * .001, ["longitude"] = 127.5, ["ammunition"] = "mod_buoy" };
        if (operation != SonobuoyDropExtension.OperationId) args["index"] = index;
        return new JObject { ["scope_id"] = service.ScopeId, ["request_id"] = owner + "." + index,
            ["operation"] = operation, ["target_ref"] = "unit:42", ["arguments"] = args };
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
        internal int Inventory = 12;
        internal List<double> Latitudes = new();
        internal List<string> Depths = new();
        internal List<string> Queue = new() { "player waypoint" };
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("mod_buoy", "Custom buoy", Inventory, Inventory - Latitudes.Count, true) };
        public void Append(string ammunition, double latitude, double longitude) { Latitudes.Add(latitude); Depths.Add(Depth); Queue.Add("one buoy"); }
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
