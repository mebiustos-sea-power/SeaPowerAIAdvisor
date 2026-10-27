using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using Api = SeaPowerAIAdvisor.Api;
using Client = SeaPowerAIAdvisor.ExtensionFixture;

internal static class CommandSdkTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Public command registration preserves the provider endpoint and rejects unavailable, duplicate and incompatible commands", () =>
        {
            var extension = new Client.CommandExtension(); var catalog = new AdvisorOperationCatalog(); var providers = 0;
            Throws<InvalidOperationException>(() => Api.AdvisorApi.RegisterOperation(extension));
            Api.AdvisorApi.Attach(_ => { providers++; return true; }, command => { catalog.Register(command); return true; });
            try
            {
                Require(Api.AdvisorApi.RegisterOperation(extension) && Api.AdvisorApi.Register(new Client.StandardExtension()) && providers == 1,
                    "The command endpoint intercepted or broke provider registration");
                Throws<InvalidOperationException>(() => Api.AdvisorApi.RegisterOperation(extension));
                Throws<NotSupportedException>(() => Api.AdvisorApi.RegisterOperation(new Client.CommandExtension { ApiVersion = 99 }));
            }
            finally { Api.AdvisorApi.Detach(); }
            Throws<InvalidOperationException>(() => Api.AdvisorApi.RegisterOperation(extension));
        });
        check("SDK metadata is immutable, translated and inspectable without creating command sessions or reading game state", () =>
        {
            var localizations = new Dictionary<string, string> { ["en"] = Text, ["ja"] = "{\"name\":\"操作\"}" };
            var created = 0; var extension = new Client.CommandExtension { Definition = new Api.OperationDefinition("fixture.metadata", Api.OperationExecutionMode.Immediate, "Original", "{\"type\":\"object\"}", localizations),
                Open = _ => { created++; throw new InvalidOperationException("Metadata created a session"); } };
            var catalog = new AdvisorOperationCatalog(); catalog.Register(extension);
            localizations["en"] = "broken";
            extension.Definition = new Api.OperationDefinition("changed.id", Api.OperationExecutionMode.Reservation, "Changed", "{\"type\":\"object\"}", new Dictionary<string,string> { ["en"] = Text });
            var service = Service(catalog);
            Require(service.Inspect("ja-JP").Single().Text["name"] == "操作" && service.Inspect("de").Single().Text["name"] == "Fixture" &&
                (string?)service.Inspect("en").Single().Definition["description"] == "Original" &&
                (string?)service.Inspect("en").Single().Definition["execution_mode"] == "immediate" && created == 0, "Registration metadata changed or inspection used live game state");
            service.Retire(); Require(created == 0, "Retiring an unused command created its handler");
            foreach (var bad in new[] { "{broken", "{}", "{\"name\":17}" })
                Throws<InvalidDataException>(() => new AdvisorOperationCatalog().Register(new Client.CommandExtension { Definition = new Api.OperationDefinition(
                    "fixture.invalid", Api.OperationExecutionMode.Immediate, "Invalid", "{\"type\":\"object\"}", new Dictionary<string,string> { ["en"] = bad }) }));
            Throws<ArgumentException>(() => new AdvisorOperationCatalog().Register(new Client.CommandExtension { Definition = new Api.OperationDefinition(
                "fixture.invalid", Api.OperationExecutionMode.Immediate, "Invalid", "{\"type\":\"array\"}", new Dictionary<string,string> { ["en"] = Text }) }));
        });
        check("Individual extension versions remain optional display metadata and inspection performs no execution", () =>
        {
            var created = 0;
            var command = new Client.CommandExtension { Version = "command-0.3.0-beta", Open = _ =>
            { created++; throw new InvalidOperationException("Inspection created a command session"); } };
            var adapter = new Client.StandardExtension { Version = "adapter-2.1", Open = (_, _, _, _) =>
            { created++; throw new InvalidOperationException("Inspection opened an API connection"); } };
            var commands = new AdvisorOperationCatalog(); commands.Register(command);
            var adapters = new AdvisorServiceCatalog(Array.Empty<AdvisorService>()); adapters.Register(adapter);
            command.Version = "changed"; adapter.Version = "changed";
            var game = new Game(); var operations = Service(commands, game);
            var shownCommand = operations.Inspect("ja").Single(); var shownAdapter = adapters.Services.Single();
            Require(shownCommand.Version == "command-0.3.0-beta" && shownAdapter.Version == "adapter-2.1" &&
                shownCommand.Definition["version"] == null && shownAdapter.Id == adapter.Id && created == 0 && game.Reads == 0,
                "Display versions were replaced, changed after registration, leaked into AI definitions or opened an execution");
            operations.Retire();
            foreach (var missing in new string?[] { null, "", "   " })
            {
                var withoutVersion = new AdvisorOperationCatalog(); withoutVersion.Register(new Client.CommandExtension { Version = missing });
                var unversioned = Service(withoutVersion);
                Require(unversioned.Inspect("en").Single().Version == null, "An omitted command version was synthesized");
                unversioned.Retire();
                var noAdapterVersion = new AdvisorServiceCatalog(Array.Empty<AdvisorService>());
                noAdapterVersion.Register(new Client.StandardExtension { Version = missing });
                Require(noAdapterVersion.Services.Single().Version == null, "An omitted adapter version was synthesized");
            }
            foreach (var invalid in new[] { "line1\nline2", new string('x', 81) })
            {
                Throws<ArgumentException>(() => new AdvisorOperationCatalog().Register(new Client.CommandExtension { Version = invalid }));
                Throws<ArgumentException>(() => new AdvisorServiceCatalog(Array.Empty<AdvisorService>()).Register(new Client.StandardExtension { Version = invalid }));
            }
        });
        check("An API-only command uses the existing approval engine, live revalidation, original order and replay protection", () =>
        {
            var executed = new List<int>(); var validations = new List<int>(); var ready = true; var sessions = 0;
            var extension = new Client.CommandExtension { Open = _ => { sessions++; return new Client.CommandHandler
            {
                Check = orders => { validations.Add(orders.Count); if (!ready) throw new Api.OperationException(409, "target_unavailable"); },
                Run = order => { executed.Add((int)JObject.Parse(order.ArgumentsJson)["index"]!); return "{\"accepted\":true}"; }
            }; } };
            var catalog = new AdvisorOperationCatalog(); catalog.Register(extension); var service = Service(catalog);
            Require(((JArray)service.Candidates("fixture.command")["candidates"]!).Count == 1 && sessions == 1, "Candidate dispatch failed");
            service.BeginProposal("test"); var first = Request(service, 0); var second = Request(service, 1);
            OperationSubmissionFixture.Submit(service, "test", first, second);
            var proposal = service.FinishProposal("test", true)!;
            Require(executed.Count == 0 && service.DescribeProposal(proposal, "en").EndsWith("Fixture proposal", StringComparison.Ordinal) && service.PreviewProposal(proposal, "en").Drawings.Count == 0,
                "A command ran before approval or required an optional preview");
            service.Approve(proposal); service.Approve(proposal); OperationSubmissionFixture.Submit(service, "test", first, second);
            Require(executed.SequenceEqual(new[] { 0, 1 }) && validations.SequenceEqual(new[] { 2, 2, 2, 2 }), "Approval changed order, skipped combined validation or executed twice");
            service.BeginProposal("later"); OperationSubmissionFixture.Submit(service, "later", Request(service, 2)); var later = service.FinishProposal("later", true)!;
            ready = false; service.Approve(later);
            Require(later.State == AdvisorProposalState.Pending && later.ErrorCode == "target_unavailable" && executed.Count == 2, "Approval ignored changed live conditions"); service.Retire();
        });
        check("Retiring SDK commands disposes sessions and only their map leases, and revokes retained game services", () =>
        {
            var game = new Game(); var map = new Map(); Api.IAdvisorOperationContext? retained = null; Api.IAdvisorGameSupport? oldGame = null;
            Api.IAdvisorMap? oldMap = null; var closed = 0; var catalog = new AdvisorOperationCatalog();
            catalog.Register(new Client.CommandExtension { Open = context =>
            {
                retained = context; oldGame = context.Game; oldMap = context.Map;
                _ = context.Map.Draw(new[] { Api.AdvisorMapDrawing.Point(new Api.AdvisorMapCoordinate(26, 127)) });
                return new Client.CommandHandler { Candidates = () => { _ = context.Game.GetUnits(); return "[]"; }, Close = () => closed++ };
            } });
            using var unrelated = map.Draw(Array.Empty<Api.AdvisorMapDrawing>());
            var service = Service(catalog, game, map); service.Candidates("fixture.command");
            Require(game.Reads == 1 && map.Active == 2, "SDK services did not dispatch to the host");
            var crossThread = Task.Run(() => { try { _ = oldGame!.GetUnits(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
            Require(crossThread && game.Reads == 1, "A worker thread accessed live game data");
            service.Retire(); service.Retire();
            Require(closed == 1 && map.Active == 1, "Retirement leaked resources, disposed twice or cleared unrelated drawings");
            Throws<ObjectDisposedException>(() => oldGame!.GetUnits()); Throws<ObjectDisposedException>(() => oldMap!.Center(new Api.AdvisorMapCoordinate(26,127)));
            Throws<ObjectDisposedException>(() => _ = retained!.Game);
        });
        check("One failing SDK cleanup does not leave another command session or its support alive", () =>
        {
            var catalog = new AdvisorOperationCatalog(); var closed = 0; Api.IAdvisorGameSupport? support = null;
            catalog.Register(new Client.CommandExtension { Open = _ => new Client.CommandHandler { Close = () => throw new InvalidOperationException("cleanup failure") } });
            catalog.Register(new Client.CommandExtension { Definition = new Api.OperationDefinition("fixture.second", Api.OperationExecutionMode.Immediate, "Second", "{\"type\":\"object\"}", new Dictionary<string,string> { ["en"] = Text }),
                Open = context => { support = context.Game; return new Client.CommandHandler { Close = () => closed++ }; } });
            var service = Service(catalog); service.Candidates("fixture.command"); service.Candidates("fixture.second");
            using var gateway = new AdvisorOperationGateway(service);
            using var session = gateway.CreateSession("closing", AdvisorPrompts.Current)!;
            var pending = session.ExecuteAsync(new JObject { ["action"] = "list" }, CancellationToken.None);
            Throws<AggregateException>(() => gateway.Dispose()); Require(closed == 1, "Cleanup stopped after the first error");
            Require((string?)pending.GetAwaiter().GetResult()["error"]?["code"] == "operation_scope_changed",
                "A failed external cleanup stranded a queued consultation task");
            Throws<ObjectDisposedException>(() => support!.GetUnits());
        });
        check("SDK cleanup retains handler and drawing failures and remains idempotent after errors", () =>
        {
            foreach (var failures in new (bool Handler, bool Drawing)[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var handlerError = new InvalidOperationException("Handler cleanup failed");
                var drawingError = new InvalidOperationException("Drawing cleanup failed");
                var closed = 0; var drawingsClosed = 0; Api.IAdvisorGameSupport? support = null;
                var map = new Map { Close = () => { drawingsClosed++; if (failures.Drawing) throw drawingError; } };
                var catalog = new AdvisorOperationCatalog();
                catalog.Register(new Client.CommandExtension { Open = context =>
                {
                    support = context.Game;
                    _ = context.Map.Draw(Array.Empty<Api.AdvisorMapDrawing>());
                    return new Client.CommandHandler { Close = () => { closed++; if (failures.Handler) throw handlerError; } };
                } });
                var service = Service(catalog, map: map); service.Candidates("fixture.command");
                var errors = Array.Empty<Exception>();
                try { service.Retire(); }
                catch (AggregateException error) { errors = error.Flatten().InnerExceptions.ToArray(); }
                Require(errors.Length == (failures.Handler ? 1 : 0) + (failures.Drawing ? 1 : 0) &&
                    errors.Contains(handlerError) == failures.Handler && errors.Contains(drawingError) == failures.Drawing,
                    "SDK cleanup hid or replaced an original failure");
                service.Retire();
                Require(closed == 1 && drawingsClosed == 1 && map.Active == 0,
                    "A cleanup failure skipped another resource or caused duplicate disposal");
                Throws<ObjectDisposedException>(() => support!.GetUnits());
            }
        });
        check("Malformed SDK results and unexpected failures remain faults and cannot be blindly replayed", () =>
        {
            foreach (var malformed in new[] { false, true })
            {
                var calls = 0; var catalog = new AdvisorOperationCatalog();
                catalog.Register(new Client.CommandExtension { Open = _ => new Client.CommandHandler { Run = _ =>
                { calls++; if (malformed) return "not json"; throw new InvalidOperationException("unexpected native failure"); } } });
                var service = Service(catalog); var request = Request(service, 0);
                Throws<Exception>(() => OperationSubmissionFixture.Adopt(service, request));
                Require((string?)OperationSubmissionFixture.Adopt(service, request)["status"] == "unknown" && calls == 1,
                    "A fault became a normal rejection or repeated its side effect");
                service.Retire();
            }
        });
    }
    private const string Text = "{\"name\":\"Fixture\",\"summary\":\"Summary\",\"requirements\":\"Conditions\"}";
    private static AdvisorOperations Service(AdvisorOperationCatalog catalog, Game? game = null, Map? map = null)
    { var service = new AdvisorOperations(_ => true); foreach (var operation in catalog.CreateOperations(game ?? new Game(), map ?? new Map())) service.Register(operation); return service; }
    private static JObject Request(AdvisorOperations service, int index) => new() { ["scope_id"] = service.ScopeId,
        ["request_id"] = "sdk-" + index, ["operation"] = "fixture.command", ["target_ref"] = "fixture:target", ["arguments"] = new JObject { ["index"] = index } };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Game : Api.IAdvisorGameSupport
    {
        internal int Reads;
        public bool IsInformationEnabled(string category) => true;
        public IReadOnlyList<Api.GameUnitInfo> GetUnits() { Reads++; return Array.Empty<Api.GameUnitInfo>(); }
        public IReadOnlyList<Api.AmmunitionInfo> GetAmmunition(string target) => throw new InvalidOperationException("unused");
        public Api.ReservationRoute? GetReservationRoute(string target) => throw new InvalidOperationException("unused");
        public string GetSonobuoyDepth(string target) => throw new InvalidOperationException("unused");
        public void AppendSonobuoyDrop(string target, string ammunition, Api.AdvisorMapCoordinate position) => throw new InvalidOperationException("unused");
    }
    private sealed class Map : Api.IAdvisorMap
    {
        internal int Active;
        internal Action Close = () => { };
        public void Center(Api.AdvisorMapCoordinate position) { }
        public IDisposable Draw(IReadOnlyList<Api.AdvisorMapDrawing> drawings) { Active++; return new Drawing(this); }
        private sealed class Drawing : IDisposable
        {
            private Map? owner; internal Drawing(Map owner) => this.owner = owner;
            public void Dispose() { if (owner == null) return; var previous = owner; owner = null; previous.Active--; previous.Close(); }
        }
    }
}
