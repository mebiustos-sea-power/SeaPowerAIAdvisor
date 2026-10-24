using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class SonobuoyDropExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions
{
    internal const string OperationId = "seapower.schedule_sonobuoy_drop";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_sonobuoy_drop");
    public OperationDefinition GetDefinition() => new(OperationId, "Schedule sonobuoy drops.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray("latitude", "longitude", "ammunition"),
        ["properties"] = new JObject
        {
            ["latitude"] = new JObject { ["type"] = "number", ["minimum"] = -90, ["maximum"] = 90 },
            ["longitude"] = new JObject { ["type"] = "number", ["minimum"] = -180, ["maximum"] = 180 },
            ["ammunition"] = new JObject { ["type"] = "string" },
            ["count"] = new JObject { ["type"] = "integer", ["enum"] = new JArray(1), ["default"] = 1 },
            ["placement"] = new JObject { ["type"] = "string", ["enum"] = new JArray("append"), ["default"] = "append" }
        }
    }.ToString(Newtonsoft.Json.Formatting.None),
        AdvisorText.ReadResources(typeof(SonobuoyDropExtension).Assembly, "SeaPowerAIAdvisor.Commands.SonobuoyDrop.Localization.", "en", "ja"),
        requiresApproval: true, new OperationPlanPolicy(2, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new SonobuoyDropOperation(context);
}

// Policy uses only the public SDK. Native reservation registration is supplied by the game support service.
internal sealed class SonobuoyDropOperation : IAdvisorOperationHandler, SeaPowerAIAdvisor.Api.IAdvisorOperationPreview,
    SeaPowerAIAdvisor.Api.IAdvisorOperationArgumentNormalizer
{
    private readonly IAdvisorOperationContext context;
    internal SonobuoyDropOperation(IAdvisorOperationContext context) => this.context = context;
    private void CheckInformation()
    {
        if (!context.Game.IsInformationEnabled("battle_units") || !context.Game.IsInformationEnabled("battle_weapons"))
            throw new OperationException(403, "information_disabled");
    }
    private IReadOnlyList<Unit> Units() => context.Game.GetUnits().Select(u => new Unit(u, context.Game))
        .Where(u => u.Ammunition().Count != 0).ToArray();
    private static JObject Arguments(OperationOrder order) => JObject.Parse(order.ArgumentsJson);
    public void Dispose() { }

    private sealed class Unit
    {
        internal GameUnitInfo Info { get; }
        private readonly IAdvisorGameSupport game;
        private readonly IReadOnlyList<AmmunitionInfo> ammunition;
        internal Unit(GameUnitInfo info, IAdvisorGameSupport game)
        { Info = info; this.game = game; ammunition = game.GetAmmunition(info.Target).Where(a => a.Type == "Bomb" && a.Subtype == "Sonobuoy").ToArray(); }
        internal int Id => Info.Id;
        internal string Name => Info.Name;
        internal string? UnavailableReason => Info.OrderUnavailableReason == "target_unavailable" ? "target_unavailable"
            : Info.IsAircraft && !Info.IsAirborne ? "unit_not_airborne" : Info.OrderUnavailableReason;
        internal string Depth => game.GetSonobuoyDepth(Info.Target);
        internal JObject Position => Info.Position is AdvisorMapCoordinate position
            ? new JObject { ["latitude_deg"] = position.Latitude, ["longitude_deg"] = position.Longitude } : new JObject();
        internal ReservationRoute? ReservationRoute => game.IsInformationEnabled("battle_movement_plans") ? game.GetReservationRoute(Info.Target) : null;
        internal IReadOnlyList<AmmunitionInfo> Ammunition() => ammunition;
        internal void Append(string ammunition, double latitude, double longitude) =>
            game.AppendSonobuoyDrop(Info.Target, ammunition, new AdvisorMapCoordinate(latitude, longitude));
    }
    public string GetCandidates()
    {
        CheckInformation();
        return new JArray(Units().Select(unit =>
        {
            var ammunition = unit.Ammunition();
            var reason = unit.UnavailableReason ?? (ammunition.Any(a => a.Available > 0 && a.SingleRoundSupported) ? null : "no_available_sonobuoy");
            return new JObject
            {
                ["target_ref"] = "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture), ["display_name"] = unit.Name,
                ["available"] = reason == null, ["unavailable_reason"] = reason, ["depth"] = unit.Depth, ["position"] = unit.Position.DeepClone(),
                ["ammunition"] = new JArray(ammunition.Select(a => new JObject { ["id"] = a.Id, ["display_name"] = a.Name,
                    ["remaining_count"] = a.Remaining, ["available_count"] = a.Available, ["single_drop_supported"] = a.SingleRoundSupported }))
            };
        })).ToString(Newtonsoft.Json.Formatting.None);
    }

    public OperationArgumentNormalization NormalizeArguments(OperationOrder order, IAdvisorOperationReferences references)
    {
        CheckInformation();
        var arguments = Arguments(order);
        if (arguments["ammunition"]?.Type != JTokenType.String) return new OperationArgumentNormalization(order.ArgumentsJson);
        var requested = (string)arguments["ammunition"]!;
        var unit = Units().FirstOrDefault(u => u.Info.Target == order.Target);
        if (unit == null) return new OperationArgumentNormalization(order.ArgumentsJson);
        var match = references.MatchAmmunition(requested, unit.Ammunition().Select(a => new AmmunitionIdentity(a.Id, a.Name)).ToArray());
        if (match.Ids.Count > 1) throw new OperationException(409, "ambiguous_ammunition_reference");
        var resolutions = new List<OperationArgumentResolution>();
        if (match.Ids.Count == 1 && match.Ids[0] != requested)
        {
            arguments["ammunition"] = match.Ids[0];
            resolutions.Add(new OperationArgumentResolution("ammunition", requested, match.Ids[0], match.Reason));
        }
        return new OperationArgumentNormalization(arguments.ToString(Newtonsoft.Json.Formatting.None), resolutions);
    }
    public void Validate(IReadOnlyList<OperationOrder> orders)
    {
        foreach (var order in orders) _ = Read(order.Target, Arguments(order));
        foreach (var group in orders.GroupBy(o => (o.Target, Ammo: (string)Arguments(o)["ammunition"]!)))
        {
            var data = Read(group.Key.Target, Arguments(group.First()));
            if (data.Unit.Ammunition().Single(a => a.Id == group.Key.Ammo).Available < group.Count())
                throw new OperationException(409, "no_available_sonobuoy");
        }
    }
    public string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language)
    {
        var text = context.Text;
        var current = Units();
        return string.Join("\n\n", orders.GroupBy(o => o.Target).Select(group =>
        {
            var unit = current.FirstOrDefault(u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture) == group.Key);
            var summary = text.Format(language, "proposal_summary", unit?.Name ?? group.Key, group.Count()) + "\n" + text.Get(language, "proposal_append");
            return summary + "\n\n" + string.Join("\n", group.Select((order, i) =>
            {
                var args = Arguments(order);
                var ammo = unit?.Ammunition().FirstOrDefault(a => a.Id == (string?)args["ammunition"]);
                return (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + (ammo?.Name ?? (string)args["ammunition"]!) +
                    " — " + ((double)args["latitude"]!).ToString("F5", CultureInfo.InvariantCulture) + ", " +
                    ((double)args["longitude"]!).ToString("F5", CultureInfo.InvariantCulture);
            }));
        }));
    }
    public AdvisorMapPreview Preview(IReadOnlyList<OperationOrder> orders, string language)
    {
        var current = Units();
        var drawings = new List<AdvisorMapDrawing>();
        foreach (var group in orders.GroupBy(order => order.Target))
        {
            var unit = current.FirstOrDefault(unit => "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture) == group.Key);
            var drops = group.Select(order =>
            {
                var arguments = Arguments(order);
                return new AdvisorMapCoordinate((double)arguments["latitude"]!, (double)arguments["longitude"]!);
            }).ToArray();
            if (drops.Length == 0) continue;
            var route = new List<AdvisorMapCoordinate>();
            var existing = unit?.ReservationRoute;
            if (existing != null) route.Add(existing.EndPosition ?? existing.CurrentPosition);
            route.AddRange(drops);
            // Draw beneath the numbered markers; preserve the proposed order separately for each unit.
            if (route.Count > 1) drawings.Add(AdvisorMapDrawing.Line(route));
            for (var i = 0; i < drops.Length; i++)
                drawings.Add(AdvisorMapDrawing.Point(drops[i], (unit?.Name ?? group.Key) + " · " + (i + 1).ToString(CultureInfo.InvariantCulture)));
        }
        return new AdvisorMapPreview(drawings);
    }
    private (Unit Unit, string Ammunition, double Latitude, double Longitude) Read(string target, JObject arguments)
    {
        CheckInformation();
        if (arguments.Properties().Any(p => p.Name is not ("latitude" or "longitude" or "ammunition" or "count" or "placement" or "depth")) ||
            arguments["ammunition"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)arguments["ammunition"]) ||
            (arguments["count"] != null && (arguments["count"]!.Type != JTokenType.Integer || arguments["count"]!.ToString() != "1")) ||
            (arguments["placement"] != null && (arguments["placement"]!.Type != JTokenType.String || (string?)arguments["placement"] != "append")))
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        var latitude = Coordinate(arguments["latitude"], 90);
        var longitude = Coordinate(arguments["longitude"], 180);
        var unit = Units().FirstOrDefault(u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture) == target)
            ?? throw new OperationException(409, "target_unavailable");
        if (unit.UnavailableReason is string reason) throw new OperationException(409, reason);
        var ammunition = (string)arguments["ammunition"]!;
        var ammo = unit.Ammunition().FirstOrDefault(a => a.Id == ammunition)
            ?? throw new OperationException(409, "sonobuoy_not_carried");
        if (ammo.Available < 1) throw new OperationException(409, "no_available_sonobuoy");
        if (!ammo.SingleRoundSupported) throw new OperationException(409, "single_drop_unavailable");
        return (unit, ammunition, latitude, longitude);
    }
    public string Execute(OperationOrder order)
    {
        var target = order.Target;
        var data = Read(target, Arguments(order));
        // Ignore incoming depth choices; the player-controlled setting is read again at registration.
        var effectiveDepth = data.Unit.Depth;
        data.Unit.Append(data.Ammunition, data.Latitude, data.Longitude);
        return new JObject { ["target_ref"] = target, ["ammunition"] = data.Ammunition, ["count"] = 1,
            ["latitude"] = data.Latitude, ["longitude"] = data.Longitude, ["placement"] = "append", ["depth"] = effectiveDepth }.ToString(Newtonsoft.Json.Formatting.None);
    }
    private static double Coordinate(JToken? value, double limit)
    {
        if (value == null || value.Type is not (JTokenType.Integer or JTokenType.Float) ||
            !double.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            double.IsNaN(result) || double.IsInfinity(result) || Math.Abs(result) > limit)
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        return result;
    }
}
