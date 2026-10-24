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
    internal const int MaximumDrops = 40;
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_sonobuoy_drop");
    public OperationDefinition GetDefinition() => new(OperationId, "Schedule sonobuoy drops.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray("placement", "drops"),
        ["properties"] = new JObject
        {
            ["placement"] = new JObject { ["type"] = "string", ["enum"] = new JArray("append", "replace"),
                ["description"] = "Append to or replace this unit's entire existing reservation queue." },
            ["drops"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumDrops,
                ["description"] = "Ordered drop positions; at most 40 drops across this command in one proposal.",
                ["items"] = new JObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JArray("latitude", "longitude", "ammunition"),
                    ["properties"] = new JObject
                    {
                        ["latitude"] = new JObject { ["type"] = "number", ["minimum"] = -90, ["maximum"] = 90 },
                        ["longitude"] = new JObject { ["type"] = "number", ["minimum"] = -180, ["maximum"] = 180 },
                        ["ammunition"] = new JObject { ["type"] = "string" }
                    }
                }
            }
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
        .Where(u => u.Ammunition.Count != 0).ToArray();
    public void Dispose() { }

    private sealed class Unit
    {
        internal GameUnitInfo Info { get; }
        private readonly IAdvisorGameSupport game;
        internal IReadOnlyList<AmmunitionInfo> Ammunition { get; }
        internal Unit(GameUnitInfo info, IAdvisorGameSupport game)
        { Info = info; this.game = game; Ammunition = game.GetAmmunition(info.Target).Where(a => a.Type == "Bomb" && a.Subtype == "Sonobuoy").ToArray(); }
        internal string? UnavailableReason => Info.OrderUnavailableReason == "target_unavailable" ? "target_unavailable"
            : Info.IsAircraft && !Info.IsAirborne ? "unit_not_airborne" : Info.OrderUnavailableReason;
        internal string Depth => game.GetSonobuoyDepth(Info.Target);
        internal ReservationRoute? ReservationRoute => game.IsInformationEnabled("battle_movement_plans") ? game.GetReservationRoute(Info.Target) : null;
    }
    private sealed class Drop
    {
        internal string Ammunition { get; }
        internal AdvisorMapCoordinate Position { get; }
        internal Drop(string ammunition, AdvisorMapCoordinate position) { Ammunition = ammunition; Position = position; }
    }
    private sealed class Plan
    {
        internal bool Replace { get; }
        internal IReadOnlyList<Drop> Drops { get; }
        internal string Placement => Replace ? "replace" : "append";
        internal Plan(bool replace, IReadOnlyList<Drop> drops) { Replace = replace; Drops = drops; }
    }
    private static JObject Position(AdvisorMapCoordinate? position) => position is AdvisorMapCoordinate value
        ? new JObject { ["latitude_deg"] = value.Latitude, ["longitude_deg"] = value.Longitude } : new JObject();
    public string GetCandidates()
    {
        CheckInformation();
        return new JArray(Units().Select(unit =>
        {
            var ammunition = unit.Ammunition;
            var reason = unit.UnavailableReason ?? (ammunition.Any(a => a.AvailableAfterClearingReservations > 0 && a.SingleRoundSupported) ? null : "no_available_sonobuoy");
            return new JObject
            {
                ["target_ref"] = unit.Info.Target, ["display_name"] = unit.Info.Name,
                ["available"] = reason == null, ["unavailable_reason"] = reason, ["depth"] = unit.Depth,
                ["position"] = Position(unit.Info.Position),
                ["reservation_end_position"] = Position(unit.ReservationRoute?.EndPosition),
                ["ammunition"] = new JArray(ammunition.Select(a => new JObject { ["id"] = a.Id, ["display_name"] = a.Name,
                    ["remaining_count"] = a.Remaining, ["available_count"] = a.Available,
                    ["available_after_replacement_count"] = a.AvailableAfterClearingReservations,
                    ["single_drop_supported"] = a.SingleRoundSupported }))
            };
        })).ToString(Newtonsoft.Json.Formatting.None);
    }
    public OperationArgumentNormalization NormalizeArguments(OperationOrder order, IAdvisorOperationReferences references)
    {
        CheckInformation();
        var arguments = JObject.Parse(order.ArgumentsJson);
        if (arguments["drops"] is not JArray drops) return new OperationArgumentNormalization(order.ArgumentsJson);
        var unit = Units().FirstOrDefault(u => u.Info.Target == order.Target);
        if (unit == null) return new OperationArgumentNormalization(order.ArgumentsJson);
        var resolutions = new List<OperationArgumentResolution>();
        for (var i = 0; i < drops.Count; i++)
        {
            if (drops[i] is not JObject drop || drop["ammunition"]?.Type != JTokenType.String) continue;
            var requested = (string)drop["ammunition"]!;
            var match = references.MatchAmmunition(requested, unit.Ammunition.Select(a => new AmmunitionIdentity(a.Id, a.Name)).ToArray());
            if (match.Ids.Count > 1) throw new OperationException(409, "ambiguous_ammunition_reference");
            if (match.Ids.Count != 1 || match.Ids[0] == requested) continue;
            drop["ammunition"] = match.Ids[0];
            resolutions.Add(new OperationArgumentResolution("drops[" + i.ToString(CultureInfo.InvariantCulture) + "].ammunition", requested, match.Ids[0], match.Reason));
        }
        return new OperationArgumentNormalization(arguments.ToString(Newtonsoft.Json.Formatting.None), resolutions);
    }
    public void Validate(IReadOnlyList<OperationOrder> orders)
    {
        var count = 0;
        foreach (var order in orders) count += Read(order).Plan.Drops.Count;
        if (orders.GroupBy(o => o.Target).Any(g => g.Count() != 1)) throw new OperationException(400, "duplicate_unit_plan");
        if (count > SonobuoyDropExtension.MaximumDrops) throw new OperationException(400, "too_many_sonobuoy_drops");
    }
    public string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language)
    {
        var text = context.Text; var current = Units();
        return string.Join("\n\n", orders.Select(order =>
        {
            var plan = Parse(JObject.Parse(order.ArgumentsJson));
            var unit = current.FirstOrDefault(u => u.Info.Target == order.Target);
            var summary = text.Format(language, "proposal_summary", unit?.Info.Name ?? order.Target, plan.Drops.Count) + "\n" +
                text.Get(language, plan.Replace ? "proposal_replace" : "proposal_append") + "\n" + text.Get(language, "proposal_depth");
            return summary + "\n\n" + string.Join("\n", plan.Drops.Select((drop, i) =>
            {
                var ammo = unit?.Ammunition.FirstOrDefault(a => a.Id == drop.Ammunition);
                return (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + (ammo?.Name ?? drop.Ammunition) +
                    " — " + drop.Position.Latitude.ToString("F5", CultureInfo.InvariantCulture) + ", " +
                    drop.Position.Longitude.ToString("F5", CultureInfo.InvariantCulture);
            }));
        }));
    }
    public AdvisorMapPreview Preview(IReadOnlyList<OperationOrder> orders, string language)
    {
        var current = Units(); var drawings = new List<AdvisorMapDrawing>();
        foreach (var order in orders)
        {
            var plan = Parse(JObject.Parse(order.ArgumentsJson));
            var unit = current.FirstOrDefault(u => u.Info.Target == order.Target);
            var existing = unit?.ReservationRoute;
            var start = plan.Replace ? unit?.Info.Position ?? existing?.CurrentPosition : existing?.EndPosition ?? existing?.CurrentPosition;
            var route = new List<AdvisorMapCoordinate>();
            if (start is AdvisorMapCoordinate origin) route.Add(origin);
            route.AddRange(plan.Drops.Select(d => d.Position));
            // Draw beneath the numbered markers; preserve the proposed order separately for each unit.
            if (route.Count > 1) drawings.Add(AdvisorMapDrawing.Line(route));
            for (var i = 0; i < plan.Drops.Count; i++)
                drawings.Add(AdvisorMapDrawing.Point(plan.Drops[i].Position, (unit?.Info.Name ?? order.Target) + " · " + (i + 1).ToString(CultureInfo.InvariantCulture)));
        }
        return new AdvisorMapPreview(drawings);
    }
    private (Unit Unit, Plan Plan) Read(OperationOrder order)
    {
        CheckInformation(); var plan = Parse(JObject.Parse(order.ArgumentsJson));
        var unit = Units().FirstOrDefault(u => u.Info.Target == order.Target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.UnavailableReason is string reason) throw new OperationException(409, reason);
        foreach (var group in plan.Drops.GroupBy(d => d.Ammunition))
        {
            var ammo = unit.Ammunition.FirstOrDefault(a => a.Id == group.Key) ?? throw new OperationException(409, "sonobuoy_not_carried");
            if ((plan.Replace ? ammo.AvailableAfterClearingReservations : ammo.Available) < group.Count())
                throw new OperationException(409, "no_available_sonobuoy");
            if (!ammo.SingleRoundSupported) throw new OperationException(409, "single_drop_unavailable");
        }
        return (unit, plan);
    }
    public string Execute(OperationOrder order)
    {
        var data = Read(order); var depth = data.Unit.Depth; var changed = false;
        try
        {
            if (data.Plan.Replace)
            {
                var reservations = context.Game as IAdvisorReservationSupport
                    ?? throw new InvalidOperationException("The game adapter does not provide reservation control.");
                reservations.ClearReservations(order.Target); changed = true;
            }
            foreach (var drop in data.Plan.Drops)
            { context.Game.AppendSonobuoyDrop(order.Target, drop.Ammunition, drop.Position); changed = true; }
        }
        catch (OperationException error) when (changed)
        {
            // A rejected later step cannot mean the unit's entire plan left the game unchanged.
            throw new InvalidOperationException("Sonobuoy plan execution stopped after reservation changes; the outcome is incomplete.", error);
        }
        return new JObject { ["target_ref"] = order.Target, ["count"] = data.Plan.Drops.Count,
            ["placement"] = data.Plan.Placement, ["depth"] = depth,
            ["drops"] = new JArray(data.Plan.Drops.Select(d => new JObject { ["ammunition"] = d.Ammunition,
                ["latitude"] = d.Position.Latitude, ["longitude"] = d.Position.Longitude })) }.ToString(Newtonsoft.Json.Formatting.None);
    }
    private static Plan Parse(JObject arguments)
    {
        if (arguments.Properties().Any(p => p.Name is not ("placement" or "drops" or "depth")) ||
            arguments["placement"]?.Type != JTokenType.String || (string?)arguments["placement"] is not ("append" or "replace") ||
            arguments["drops"] is not JArray drops || drops.Count < 1 || drops.Count > SonobuoyDropExtension.MaximumDrops)
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        return new Plan((string)arguments["placement"]! == "replace", ((JArray)arguments["drops"]!).Select(value =>
        {
            if (value is not JObject drop || drop.Count != 3 || drop.Properties().Any(p => p.Name is not ("latitude" or "longitude" or "ammunition")) ||
                drop["ammunition"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)drop["ammunition"]))
                throw new OperationException(400, "invalid_sonobuoy_arguments");
            return new Drop((string)drop["ammunition"]!, new AdvisorMapCoordinate(Coordinate(drop["latitude"], 90), Coordinate(drop["longitude"], 180)));
        }).ToArray());
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
