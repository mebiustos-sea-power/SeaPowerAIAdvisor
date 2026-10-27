using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor.ExampleCommand;

internal static class CommandRegistration
{
    internal static void Register()
    {
        // An installed but disabled host has no active registration endpoint.
        if (!AdvisorApi.IsAvailable) return;
        // RegisterOperation returns false when this MOD is disabled in Sea Power's MOD manager.
        AdvisorApi.RegisterOperation(new SonobuoyCommandExtension());
    }
}

internal sealed class SonobuoyCommandExtension : IAdvisorOperationExtension, IAdvisorExtensionVersion
{
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "0.1.0";
    public OperationDefinition GetDefinition() => new("example.seapower.sonobuoy_drop", OperationExecutionMode.Reservation,
        "External SDK example: append a move-and-drop reservation for a player-controlled unit carrying sonobuoys. " +
        "Aircraft must be airborne. Each operation drops one sonobuoy; a plan may contain several operations. " +
        "Use a carried ammunition ID from candidates, preserve the player's depth setting, and plan travel order with drop positions.",
        new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray("latitude", "longitude", "ammunition"),
            ["properties"] = new JObject
            {
                ["latitude"] = new JObject { ["type"] = "number", ["minimum"] = -90, ["maximum"] = 90 },
                ["longitude"] = new JObject { ["type"] = "number", ["minimum"] = -180, ["maximum"] = 180 },
                ["ammunition"] = new JObject { ["type"] = "string", ["description"] = "A carried sonobuoy ID from candidates." }
            }
        }.ToString(Formatting.None), new Dictionary<string, string> { ["en"] = Text("en"), ["ja"] = Text("ja") },
        requiresApproval: true);

    private static string Text(string language)
    {
        using var stream = typeof(SonobuoyCommandExtension).Assembly.GetManifestResourceStream(
            "SeaPowerAIAdvisor.ExampleCommand.Localization." + language + ".json")
            ?? throw new InvalidDataException("Missing example command localization: " + language);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new SonobuoyCommand(context);
}

