using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

internal static class Program
{
    private static int passed;
    private static readonly IAdvisorOperationExtension Extension = (IAdvisorOperationExtension)Activator.CreateInstance(
        Assembly.Load("SeaPowerAIAdvisor.ExampleCommand").GetType("SeaPowerAIAdvisor.ExampleCommand.SonobuoyCommandExtension", true)!, nonPublic: true)!;
    private static int Main()
    {
        try
        {
            Check("Disabled host and disabled external MOD return normally from registration", () =>
            {
                var registration = Assembly.Load("SeaPowerAIAdvisor.ExampleCommand").GetType("SeaPowerAIAdvisor.ExampleCommand.CommandRegistration", true)!
                    .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!;
                var api = typeof(AdvisorApi);
                var attach = api.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "Attach" && m.GetParameters().Length == 2);
                var detach = api.GetMethod("Detach", BindingFlags.Static | BindingFlags.NonPublic)!;
                registration.Invoke(null, null);
                var attempts = 0;
                Func<IAdvisorExtension, bool> provider = _ => true;
                Func<IAdvisorOperationExtension, bool> command = _ => { attempts++; return false; };
                attach.Invoke(null, new object[] { provider, command });
                try { registration.Invoke(null, null); Require(attempts == 1, "Disabled MOD did not reach the host activation check"); }
                finally { detach.Invoke(null, null); }
            });
            Check("Candidates distinguish unloaded, grounded, unavailable and usable units", () =>
            {
                var context = new Context();
                context.Game.Units.Add(new GameUnitInfo(2, "Grounded", null, true, false, null));
                context.Game.Units.Add(new GameUnitInfo(3, "Empty", null, false, false, null));
                context.Game.Units.Add(new GameUnitInfo(4, "Unavailable", null, true, true, "following_formation"));
                using var handler = Extension.CreateSession(context);
                var rows = JArray.Parse(handler.GetCandidates()).Cast<JObject>().ToArray();
                Require(rows.Length == 3 && (bool)rows[0]["available"]! &&
                    (string?)rows[1]["unavailable_reason"] == "unit_not_airborne" &&
                    (string?)rows[2]["unavailable_reason"] == "following_formation", "Wrong candidate applicability");
                Require((int)rows[0]["ammunition"]![0]!["available_count"]! == 2 && context.Game.Drops.Count == 0, "Candidate lookup changed orders");
            });
            Check("Information permissions reject lookup and registration without writes", () =>
            {
                var context = new Context(); context.Game.WeaponsEnabled = false;
                using var handler = Extension.CreateSession(context);
                Reject("information_disabled", () => handler.GetCandidates());
                Reject("information_disabled", () => handler.Execute(Order()));
                Require(context.Game.Drops.Count == 0, "Disabled information allowed a write");
            });
            Check("Malformed coordinates, extra arguments, unknown targets and ammunition reject safely", () =>
            {
                var context = new Context(); using var handler = Extension.CreateSession(context);
                foreach (var json in new[] { "[]", "{", "{\"latitude\":91,\"longitude\":127,\"ammunition\":\"buoy\"}",
                    "{\"latitude\":26,\"longitude\":127,\"ammunition\":\"buoy\",\"depth\":\"below_layer\"}" })
                    Reject("invalid_sonobuoy_arguments", () => handler.Execute(new OperationOrder("unit:1", json)));
                Reject("target_unavailable", () => handler.Execute(new OperationOrder("unit:999", Order().ArgumentsJson)));
                Reject("sonobuoy_not_carried", () => handler.Execute(new OperationOrder("unit:1", Order().ArgumentsJson.Replace("buoy", "Display name"))));
                Require(context.Game.Drops.Count == 0, "Invalid arguments registered an order");
            });
            Check("Combined stock is checked separately for each target", () =>
            {
                var context = new Context(); context.Game.Stock = 1;
                context.Game.Units.Add(new GameUnitInfo(2, "Second", null, true, true, null));
                using var handler = Extension.CreateSession(context);
                Reject("no_available_sonobuoy", () => handler.Validate(new[] { Order(), Order() }));
                handler.Validate(new[] { Order(), new OperationOrder("unit:2", Order().ArgumentsJson) });
                Require(context.Game.Drops.Count == 0, "Validation changed game state");
            });
            Check("Review is localized, ordered and free of registration or direct map effects", () =>
            {
                var context = new Context(); using var handler = Extension.CreateSession(context);
                var orders = new[] { Order(26.1), Order(26.2) };
                foreach (var language in new[] { "en", "ja" })
                {
                    var description = handler.DescribeProposal(orders, language);
                    Require(description.Contains("Hobby") && description.IndexOf("26.10000", StringComparison.Ordinal) <
                        description.IndexOf("26.20000", StringComparison.Ordinal), "Review lost its target or order");
                }
                var preview = ((IAdvisorOperationPreview)handler).Preview(orders, "ja");
                var line = preview.Drawings.Single(d => d.Kind == AdvisorMapDrawingKind.Line);
                Require(line.Coordinates.Count == 3 && line.Coordinates[0].Latitude == 25 &&
                    line.Coordinates[1].Latitude == 26.1 && preview.Drawings.Count == 3 && context.Game.Drops.Count == 0,
                    "Preview lost the queued endpoint or proposed order");
                context.Game.MovementEnabled = false;
                var reads = context.Game.RouteReads;
                preview = ((IAdvisorOperationPreview)handler).Preview(orders, "en");
                Require(context.Game.RouteReads == reads && preview.Drawings.First().Coordinates.Count == 2,
                    "Preview read disabled movement information");
            });
            Check("Execution uses exact coordinates, appends in order and preserves the current depth setting", () =>
            {
                var context = new Context(); using var handler = Extension.CreateSession(context);
                var orders = new[] { Order(26.1), Order(26.2) }; handler.Validate(orders);
                context.Game.Depth = "below_layer";
                foreach (var order in orders) Require((string?)JObject.Parse(handler.Execute(order))["depth"] == "below_layer", "Stale depth reported");
                Require(context.Game.Drops.Count == 2 && context.Game.Drops.Select(d => d.Position.Latitude).SequenceEqual(new[] { 26.1, 26.2 }) &&
                    context.Game.Drops.All(d => d.Target == "unit:1" && d.Ammo == "buoy" && d.Position.Longitude == 127.4) &&
                    context.Game.Depth == "below_layer", "Execution changed the target, order, position or depth");
            });
            Check("Live loss of stock, flight or target is rechecked at execution", () =>
            {
                foreach (var change in new[] { "stock", "flight", "target" })
                {
                    var context = new Context(); using var handler = Extension.CreateSession(context); handler.Validate(new[] { Order() });
                    if (change == "stock") context.Game.Stock = 0;
                    else if (change == "flight") context.Game.Units[0] = new GameUnitInfo(1, "Hobby", null, true, false, null);
                    else context.Game.Units.Clear();
                    Reject(change == "stock" ? "no_available_sonobuoy" : change == "flight" ? "unit_not_airborne" : "target_unavailable",
                        () => handler.Execute(Order()));
                    Require(context.Game.Drops.Count == 0, "Live revalidation allowed an invalid write");
                }
            });
            Console.WriteLine("PASS " + passed + " external command checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static OperationOrder Order(double latitude = 26.1) => new("unit:1", new JObject
        { ["latitude"] = latitude, ["longitude"] = 127.4, ["ammunition"] = "buoy" }.ToString());
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(string code, Action action)
    {
        try { action(); } catch (OperationException error) when (error.Code == code) { return; }
        throw new Exception("Expected rejection: " + code);
    }
    private static void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private sealed class Context : IAdvisorOperationContext
    {
        public Game Game { get; } = new();
        IAdvisorGameSupport IAdvisorOperationContext.Game => Game;
        public IAdvisorMap Map { get; } = new NoMap();
        public IAdvisorOperationText Text { get; } = new Text();
    }
    private sealed class Game : IAdvisorGameSupport
    {
        public List<GameUnitInfo> Units = new() { new GameUnitInfo(1, "Hobby", new AdvisorMapCoordinate(26, 127), true, true, null) };
        public List<(string Target, string Ammo, AdvisorMapCoordinate Position)> Drops = new();
        public bool WeaponsEnabled = true, MovementEnabled = true;
        public int Stock = 2, RouteReads;
        public string Depth = "alternating";
        public bool IsInformationEnabled(string category) => category switch { "battle_weapons" => WeaponsEnabled, "battle_movement_plans" => MovementEnabled, _ => true };
        public IReadOnlyList<GameUnitInfo> GetUnits() => Units;
        public IReadOnlyList<AmmunitionInfo> GetAmmunition(string target) => target == "unit:3" ? Array.Empty<AmmunitionInfo>() :
            new[] { new AmmunitionInfo("buoy", "Display name", "Bomb", "Sonobuoy", Stock, Stock, true) };
        public ReservationRoute GetReservationRoute(string target) { RouteReads++; return new ReservationRoute(new AdvisorMapCoordinate(26, 127), new AdvisorMapCoordinate(25, 126)); }
        public string GetSonobuoyDepth(string target) => Depth;
        public void AppendSonobuoyDrop(string target, string ammunition, AdvisorMapCoordinate position) { Drops.Add((target, ammunition, position)); Stock--; }
    }
    private sealed class NoMap : IAdvisorMap
    {
        public void Center(AdvisorMapCoordinate location) => throw new Exception("Preview must not center the map");
        public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings) => throw new Exception("Preview drawing belongs to the host");
    }
    private sealed class Text : IAdvisorOperationText
    {
        public string Get(string language, string key) => (string)JObject.Parse(Extension.GetDefinition().Localizations[language])[key]!;
        public string Format(string language, string key, params object[] arguments) => string.Format(CultureInfo.InvariantCulture, Get(language, key), arguments);
    }
}
