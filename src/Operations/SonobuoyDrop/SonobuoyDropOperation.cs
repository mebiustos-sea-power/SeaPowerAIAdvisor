using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class SonobuoyDropExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorReservationExtension, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.schedule_sonobuoy_drop";
    internal const int MaximumDrops = 40;
    public bool EndsReservationSequence => false;
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_sonobuoy_drop");
    public OperationDefinition GetDefinition() => new(OperationId, "Schedule sonobuoy drops.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray("drops"),
        ["properties"] = new JObject
        {
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
internal sealed class SonobuoyDropOperation : IAdvisorOperationHandler,
    SeaPowerAIAdvisor.Api.IAdvisorOperationArgumentNormalizer, IAdvisorReservationOperation
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
        internal IReadOnlyList<Drop> Drops { get; }
        internal Plan(IReadOnlyList<Drop> drops) => Drops = drops;
    }
    private static JObject Position(AdvisorMapCoordinate? position) => position is AdvisorMapCoordinate value
        ? new JObject { ["latitude_deg"] = value.Latitude, ["longitude_deg"] = value.Longitude } : new JObject();
    public string GetCandidates()
    {
        CheckInformation();
        return new JArray(Units().Select(unit =>
        {
            var ammunition = unit.Ammunition;
            var reason = unit.UnavailableReason;
            ReservationQueueState? queue = null;
            if (reason is null or "following_formation" && context.Game is IAdvisorReservationSequenceSupport sequence)
            {
                queue = sequence.InspectReservations(unit.Info.Target, ReservationPlacement.Append);
                // Explicit replacement can still make a queue eligible; advertise both choices.
                var replacement = sequence.InspectReservations(unit.Info.Target, ReservationPlacement.Replace);
                reason = queue.UnavailableReason == null || replacement.UnavailableReason == null ? null : queue.UnavailableReason;
            }
            reason ??= ammunition.Any(a => a.AvailableAfterClearingReservations > 0 && a.SingleRoundSupported) ? null : "no_available_sonobuoy";
            return new JObject
            {
                ["target_ref"] = unit.Info.Target, ["display_name"] = unit.Info.Name,
                ["available"] = reason == null, ["unavailable_reason"] = reason, ["depth"] = unit.Depth,
                ["position"] = Position(unit.Info.Position),
                ["reservation_end_position"] = Position(queue?.BecomesIndependent == true ? null : unit.ReservationRoute?.EndPosition),
                ["requires_independent_action"] = queue?.BecomesIndependent == true,
                ["append_unavailable_reason"] = queue?.UnavailableReason,
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
    private Unit ReservationUnit(ReservationOrder order)
    {
        CheckInformation();
        var unit = Units().FirstOrDefault(u => u.Info.Target == order.Order.Target) ?? throw new OperationException(409, "target_unavailable");
        var reason = unit.UnavailableReason;
        if (reason == "following_formation" && order.Queue.BecomesIndependent) reason = null;
        if (reason != null) throw new OperationException(409, reason);
        return unit;
    }
    public void ValidateReservations(IReadOnlyList<ReservationOrder> orders)
    {
        var items = orders.Select(order => (Order: order, Unit: ReservationUnit(order), Plan: Parse(JObject.Parse(order.Order.ArgumentsJson)))).ToArray();
        if (items.Sum(i => i.Plan.Drops.Count) > SonobuoyDropExtension.MaximumDrops) throw new OperationException(400, "too_many_sonobuoy_drops");
        foreach (var group in items.GroupBy(i => i.Order.Order.Target))
        {
            var first = group.First();
            foreach (var demand in group.SelectMany(i => i.Plan.Drops).GroupBy(d => d.Ammunition))
            {
                var ammo = first.Unit.Ammunition.FirstOrDefault(a => a.Id == demand.Key) ?? throw new OperationException(409, "sonobuoy_not_carried");
                if (!ammo.SingleRoundSupported) throw new OperationException(409, "single_drop_unavailable");
                if ((first.Order.Placement == ReservationPlacement.Replace ? ammo.AvailableAfterClearingReservations : ammo.Available) < demand.Count())
                    throw new OperationException(409, "no_available_sonobuoy");
            }
        }
    }
    public string DescribeReservation(ReservationOrder order, string language)
    {
        var plan = Parse(JObject.Parse(order.Order.ArgumentsJson)); var unit = ReservationUnit(order); var text = context.Text;
        var summary = text.Format(language, "proposal_summary", unit.Info.Name, plan.Drops.Count);
        if (order.Index == 0 && order.Queue.BecomesIndependent) summary += "\n" + text.Get(language, "proposal_independent");
        return summary + "\n" + text.Get(language, "proposal_depth") + "\n\n" + string.Join("\n", plan.Drops.Select((drop, i) =>
            (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + (unit.Ammunition.FirstOrDefault(a => a.Id == drop.Ammunition)?.Name ?? drop.Ammunition) +
            " — " + drop.Position.Latitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + drop.Position.Longitude.ToString("F5", CultureInfo.InvariantCulture)));
    }
    public ReservationPreview PreviewReservation(ReservationOrder order, string language)
    {
        var plan = Parse(JObject.Parse(order.Order.ArgumentsJson)); var unit = ReservationUnit(order); var drawings = new List<AdvisorMapDrawing>();
        var route = new List<AdvisorMapCoordinate>(); if (order.StartPosition is AdvisorMapCoordinate start) route.Add(start);
        route.AddRange(plan.Drops.Select(d => d.Position));
        if (route.Count > 1) drawings.Add(AdvisorMapDrawing.Line(route));
        for (var i = 0; i < plan.Drops.Count; i++) drawings.Add(AdvisorMapDrawing.Point(plan.Drops[i].Position,
            unit.Info.Name + " / " + (order.Index + 1).ToString(CultureInfo.InvariantCulture) + "." + (i + 1).ToString(CultureInfo.InvariantCulture)));
        return new ReservationPreview(new AdvisorMapPreview(drawings), plan.Drops.Last().Position);
    }
    public string ExecuteReservation(ReservationOrder order)
    {
        var plan = Parse(JObject.Parse(order.Order.ArgumentsJson)); var unit = ReservationUnit(order); var changed = false;
        // The shared coordinator already handled replacement. Native append checks the currently available inventory again.
        try
        {
            foreach (var drop in plan.Drops)
            { context.Game.AppendSonobuoyDrop(order.Order.Target, drop.Ammunition, drop.Position); changed = true; }
        }
        catch (OperationException error) when (changed)
        { throw new InvalidOperationException("Sonobuoy segment stopped after reservation changes.", error); }
        return new JObject { ["target_ref"] = order.Order.Target, ["count"] = plan.Drops.Count, ["depth"] = unit.Depth,
            ["drops"] = new JArray(plan.Drops.Select(d => new JObject { ["ammunition"] = d.Ammunition,
                ["latitude"] = d.Position.Latitude, ["longitude"] = d.Position.Longitude })) }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static Plan Parse(JObject arguments)
    {
        if (arguments.Properties().Any(p => p.Name is not ("drops" or "depth")) ||
            arguments["drops"] is not JArray drops || drops.Count < 1 || drops.Count > SonobuoyDropExtension.MaximumDrops)
            throw new OperationException(400, "invalid_sonobuoy_arguments");
        return new Plan(((JArray)arguments["drops"]!).Select(value =>
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