internal sealed class SonobuoyCommand : IAdvisorOperationHandler, IAdvisorOperationActions, IAdvisorOperationPreview
{
    private readonly IAdvisorOperationContext context;
    internal SonobuoyCommand(IAdvisorOperationContext context) => this.context = context;
    private void RequireInformation()
    {
        if (!context.Game.IsInformationEnabled("battle_units") || !context.Game.IsInformationEnabled("battle_weapons"))
            throw new OperationException(403, "information_disabled");
    }
    private static string? Unavailable(GameUnitInfo unit) => unit.OrderUnavailableReason ??
        (unit.IsAircraft && !unit.IsAirborne ? "unit_not_airborne" : null);
    private IReadOnlyList<AmmunitionInfo> Sonobuoys(string target) => context.Game.GetAmmunition(target)
        .Where(a => a.Type == "Bomb" && a.Subtype == "Sonobuoy").ToArray();
    public string GetCandidates()
    {
        RequireInformation();
        var candidates = new JArray();
        foreach (var unit in context.Game.GetUnits())
        {
            var ammunition = Sonobuoys(unit.Target);
            if (ammunition.Count == 0) continue;
            var reason = Unavailable(unit) ?? (ammunition.Any(a => a.Available > 0 && a.SingleRoundSupported) ? null : "no_available_sonobuoy");
            candidates.Add(new JObject
            {
                ["target_ref"] = unit.Target, ["display_name"] = unit.Name,
                ["available"] = reason == null, ["unavailable_reason"] = reason,
                ["position"] = unit.Position is AdvisorMapCoordinate p
                    ? new JObject { ["latitude_deg"] = p.Latitude, ["longitude_deg"] = p.Longitude } : null,
                ["depth"] = context.Game.GetSonobuoyDepth(unit.Target),
                ["ammunition"] = new JArray(ammunition.Select(a => new JObject
                {
                    ["id"] = a.Id, ["display_name"] = a.Name, ["remaining_count"] = a.Remaining,
                    ["available_count"] = a.Available, ["single_drop_supported"] = a.SingleRoundSupported
                }))
            });
        }
        return candidates.ToString(Formatting.None);
    }
    private (GameUnitInfo Unit, AmmunitionInfo Ammo, AdvisorMapCoordinate Position) Read(OperationOrder order)
    {
        RequireInformation();
        JObject args;
        try { args = JObject.Parse(order.ArgumentsJson); }
        catch (JsonReaderException) { throw new OperationException(400, "invalid_sonobuoy_arguments"); }
        if (args.Properties().Any(p => p.Name is not ("latitude" or "longitude" or "ammunition")) ||
            args["ammunition"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args["ammunition"]))
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        var position = new AdvisorMapCoordinate(Coordinate(args["latitude"], 90), Coordinate(args["longitude"], 180));
        var unit = context.Game.GetUnits().FirstOrDefault(u => u.Target == order.Target)
            ?? throw new OperationException(409, "target_unavailable");
        if (Unavailable(unit) is string reason) throw new OperationException(409, reason);
        var ammo = Sonobuoys(unit.Target).FirstOrDefault(a => a.Id == (string)args["ammunition"]!)
            ?? throw new OperationException(409, "sonobuoy_not_carried");
        if (ammo.Available < 1) throw new OperationException(409, "no_available_sonobuoy");
        if (!ammo.SingleRoundSupported) throw new OperationException(409, "single_drop_unavailable");
        return (unit, ammo, position);
    }
    private static double Coordinate(JToken? value, double maximum)
    {
        if (value == null || value.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        var number = (double)value;
        if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > maximum)
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        return number;
    }
    public void Validate(IReadOnlyList<OperationOrder> orders)
    {
        var drops = orders.Select(Read).ToArray();
        foreach (var group in drops.GroupBy(d => (d.Unit.Target, d.Ammo.Id)))
            if (group.First().Ammo.Available < group.Count()) throw new OperationException(409, "no_available_sonobuoy");
    }
    public string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language)
    {
        var drops = orders.Select(Read).ToArray();
        return string.Join("\n\n", drops.GroupBy(d => d.Unit.Target).Select(group =>
            context.Text.Format(language, "proposal", group.First().Unit.Name, group.Count()) + "\n" +
            string.Join("\n", group.Select((drop, i) => (i + 1) + ". " + drop.Ammo.Name + " - " +
                drop.Position.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                drop.Position.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)))));
    }
    public AdvisorMapPreview Preview(IReadOnlyList<OperationOrder> orders, string language)
    {
        var drawings = new List<AdvisorMapDrawing>();
        foreach (var group in orders.Select(Read).GroupBy(d => d.Unit.Target))
        {
            var drops = group.ToArray();
            var route = new List<AdvisorMapCoordinate>();
            if (context.Game.IsInformationEnabled("battle_movement_plans"))
            {
                var existing = context.Game.GetReservationRoute(group.Key);
                if (existing != null) route.Add(existing.EndPosition ?? existing.CurrentPosition);
            }
            route.AddRange(drops.Select(d => d.Position));
            if (route.Count > 1) drawings.Add(AdvisorMapDrawing.Line(route));
            for (var i = 0; i < drops.Length; i++)
                drawings.Add(AdvisorMapDrawing.Point(drops[i].Position, drops[i].Unit.Name + " " + (i + 1)));
        }
        return new AdvisorMapPreview(drawings);
    }
    public string Execute(OperationOrder order)
    {
        var drop = Read(order);
        var depth = context.Game.GetSonobuoyDepth(drop.Unit.Target);
        context.Game.AppendSonobuoyDrop(drop.Unit.Target, drop.Ammo.Id, drop.Position);
        return new JObject
        {
            ["target_ref"] = drop.Unit.Target, ["ammunition"] = drop.Ammo.Id, ["count"] = 1,
            ["latitude"] = drop.Position.Latitude, ["longitude"] = drop.Position.Longitude,
            ["placement"] = "append", ["depth"] = depth
        }.ToString(Formatting.None);
    }
    public void Dispose() { }
}
