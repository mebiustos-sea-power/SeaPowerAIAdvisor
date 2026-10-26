using IAdvisorOperationPreview = SeaPowerAIAdvisor.IAdvisorOperationPreview;
using SeaPowerAIAdvisor.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class OperationPlanTests
{
    internal static void Run(Action<string, Action> check)
    {
        RunRevisions(check);
        check("A complete submission preserves mixed operation and target order and executes only the selected alternative", () =>
        {
            var log = new List<string>(); var service = new AdvisorOperations(_ => true);
            var a = new Command("custom.move", log); var b = new Command("custom.observe", log);
            service.Register(a); service.Register(b); service.BeginProposal("mixed");
            var request = Batch(Plan("a", Order(a, "one", 1), Order(b, "two", 2), Order(a, "two", 3)), Plan("b", Order(b, "one", 4)));
            var original = request.ToString(); var result = service.SubmitPlans("mixed", request);
            request["plans"]![0]!["title"] = "changed";
            Require((string?)result["status"] == "proposed" && log.Count == 0, "Submission executed or lost its receipt");
            var proposal = service.FinishProposal("mixed", true)!;
            Require(proposal.Plans[0].Title == "Plan a" && proposal.Plans[0].Orders.Count == 3, "Caller mutation changed a retained plan");
            Require(service.DescribeProposal(proposal, "en", "a").Contains("custom.move:2", StringComparison.Ordinal), "Command descriptions did not receive the inspected plan");
            service.Approve(proposal, new[] { "a" }); service.Approve(proposal, new[] { "b" });
            Require(log.SequenceEqual(new[] { "custom.move:one:1", "custom.observe:two:2", "custom.move:two:3" }) && proposal.RegisteredCount == 3,
                "Selection mixed alternatives, reordered operations or registered twice");
            Require((bool?)service.SubmitPlans("mixed", JObject.Parse(original))["registered"] == true && log.Count == 3, "A replay repeated native operations");
            var conflict = JObject.Parse(original); conflict["plans"]![0]!["summary"] = "different";
            Expect("request_id_conflict", () => service.SubmitPlans("mixed", conflict));
        });
        check("Individual command switches filter discovery and reject disabled members before submitting or adopting a mixed plan", () =>
        {
            var disabled = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal); var log = new List<string>();
            var a = new Command("custom.move", log); var b = new Command("custom.observe", log);
            var service = new AdvisorOperations(id => !disabled.Contains(id)); service.Register(a); service.Register(b);
            service.BeginProposal("later"); service.SubmitPlans("later", Batch(Plan("a", Order(a, "one", 1), Order(b, "two", 2))));
            var proposal = service.FinishProposal("later", true)!;
            disabled.Add(b.Id);
            Require(service.Enabled && service.Inspect("en").Count == 2 &&
                service.Catalog()["operations"]!.Count() == 1 && (string?)service.Catalog()["operations"]![0]!["id"] == a.Id &&
                (int?)service.Discover("", 0, 20)["total_count"] == 1, "A disabled command disappeared from settings or remained advertised to AI");
            Expect("operation_disabled", () => service.Describe(b.Id)); Expect("operation_disabled", () => service.Candidates(b.Id));
            service.BeginProposal("rejected");
            Expect("operation_disabled", () => service.SubmitPlans("rejected", Batch(Plan("a", Order(a, "one", 3), Order(b, "two", 4)))));
            Require(service.Proposal("rejected")!.Orders.Count == 0 && log.Count == 0, "A rejected mixed submission retained a partial plan or wrote orders");
            service.Approve(proposal, new[] { "a" });
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "operation_disabled" && log.Count == 0,
                "Disabling one command allowed a previously submitted mixed plan to execute partially");
            disabled.Remove(b.Id); service.Approve(proposal, new[] { "a" });
            Require(log.SequenceEqual(new[] { "custom.move:one:1", "custom.observe:two:2" }), "Re-enabling failed or reordered the retained plan");
            disabled.Add(a.Id); disabled.Add(b.Id);
            using var gateway = new AdvisorOperationGateway(service);
            Require(!service.Enabled && gateway.CreateSession("none", AdvisorPrompts.Current) == null && service.Catalog()["operations"]!.Count() == 0,
                "Disabling every command still supplied an operation service");
            var c = new Command("custom.new", log); service.Register(c);
            Require(service.Enabled && service.Catalog()["operations"]!.Count() == 1 &&
                (string?)service.Catalog()["operations"]![0]!["id"] == c.Id, "A newly registered command did not default to enabled");
        });
        check("Each alternative is validated independently, while multi-selection checks the combined demand before any side effect", () =>
        {
            var log = new List<string>(); var command = new Command("custom.inventory", log) { Available = 2 };
            var service = Service(command, "stock");
            service.SubmitPlans("stock", Batch(Plan("a", Order(command, "one", 1), Order(command, "one", 2)),
                Plan("b", Order(command, "one", 3), Order(command, "one", 4))));
            var proposal = service.FinishProposal("stock", true)!;
            service.Approve(proposal, new[] { "a", "b" });
            Require(proposal.State == AdvisorProposalState.Pending && proposal.ErrorCode == "insufficient_stock" && log.Count == 0,
                "A combined shortage registered a partial plan or discarded valid alternatives");
            service.Approve(proposal, new[] { "b" });
            Require(proposal.State == AdvisorProposalState.Registered && proposal.ErrorCode == null && log.SequenceEqual(new[] { "custom.inventory:one:3", "custom.inventory:one:4" }),
                "Changing selection failed to use retained arguments");
        });
        check("Multiple selections use submission order and single-selection rejects empty, repeated, unknown or multiple IDs", () =>
        {
            foreach (var selection in new[] { AdvisorPlanSelection.Single, AdvisorPlanSelection.Multiple })
            {
                var log = new List<string>(); var command = new Command("custom.choice", log, selection: selection);
                var service = Service(command, "choice");
                service.SubmitPlans("choice", Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "two", 2))));
                var proposal = service.FinishProposal("choice", true)!;
                foreach (var ids in new[] { Array.Empty<string>(), new[] { "missing" }, new[] { "a", "a" } })
                    Expect("invalid_plan_selection", () => service.Approve(proposal, ids));
                if (selection == AdvisorPlanSelection.Single)
                {
                    Expect("invalid_plan_selection", () => service.Approve(proposal));
                    Expect("invalid_plan_selection", () => service.Approve(proposal, new[] { "a", "b" }));
                    service.Approve(proposal, new[] { "b" });
                    Require(log.Single() == "custom.choice:two:2", "A single choice registered another plan");
                }
                else
                {
                    service.Approve(proposal, new[] { "b", "a" });
                    Require(log.SequenceEqual(new[] { "custom.choice:one:1", "custom.choice:two:2" }), "Click order reordered plans");
                }
            }
        });
        check("Malformed or invalid plan sets are rejected atomically and can be corrected without retaining partial work", () =>
        {
            var command = new Command("custom.atomic", new()); var service = Service(command, "atomic");
            var valid = Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "two", 2)));
            var duplicate = (JObject)valid.DeepClone(); duplicate["plans"]![1]!["plan_id"] = "a";
            Expect("duplicate_plan_id", () => service.SubmitPlans("atomic", duplicate));
            var bad = (JObject)valid.DeepClone(); bad["plans"]![1]!["operations"]![0]!["arguments"]!["index"] = -1;
            Expect("invalid_command_argument", () => service.SubmitPlans("atomic", bad));
            var extra = (JObject)valid.DeepClone(); extra["plans"]![0]!["operations"]![0]!["request_id"] = "unexpected";
            Expect("invalid_plan_submission", () => service.SubmitPlans("atomic", extra));
            var tooMany = (JObject)valid.DeepClone(); tooMany["plans"]![0]!["operations"] = new JArray(Enumerable.Range(0, 41).Select(i => Order(command, "one", i)));
            Expect("invalid_plan_submission", () => service.SubmitPlans("atomic", tooMany));
            Require(service.Proposal("atomic")!.Orders.Count == 0 && service.PriorOrders("atomic").Count == 0, "Rejected alternatives leaked receipts or work");
            service.SubmitPlans("atomic", valid);
            Require(service.Proposal("atomic")!.Plans.Count == 2, "A corrected submission could not be accepted");
        });
        check("Operation plan-count policies intersect over the full plan set, with single selection taking precedence", () =>
        {
            var log = new List<string>(); var a = new Command("custom.range", log, 1, 3); var b = new Command("custom.exact", log, 2, 2, AdvisorPlanSelection.Single);
            var service = new AdvisorOperations(_ => true); service.Register(a); service.Register(b); service.BeginProposal("policy");
            Expect("plan_count_out_of_range", () => service.SubmitPlans("policy", Batch(Plan("a", Order(a, "one", 1), Order(b, "two", 2)))));
            service.SubmitPlans("policy", Batch(Plan("a", Order(a, "one", 1), Order(b, "two", 2)), Plan("b", Order(a, "one", 3))));
            Require(service.Proposal("policy")!.Selection == AdvisorPlanSelection.Single &&
                (int?)service.Describe(b.Id)["operation"]?["plan_policy"]?["minimum"] == 2, "Published command policy was not applied to the set");
            var strict = new Command("custom.three", log, 3, 3); service.Register(strict); service.BeginProposal("incompatible");
            Expect("incompatible_plan_policies", () => service.SubmitPlans("incompatible", Batch(Plan("a", Order(b, "one", 1), Order(strict, "two", 2)))));
            var flexible = new Command("custom.flexible", log, 1, 2, AdvisorPlanSelection.Single, target: 2); service.Register(flexible);
            Require((int?)service.Describe(a.Id)["operation"]?["plan_policy"]?["target"] == 3 &&
                (int?)service.Describe(flexible.Id)["operation"]?["plan_policy"]?["target"] == 2,
                "Different generation targets were lost from the published contracts");
            foreach (var count in new[] { 1, 2 })
            {
                var owner = "flexible-" + count; service.BeginProposal(owner);
                JObject Alternative(string id) => Plan(id, Order(a, "one", 1), Order(flexible, "two", 2));
                Expect("plan_count_out_of_range", () => service.SubmitPlans(owner, Batch(Alternative("a"), Alternative("b"), Alternative("c"))));
                var invalid = Batch(Alternative("a")); invalid["plans"]![0]!["operations"]![1]!["arguments"]!["index"] = -1;
                Expect("invalid_command_argument", () => service.SubmitPlans(owner, invalid));
                Require(service.PriorOrders(owner).Count == 0 && service.Proposal(owner)!.Orders.Count == 0 && log.Count == 0,
                    "An invalid single plan or excessive plan set retained partial work");
                var request = Batch(Enumerable.Range(0, count).Select(i => Alternative("plan" + i)).ToArray());
                service.SubmitPlans(owner, request);
                var proposal = service.FinishProposal(owner, true)!;
                Require(proposal.State == AdvisorProposalState.Pending && proposal.Plans.Count == count &&
                    proposal.Selection == AdvisorPlanSelection.Single && log.Count == 0,
                    "Compatible accepted ranges were narrowed by generation targets or bypassed approval");
            }
        });
        check("Invalid generation targets fail explicitly instead of silently changing command policies", () =>
        {
            foreach (var target in new[] { 0, 3 })
            {
                try { _ = new AdvisorOperationPlanPolicy(target, 1, 2, AdvisorPlanSelection.Single); }
                catch (ArgumentOutOfRangeException error) when (error.ParamName == "target") { continue; }
                throw new InvalidOperationException("An out-of-range generation target was accepted");
            }
        });
        check("Alternative plans require user selection even when their individual commands allow automatic execution", () =>
        {
            var log = new List<string>(); var command = new Command("custom.auto", log) { Approval = false };
            var service = Service(command, "auto");
            service.SubmitPlans("auto", Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "one", 2))));
            var proposal = service.FinishProposal("auto", true)!;
            Require(proposal.State == AdvisorProposalState.Pending && log.Count == 0, "Alternatives executed without choosing");
            service.Approve(proposal, new[] { "a" }); Require(log.Count == 1, "Approved automatic command did not execute");
        });
        check("A failed consultation, removed history or retired battle invalidates every alternative without reviving it on replay", () =>
        {
            foreach (var cause in new[] { "incomplete", "history", "battle" })
            {
                var log = new List<string>(); var command = new Command("custom.lifetime", log); var service = Service(command, "life");
                var request = Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "two", 2)));
                service.SubmitPlans("life", request);
                var proposal = service.FinishProposal("life", cause != "incomplete")!;
                if (cause == "history") service.DiscardProposalsExcept(Array.Empty<string>());
                if (cause == "battle") service.Retire();
                try { service.Approve(proposal, new[] { "a" }); } catch (AdvisorOperationException) { }
                Require(proposal.State == AdvisorProposalState.Cancelled && log.Count == 0, "An invalidated alternative executed");
                if (cause != "battle")
                    Require((string?)service.SubmitPlans("life", request)["status"] == "cancelled" && log.Count == 0, "Replay revived a cancelled set");
            }
        });
        check("Selected-plan native failures stop subsequent operations and never execute an unselected alternative", () =>
        {
            foreach (var unknown in new[] { false, true })
            {
                var log = new List<string>(); var command = new Command("custom.failure", log) { FailAt = 2, Unknown = unknown };
                var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Create alternatives"));
                var service = Service(command, turn.OperationId);
                service.SubmitPlans(turn.OperationId, Batch(Plan("a", Order(command, "one", 1), Order(command, "one", 2), Order(command, "one", 3)),
                    Plan("b", Order(command, "two", 4))));
                turn.Finish(new AdviceResult("Original answer", 1, 0, null), service);
                var proposal = turn.Proposal!;
                try { service.Approve(proposal, new[] { "a" }); } catch (IOException) when (unknown) { }
                service.Approve(proposal, new[] { "a" }); service.Approve(proposal, new[] { "b" });
                Require(proposal.State == (unknown ? AdvisorProposalState.Unknown : AdvisorProposalState.Partial) && proposal.RegisteredCount == 1 && log.Single() == "custom.failure:one:1",
                    "Partial or unknown execution continued or replayed work");
                foreach (var language in new[] { "en", "ja" })
                {
                    var text = AdvisorText.Load(language);
                    var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                    var button = rendered.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ReviewProposal0");
                    Require((string?)button.Attribute("Content") == text["operation.accept"] && (string?)button.Attribute("IsEnabled") == "false" &&
                        rendered.Descendants().Any(e => (string?)e.Attribute("Text") == text.Format("operation.failure", proposal.ErrorCode!, 1, 3)) &&
                        button.Parent!.Elements().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string?)e.Attribute("Text"))
                            .SequenceEqual(new[] { text.Format("operation.review_plan", 1, proposal.Plans[0].Title) }) &&
                        turn.Exchange!.Answer == "Original answer",
                        "A failed adoption offered another execution or hid its error and completed count behind the unified caption");
                }
            }
        });
        check("Preview switching shows retained coordinates for only the inspected plan and releases only its own map drawings", () =>
        {
            var command = new Command("custom.map", new()); var service = Service(command, "map");
            service.SubmitPlans("map", Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "two", 2))));
            var proposal = service.FinishProposal("map", true)!; var map = new Map();
            using var other = map.Draw(Array.Empty<AdvisorMapDrawing>()); using var preview = new AdvisorOperationPreview(map);
            preview.Show(service, proposal, "en", "a"); preview.Show(service, proposal, "en", "b");
            Require(map.Active == 2 && map.Latitudes.SequenceEqual(new[] { 1d, 2d }) && service.PreviewProposal(proposal, "en", "b").Drawings.Count == 1,
                "Switching mixed alternatives or leaked a drawing lease");
            preview.Dispose(); Require(map.Active == 1 && proposal.State == AdvisorProposalState.Pending, "Closing preview cancelled work or cleared unrelated map content");
        });
        check("Single-selection navigation wraps both ends and keeps the displayed map and approved operations aligned", () =>
        {
            var log = new List<string>(); var command = new Command("custom.browse", log, selection: AdvisorPlanSelection.Single);
            var service = Service(command, "browse");
            service.SubmitPlans("browse", Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "two", 2)),
                Plan("c", Order(command, "three", 3))));
            var proposal = service.FinishProposal("browse", true)!; var state = new AdvisorOperationReviewState(proposal);
            var map = new Map(); using var preview = new AdvisorOperationPreview(map);
            void Inspect() => preview.Show(service, proposal, "en", state.Current.Id);
            Require(state.CanNavigate, "Multiple alternatives did not enable navigation");
            Inspect(); state.Move(-1); Inspect(); state.Move(1); Inspect(); state.Move(1); Inspect();
            state.Move(1); Inspect(); state.Move(1); Inspect(); state.Move(-1); Inspect();
            Require(map.Active == 1 && map.Latitudes.SequenceEqual(new[] { 1d, 3d, 1d, 2d, 3d, 1d, 3d }) &&
                state.Current.Id == "c" && state.SelectedPlanIds.SequenceEqual(new[] { "c" }) && log.Count == 0,
                "Browsing changed the game, mixed previews or kept an invisible execution target");
            Require(service.DescribeProposal(proposal, "en", state.Current.Id) == "custom.browse:1", "The inspected description used another plan");
            service.Approve(proposal, state.SelectedPlanIds); preview.Tick(service);
            Require(log.Single() == "custom.browse:three:3" && map.Active == 0, "Approval executed a different plan or left a stale preview");
            var reopened = new AdvisorOperationReviewState(proposal);
            Require(reopened.Current.Id == "c" && reopened.SelectedPlanIds.SequenceEqual(new[] { "c" }), "Reopening showed an unexecuted alternative");
            var one = Service(command, "one"); one.SubmitPlans("one", Batch(Plan("a", Order(command, "one", 1))));
            var single = new AdvisorOperationReviewState(one.FinishProposal("one", true)!);
            Require(!single.CanNavigate && !single.Move(-1) && !single.Move(1) && single.Current.Id == "a" && log.Count == 1,
                "A single alternative offered navigation or browsing registered game orders");
        });
        check("Multi-plan browsing preserves explicit checks and a failed preflight can be reviewed without selecting an unseen plan", () =>
        {
            var log = new List<string>(); var command = new Command("custom.checks", log) { Available = 1 };
            var service = Service(command, "checks");
            service.SubmitPlans("checks", Batch(Plan("a", Order(command, "one", 1)), Plan("b", Order(command, "one", 2))));
            var proposal = service.FinishProposal("checks", true)!; var state = new AdvisorOperationReviewState(proposal, "b");
            Require(!state.IncludesCurrent && state.SelectedPlanIds.SequenceEqual(new[] { "a" }), "Browsing auto-selected a second plan");
            state.IncludeCurrent(true); state.Move(1); state.Move(1);
            Require(state.IncludesCurrent && state.SelectedPlanIds.SequenceEqual(new[] { "a", "b" }), "Switching lost a checkbox selection");
            service.Approve(proposal, state.SelectedPlanIds);
            Require(proposal.State == AdvisorProposalState.Pending && log.Count == 0, "A combined shortage registered work");
            var reopened = new AdvisorOperationReviewState(proposal);
            Require(reopened.SelectedPlanIds.SequenceEqual(new[] { "a", "b" }), "Failed preflight choices were not retained");
            reopened.IncludeCurrent(false); reopened.Move(1); reopened.IncludeCurrent(false);
            Require(reopened.SelectedPlanIds.Count == 0, "Unchecking all plans left an invisible execution target");
            reopened.IncludeCurrent(true); service.Approve(proposal, reopened.SelectedPlanIds);
            Require(log.Single() == "custom.checks:one:2", "Only the explicitly checked plan should register");
        });
        check("Approval targets follow execution selection, deduplicate identities and distinguish units sharing a display name", () =>
        {
            var log = new List<string>(); var command = new Command("custom.targets", log);
            var service = Service(command, "targets");
            service.SubmitPlans("targets", Batch(
                Plan("a", Order(command, "unit:1029", 1), Order(command, "unit:1029", 2)),
                Plan("b", Order(command, "unit:1030", 3), Order(command, "unit:1029", 4)),
                Plan("c", Order(command, "unit:999", 5), Order(command, "zone:alpha", 6))));
            var proposal = service.FinishProposal("targets", true)!;
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var lookups = new List<int>();
                string? Name(int id) { lookups.Add(id); return id is 1029 or 1030 ? "Hobby 1" : null; }
                string[] Targets(AdvisorOperationReviewState state)
                {
                    var view = AdvisorOperationTargets.Render(state.SelectedPlans, text, Name);
                    Require(!view.Descendants().Any(e => e.Name.LocalName == "Border"), "A nonreservation command displayed a queue-policy badge");
                    return view.Descendants().Where(e => e.Name.LocalName == "WrapPanel")
                        .Select(row => (string)row.Elements().First().Attribute("Text")!).ToArray();
                }
                var state = new AdvisorOperationReviewState(proposal, "b");
                Require(Targets(state).SequenceEqual(new[] { "Hobby 1 (1029)" }) && lookups.SequenceEqual(new[] { 1029 }),
                    "Browsing an unchecked plan changed targets or repeated an identity lookup");
                state.IncludeCurrent(true); state.Move(1);
                Require(Targets(state).SequenceEqual(new[] { "Hobby 1 (1029)", "Hobby 1 (1030)" }),
                    "Same-name units merged, repeated reservations duplicated a target or targets followed browsing instead of execution order");
                state.IncludeCurrent(true);
                Require(Targets(state).SequenceEqual(new[] { "Hobby 1 (1029)", "Hobby 1 (1030)", "unit:999", "zone:alpha" }),
                    "An unavailable name or non-unit reference was dropped or invented");
                state.IncludeCurrent(false); state.Move(1); state.IncludeCurrent(false); state.Move(1); state.IncludeCurrent(false);
                var empty = AdvisorOperationTargets.Render(state.SelectedPlans, text, Name);
                Require(state.SelectedPlanIds.Count == 0 && Targets(state).Length == 0 &&
                    empty.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string)e.Attribute("Text")!)
                        .SequenceEqual(new[] { text["operation.targets"], text["operation.no_targets"] }) && log.Count == 0,
                    "Empty selection retained targets or review formatting executed game orders");
            }
            var singleCommand = new Command("custom.single_targets", log, selection: AdvisorPlanSelection.Single);
            var single = Service(singleCommand, "single");
            single.SubmitPlans("single", Batch(Plan("a", Order(singleCommand, "unit:1029", 1)), Plan("b", Order(singleCommand, "unit:1030", 2))));
            var singleProposal = single.FinishProposal("single", true)!;
            var selection = new AdvisorOperationReviewState(singleProposal, "b"); var en = AdvisorText.Load("en");
            string[] SingleTargets() => AdvisorOperationTargets.Render(selection.SelectedPlans, en, _ => "Hobby 1")
                .Descendants().Where(e => e.Name.LocalName == "WrapPanel").Select(row => (string)row.Elements().First().Attribute("Text")!).ToArray();
            Require(SingleTargets().SequenceEqual(new[] { "Hobby 1 (1030)" }), "Direct selection displayed another unit");
            selection.Move(1);
            Require(SingleTargets().SequenceEqual(new[] { "Hobby 1 (1029)" }), "Switching a single-choice plan left old targets");
            single.Approve(singleProposal, selection.SelectedPlanIds);
            Require(log.SequenceEqual(new[] { "custom.single_targets:unit:1029:1" }), "Displayed targets and approved targets diverged");
        });
        check("Each pending alternative has its own safe history button that opens and executes the corresponding plan", () =>
        {
            var log = new List<string>(); var command = new Command("custom.history", log, selection: AdvisorPlanSelection.Single);
            var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Create alternatives"));
            var service = Service(command, turn.OperationId);
            var first = Plan("a", Order(command, "one", 1)); first["title"] = "前方 <警戒> & 支援";
            var last = Plan("c", Order(command, "three", 3)); last["title"] = new string('あ', 100);
            service.SubmitPlans(turn.OperationId, Batch(first, Plan("b", Order(command, "two", 2)), last));
            turn.Finish(new AdviceResult("Original answer", 1, 0, null), service);
            var proposal = turn.Proposal!;
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var actions = AdvisorTranscript.ReviewActions(proposal, 0, text).ToArray();
                var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var buttons = rendered.Descendants().Where(e => ((string?)e.Attribute(x + "Name"))?.StartsWith("ReviewProposal", StringComparison.Ordinal) == true).ToArray();
                Require(buttons.Length == 3 && actions.Select(a => a.PlanId).SequenceEqual(new[] { "a", "b", "c" }) &&
                    actions.Select(a => a.Name).Distinct().Count() == 3, "History collapsed alternatives or lost their retained identities");
                for (var i = 0; i < actions.Length; i++)
                {
                    var button = buttons.Single(e => (string?)e.Attribute(x + "Name") == actions[i].Name);
                    Require((string?)button.Attribute("IsEnabled") == "true" &&
                        button.Descendants().Any(e => (string?)e.Attribute("Text") == actions[i].Caption) &&
                        actions[i].Caption.Contains(proposal.Plans[i].Title, StringComparison.Ordinal),
                        "A history action hid its title, interpreted it as markup or could not be used");
                    var state = new AdvisorOperationReviewState(proposal, actions[i].PlanId);
                    Require(state.Current.Id == actions[i].PlanId && state.SelectedPlanIds.SequenceEqual(new[] { state.Current.Id }) &&
                        service.PreviewProposal(proposal, language, state.Current.Id).Drawings.Single().Coordinates[0].Latitude == i + 1 && log.Count == 0,
                        "Opening a history action showed another map, selected an invisible alternative or executed work");
                }
                var waiting = chat.Begin(new AdvisorQuestion("Another consultation"));
                var busy = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                Require(busy.Descendants().Where(e => actions.Any(a => a.Name == (string?)e.Attribute(x + "Name")))
                    .All(e => (string?)e.Attribute("IsEnabled") == "false"), "An alternative stayed actionable during another consultation");
                waiting.Cancel();
            }
            var selected = new AdvisorOperationReviewState(proposal, "c"); service.Approve(proposal, selected.SelectedPlanIds);
            Require(log.SequenceEqual(new[] { "custom.history:three:3" }) &&
                AdvisorTranscript.ReviewActions(proposal, 0, AdvisorText.Load("en")).Single().PlanId == null &&
                proposal.State == AdvisorProposalState.Registered,
                "Direct plan selection executed a different alternative or retained obsolete approval actions");
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var buttons = rendered.Descendants().Where(e => ((string?)e.Attribute(x + "Name"))?.StartsWith("ReviewProposal", StringComparison.Ordinal) == true).ToArray();
                Require(buttons.Length == 1 && (string?)buttons[0].Attribute("Content") == text["operation.accept"] &&
                    (string?)buttons[0].Attribute("IsEnabled") == "false" &&
                    buttons[0].Parent!.Elements().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string?)e.Attribute("Text"))
                        .SequenceEqual(new[] { text.Format("operation.review_plan", 3, proposal.Plans[2].Title) }) &&
                    turn.Exchange!.Answer == "Original answer",
                    "Accepted alternatives did not collapse to one disabled common action or changed the original answer");
            }
        });
        check("Accepted history keeps original numbers and literal titles for every selected plan and never labels an unsuccessful preflight as adopted", () =>
        {
            var log = new List<string>(); var command = new Command("custom.accepted", log);
            var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Create alternatives"));
            var service = Service(command, turn.OperationId);
            var first = Plan("a", Order(command, "one", 1), Order(command, "one", 2)); first["title"] = "<第一> & 警戒";
            service.SubmitPlans(turn.OperationId, Batch(first, Plan("b", Order(command, "two", 3)), Plan("c", Order(command, "three", 4))));
            turn.Finish(new AdviceResult("Original answer", 1, 0, null), service);
            var proposal = turn.Proposal!;
            command.Available = 0; service.Approve(proposal, new[] { "c", "a" });
            foreach (var language in new[] { "en", "ja" })
            {
                var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load(language), "Adviser", "empty"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var buttons = rendered.Descendants().Where(e => ((string?)e.Attribute(x + "Name"))?.StartsWith("ReviewProposal", StringComparison.Ordinal) == true).ToArray();
                Require(proposal.State == AdvisorProposalState.Pending && buttons.Length == 3 &&
                    !buttons[0].Parent!.Elements().Any(e => e.Name.LocalName == "TextBlock") && log.Count == 0,
                    "Preflight failure mislabeled unexecuted selections as adopted or removed the remaining choices");
            }
            command.Available = 100; service.Approve(proposal, new[] { "c", "a" });
            _ = new AdvisorOperationReviewState(proposal, "b");
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var button = rendered.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ReviewProposal0");
                var labels = button.Parent!.Elements().Where(e => e.Name.LocalName == "TextBlock").ToArray();
                Require(proposal.State == AdvisorProposalState.Registered && (string?)button.Attribute("Content") == text["operation.accept"] &&
                    (string?)button.Attribute("IsEnabled") == "false" && labels.Select(e => (string?)e.Attribute("Text")).SequenceEqual(new[] {
                        text.Format("operation.review_plan", 1, proposal.Plans[0].Title), text.Format("operation.review_plan", 3, proposal.Plans[2].Title) }) &&
                    turn.Exchange!.Answer == "Original answer" &&
                    log.SequenceEqual(new[] { "custom.accepted:one:1", "custom.accepted:one:2", "custom.accepted:three:4" }),
                    "Accepted history lost original proposal numbers, duplicated a multi-order plan, changed a literal title or followed a different selection");
            }
        });
        check("Batch transport normalizes unit and ammunition aliases before retention and labels corrections with plan and operation identity", () =>
        {
            var unit = new BuoyUnit(); var command = SonobuoyFixture.Create(() => new IBattleSonobuoyUnit[] { unit });
            var service = new AdvisorOperations(_ => true); service.Register(command);
            using var gateway = new AdvisorOperationGateway(service);
            var references = new AdvisorOperationReferences(new AdvisorUnitReferences(new[] { ("own_unit_1", 42) }),
                new AdvisorWeaponReferences(new[] { ("weapon_reference_1", "mod_buoy", "Custom buoy") }));
            using var session = gateway.CreateSession("aliases", AdvisorPrompts.Current, references)!;
            JObject Drop(string target, string ammunition) => new() { ["operation_id"] = command.Id, ["target_ref"] = target,
                ["arguments"] = SonobuoyFixture.Arguments(ammunition, 26.5, 127) };
            var request = Batch(Plan("a", Drop("own_unit_1", "Custom buoy")), Plan("b", Drop("unit:own_unit_1", "weapon_reference_1"))); request["action"] = "submit";
            var result = OperationGatewayTests.Finish(gateway, session.ExecuteAsync(request, CancellationToken.None));
            var proposal = service.Proposal("aliases")!;
            Require((string?)result["status"] == "proposed" && result["plan_resolutions"]?.Count() == 4 &&
                (string?)result["plan_resolutions"]?[3]?["plan_id"] == "b" && (int?)result["plan_resolutions"]?[3]?["operation_index"] == 0 &&
                (string?)result["plan_resolutions"]?[3]?["argument_resolutions"]?[0]?["resolved"] == "mod_buoy" &&
                proposal.Plans.All(p => p.Orders.Single().Target == "unit:42" && (string?)p.Orders.Single().Arguments["drops"]![0]!["ammunition"] == "mod_buoy") &&
                (string?)session.History[0]?["request"]?["plans"]?[0]?["operations"]?[0]?["target_ref"] == "own_unit_1",
                "Normalization lost canonical identities, plan boundaries or original evidence");
            _ = OperationGatewayTests.Finish(gateway, session.ExecuteAsync(request, CancellationToken.None));
            service.FinishProposal("aliases", true); service.Approve(proposal, new[] { "b" });
            Require(unit.Count == 1 && proposal.Plans.Count == 2, "An identical normalized retry duplicated or executed both alternatives");
        });
    }
    private static AdvisorOperations Service(Command command, string owner)
    { var service = new AdvisorOperations(_ => true); service.Register(command); service.BeginProposal(owner); return service; }
    private static JObject Batch(params JObject[] plans) => new() { ["request_id"] = "submission", ["plans"] = new JArray(plans) };
    private static JObject Plan(string id, params JObject[] orders) => SonobuoyFixture.Queued(new JObject
    { ["plan_id"] = id, ["title"] = "Plan " + id, ["summary"] = "Trade-off " + id, ["operations"] = new JArray(orders) });
    private static JObject Order(Command operation, string target, int index) => new()
    { ["operation_id"] = operation.Id, ["target_ref"] = target, ["arguments"] = new JObject { ["index"] = index } };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); } catch (AdvisorOperationException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private sealed class Command : IAdvisorOperation, IAdvisorOperationPlans, IAdvisorOperationPreview
    {
        public string Id { get; }
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Example";
        public bool RequiresApproval => Approval;
        public AdvisorOperationPlanPolicy PlanPolicy { get; }
        internal bool Approval = true, Unknown;
        internal int Available = 100, FailAt = -1;
        private readonly List<string> log;
        internal Command(string id, List<string> log, int minimum = 1, int maximum = 3, AdvisorPlanSelection selection = AdvisorPlanSelection.Multiple, int? target = null)
        { Id = id; this.log = log; PlanPolicy = new(target ?? maximum, minimum, maximum, selection); }
        public JObject Definition() => new() { ["id"] = Id };
        public JArray Candidates() => new();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders)
        {
            if (orders.Any(o => (int)o.Arguments["index"]! < 0)) throw new AdvisorOperationException(400, "invalid_command_argument");
            if (orders.GroupBy(o => o.Target).Any(g => g.Count() > Available)) throw new AdvisorOperationException(409, "insufficient_stock");
        }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Id + ":" + orders.Count;
        public JObject Execute(string target, JObject arguments)
        {
            var index = (int)arguments["index"]!;
            if (index == FailAt) { if (Unknown) throw new IOException("native unknown"); throw new AdvisorOperationException(409, "native rejected"); }
            log.Add(Id + ":" + target + ":" + index); return new JObject();
        }
        public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language) => new(orders.Select(o =>
            AdvisorMapDrawing.Point(new AdvisorMapCoordinate((int)o.Arguments["index"]!, 127))));
    }
    private sealed class BuoyUnit : IBattleSonobuoyUnit
    {
        public int Id => 42;
        public string Name => "Hobby";
        public string? UnavailableReason => null;
        public string Depth => "alternating";
        public JObject Position => new();
        public ReservationRoute? ReservationRoute => new(new AdvisorMapCoordinate(26.5, 127.5), null);
        internal int Count;
        public IReadOnlyList<BattleSonobuoyAmmo> Ammunition() => new[] { new BattleSonobuoyAmmo("mod_buoy", "Custom buoy", 1, 1 - Count, true) };
        public void ClearReservations() => Count = 0;
        public void Append(string ammunition, double latitude, double longitude) => Count++;
    }
    private sealed class Map : IAdvisorMap
    {
        internal int Active;
        internal List<double> Latitudes = new();
        public void Center(AdvisorMapCoordinate location) { }
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings)
        { Active++; Latitudes.AddRange(drawings.Select(d => d.Coordinates[0].Latitude)); return new Lease(() => Active--); }
        private sealed class Lease : IDisposable
        {
            private Action? release;
            internal Lease(Action release) => this.release = release;
            public void Dispose() { var action = release; release = null; action?.Invoke(); }
        }
    }
}
