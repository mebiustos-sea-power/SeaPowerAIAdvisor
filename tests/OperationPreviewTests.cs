using IAdvisorOperationPreview = SeaPowerAIAdvisor.IAdvisorOperationPreview;
using SeaPowerAIAdvisor.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class OperationPreviewTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Commands supply geographic previews without making them mandatory for other operations", () =>
        {
            var service = new AdvisorOperations(() => true);
            var text = new TextOperation("custom.text"); var visible = new VisibleOperation("custom.visible");
            service.Register(text); service.Register(visible);
            var proposal = Plan(service, "mixed", text.Id, visible.Id);
            var preview = service.PreviewProposal(proposal, "en");
            Require(preview.Drawings.Select(d => d.Kind).SequenceEqual(new[] { AdvisorMapDrawingKind.Point, AdvisorMapDrawingKind.Line, AdvisorMapDrawingKind.Circle }) &&
                preview.Drawings[2].RadiusNauticalMiles == 3 && text.Executions == 0 && visible.Executions == 0,
                "The common layer discarded an extension's geometry, confused radius units or executed previewed work");
            var textOnly = Plan(service, "text", text.Id);
            Require(service.PreviewProposal(textOnly, "en").Drawings.Count == 0, "A non-geographic operation was forced to create a map preview");
            var empty = new EmptyPreviewOperation(); service.Register(empty);
            var map = new Map { FailCenter = true }; using var review = new AdvisorOperationPreview(map);
            review.Show(service, Plan(service, "empty", empty.Id), "en"); review.Tick(service);
            Require(map.Centers == 0 && map.Active == 0, "An empty preview moved the map or created an unnecessary overlay");
        });
        check("Opening, switching and reopening approval previews preserve the map view and only dispose their own drawings", () =>
        {
            var service = new AdvisorOperations(() => true); var command = new VisibleOperation("custom.visible"); service.Register(command);
            var first = Plan(service, "first", command.Id); var second = Plan(service, "second", command.Id);
            var map = new Map { FailCenter = true }; using var unrelated = map.Draw(Array.Empty<AdvisorMapDrawing>());
            using var review = new AdvisorOperationPreview(map);
            review.Show(service, first, "en");
            for (var i = 0; i < 10; i++) review.Tick(service);
            Require(map.Active == 2 && map.Centers == 0, "Opening or ticking a review moved the map or expired the preview");
            review.Dispose(); review.Dispose();
            Require(map.Active == 1 && first.State == AdvisorProposalState.Pending && command.Executions == 0,
                "Closing the review removed unrelated map content, cancelled the proposal or registered an order");
            review.Show(service, first, "en"); review.Show(service, second, "en");
            Require(map.Active == 2 && map.Centers == 0 && first.State == AdvisorProposalState.Pending, "Reopening or switching reviews moved the map, left duplicate overlays or discarded the earlier proposal");
            review.Dispose(); Require(map.Active == 1, "The replaced preview kept its drawings");
        });
        check("Retired, removed, replaced, disabled and registered proposals lose their previews", () =>
        {
            foreach (var cause in new[] { "retire", "remove", "replace", "disable", "register", "service" })
            {
                var enabled = true; var service = new AdvisorOperations(() => enabled);
                var command = new VisibleOperation("custom.visible"); service.Register(command);
                var proposal = Plan(service, "turn", command.Id); var map = new Map();
                using var review = new AdvisorOperationPreview(map); review.Show(service, proposal, "en");
                AdvisorOperations? active = service;
                if (cause == "retire") service.Retire();
                if (cause == "remove") service.DiscardProposalsExcept(Array.Empty<string>());
                if (cause == "replace") service.BeginProposal("turn");
                if (cause == "disable") enabled = false;
                if (cause == "register") service.Approve(proposal);
                if (cause == "service") active = null;
                review.Tick(active); review.Tick(active);
                Require(map.Active == 0 && command.Executions == (cause == "register" ? 1 : 0),
                    "A stale preview survived " + cause + " or teardown executed work");
            }
        });
        check("Map preview errors release acquired drawings and propagate instead of masking a broken renderer", () =>
        {
            var service = new AdvisorOperations(() => true); var command = new VisibleOperation("custom.visible"); service.Register(command);
            var proposal = Plan(service, "turn", command.Id);
            var map = new Map { FailCenter = true };
            using var unrelated = map.Draw(Array.Empty<AdvisorMapDrawing>());
            using var preview = new AdvisorOperationPreview(map);
            preview.Show(service, proposal, "en");
            map.FailDraw = true;
            try { preview.Show(service, proposal, "en"); throw new Exception("Renderer failure was hidden"); }
            catch (InvalidOperationException error) when (error.Message == "renderer failure") { }
            Require(map.Active == 1 && map.Centers == 0 && proposal.State == AdvisorProposalState.Pending && command.Executions == 0,
                "Renderer failure leaked a preview, removed unrelated drawings, moved the map or changed pending work");
        });
    }
    private static AdvisorOperationProposal Plan(AdvisorOperations service, string owner, params string[] operations)
    {
        service.BeginProposal(owner);
        Require((string?)OperationSubmissionFixture.Submit(service, owner, operations.Select((id, i) => new JObject {
            ["scope_id"] = service.ScopeId, ["request_id"] = owner + "." + i, ["operation"] = id,
            ["target_ref"] = "example:1", ["arguments"] = new JObject() }).ToArray())["status"] == "proposed", "Test proposal rejected");
        return service.FinishProposal(owner, true)!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private class TextOperation : IAdvisorOperation
    {
        public string Id { get; }
        public AdvisorOperationText Documentation => OperationDocumentationTests.Example;
        public string Description => "Example operation";
        public bool RequiresApproval => true;
        internal int Executions;
        internal TextOperation(string id) => Id = id;
        public JObject Definition() => new() { ["id"] = Id };
        public JArray Candidates() => new();
        public void Validate(IReadOnlyList<AdvisorOperationOrder> orders) { }
        public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language) => Description;
        public JObject Execute(string target, JObject arguments) { Executions++; return new JObject(); }
    }
    private sealed class VisibleOperation : TextOperation, IAdvisorOperationPreview
    {
        internal VisibleOperation(string id) : base(id) { }
        public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language)
        {
            var a = new AdvisorMapCoordinate(27, 128); var b = new AdvisorMapCoordinate(28, 129);
            return new AdvisorMapPreview(new[] { AdvisorMapDrawing.Point(a, "Example"), AdvisorMapDrawing.Line(new[] { a, b }), AdvisorMapDrawing.Circle(a, 3) });
        }
    }
    private sealed class EmptyPreviewOperation : TextOperation, IAdvisorOperationPreview
    {
        internal EmptyPreviewOperation() : base("custom.empty") { }
        public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language) => AdvisorMapPreview.Empty;
    }
    private sealed class Map : IAdvisorMap
    {
        internal int Active, Centers;
        internal bool FailDraw, FailCenter;
        public void Center(AdvisorMapCoordinate coordinate) { if (FailCenter) throw new InvalidOperationException("renderer failure"); Centers++; }
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings)
        {
            if (FailDraw) throw new InvalidOperationException("renderer failure");
            Active++; return new Lease(this);
        }
        private sealed class Lease : IDisposable
        {
            private Map? owner;
            internal Lease(Map map) => owner = map;
            public void Dispose() { var previous = owner; owner = null; if (previous != null) previous.Active--; }
        }
    }
}
