using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class MovementExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorReservationExtension, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.schedule_movement";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public bool EndsReservationSequence => false;
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_movement");
    public OperationDefinition GetDefinition() => new(OperationId, "Schedule an ordered movement route.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("waypoints"),
        ["properties"] = new JObject { ["waypoints"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 40,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("latitude", "longitude"), ["properties"] = new JObject
                {
                    ["latitude"] = new JObject { ["type"] = "number", ["minimum"] = -90, ["maximum"] = 90 },
                    ["longitude"] = new JObject { ["type"] = "number", ["minimum"] = -180, ["maximum"] = 180 },
                    ["speed"] = new JObject { ["type"] = "string", ["description"] = "An ID from the target's speed_options. Omit to preserve its speed command." },
                    ["altitude"] = new JObject { ["type"] = "string", ["description"] = "An ID from the target's altitude_options. Applies while travelling TO this point; omit to preserve target altitude." },
                    ["depth"] = new JObject { ["type"] = "string", ["description"] = "An ID from the target's depth_options. Applies while travelling TO this point; do not combine with altitude." }
                } } } }
    }.ToString(Formatting.None), AdvisorText.ReadResources(typeof(MovementExtension).Assembly,
        "SeaPowerAIAdvisor.Commands.Movement.Localization.", "en", "ja"), true, new OperationPlanPolicy(2, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new MovementOperation(context);
}

// Shared presentation and game support for movement-related reservation commands.
internal abstract class MovementOperationBase : IAdvisorOperationHandler, IAdvisorReservationOperation
{
    protected readonly IAdvisorOperationContext Context;
    protected IAdvisorMovementSupport Movement => Context.Game as IAdvisorMovementSupport
        ?? throw new InvalidOperationException("The game adapter does not provide movement support.");
    protected MovementOperationBase(IAdvisorOperationContext context) => Context = context;
    protected IReadOnlyList<MovementUnitInfo> Units()
    {
        if (!Context.Game.IsInformationEnabled("battle_units") || !Context.Game.IsInformationEnabled("battle_movement_plans"))
            throw new OperationException(403, "information_disabled");
        return Movement.GetMovementUnits();
    }
    protected MovementUnitInfo Unit(string target)
    {
        var unit = Units().FirstOrDefault(u => u.Unit.Target == target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        return unit;
    }
    protected static JObject Position(AdvisorMapCoordinate? position) => position is AdvisorMapCoordinate value
        ? new JObject { ["latitude_deg"] = value.Latitude, ["longitude_deg"] = value.Longitude } : new JObject();
    protected string FormationDescription(ReservationOrder order, string language) => order.Index == 0 && order.Queue.BecomesIndependent
        ? "\n" + Context.Text.Get(language, "proposal_independent") : "";
    public void Dispose() { }
    public abstract string GetCandidates();
    public abstract void ValidateReservations(IReadOnlyList<ReservationOrder> orders);
    public abstract string DescribeReservation(ReservationOrder order, string language);
    public abstract ReservationPreview PreviewReservation(ReservationOrder order, string language);
    public abstract string ExecuteReservation(ReservationOrder order);
}

internal sealed class MovementOperation : MovementOperationBase
{
    internal MovementOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates() => new JArray(Units().Select(unit => new JObject
    {
        ["target_ref"] = unit.Unit.Target, ["display_name"] = unit.Unit.Name, ["kind"] = unit.Kind,
        ["available"] = unit.Unit.OrderUnavailableReason == null, ["unavailable_reason"] = unit.Unit.OrderUnavailableReason,
        ["position"] = Position(unit.Unit.Position), ["formation_leader"] = unit.LeaderTarget,
        ["formation_leader_name"] = unit.LeaderName, ["is_formation_leader"] = unit.IsFormationLeader,
        ["following_formation"] = unit.IsFollowingFormation,
        ["speed_options"] = Presets(unit.Speeds), ["altitude_options"] = Presets(unit.Altitudes), ["depth_options"] = Presets(unit.Depths)
    })).ToString(Formatting.None);
    private static JArray Presets(IEnumerable<MovementPreset> presets) => new(presets.Select(p => new JObject
        { ["id"] = p.Id, ["name"] = p.Name, ["value"] = p.Value, ["unit"] = p.Unit }));
    private static IReadOnlyList<MovementWaypoint> Parse(OperationOrder order)
    {
        var args = JObject.Parse(order.ArgumentsJson);
        if (args.Count != 1 || args["waypoints"] is not JArray points || points.Count < 1 || points.Count > 40)
            throw new OperationException(400, "invalid_movement_arguments");
        return points.Select(value =>
        {
            if (value is not JObject point || point.Properties().Any(p => p.Name is not ("latitude" or "longitude" or "speed" or "altitude" or "depth")))
                throw new OperationException(400, "invalid_movement_arguments");
            string? Text(string field) => point[field] == null ? null : point[field]!.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)point[field])
                ? (string)point[field]! : throw new OperationException(400, "invalid_movement_arguments");
            double Number(string field, double min, double max)
            {
                if (point[field]?.Type is not (JTokenType.Integer or JTokenType.Float) || !double.TryParse(point[field]!.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max)
                    throw new OperationException(400, "invalid_movement_arguments");
                return number;
            }
            var depth = Text("depth"); var speed = Text("speed");
            var altitude = Text("altitude");
            if (altitude != null && depth != null) throw new OperationException(400, "invalid_movement_arguments");
            return new MovementWaypoint(new AdvisorMapCoordinate(Number("latitude", -90, 90), Number("longitude", -180, 180)), speed, altitude, depth);
        }).ToArray();
    }
    private (MovementUnitInfo Unit, IReadOnlyList<MovementWaypoint> Points) Read(ReservationOrder order)
    {
        var unit = Unit(order.Order.Target); var points = Parse(order.Order);
        foreach (var point in points)
            if (point.Speed != null && !unit.Speeds.Any(p => p.Id == point.Speed) || point.Altitude != null && !unit.Altitudes.Any(p => p.Id == point.Altitude) ||
                point.Depth != null && !unit.Depths.Any(p => p.Id == point.Depth)) throw new OperationException(400, "movement_setting_not_supported");
        return (unit, points);
    }
    public override void ValidateReservations(IReadOnlyList<ReservationOrder> orders)
    {
        if (orders.Sum(o => Read(o).Points.Count) > 40) throw new OperationException(400, "too_many_movement_waypoints");
    }
    public override string DescribeReservation(ReservationOrder order, string language)
    {
        var data = Read(order);
        return Context.Text.Format(language, "proposal_summary", data.Unit.Unit.Name, data.Points.Count) + FormationDescription(order, language) +
            (data.Unit.IsFormationLeader ? "\n" + Context.Text.Get(language, "proposal_followers") : "") + "\n\n" +
            string.Join("\n", data.Points.Select((p, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ". " +
                p.Position.Latitude.ToString("F5", CultureInfo.InvariantCulture) + ", " + p.Position.Longitude.ToString("F5", CultureInfo.InvariantCulture) +
                (p.Speed == null ? "" : " / " + Context.Text.Format(language, "setting_speed", Context.Text.Get(language, "speed_" + p.Speed))) +
                (p.Altitude == null ? "" : " / " + Context.Text.Format(language, "setting_altitude", data.Unit.Altitudes.Single(a => a.Id == p.Altitude).Value)) +
                (p.Depth == null ? "" : " / " + Context.Text.Format(language, "setting_depth", Context.Text.Get(language, "depth_" + p.Depth))))) + "\n" + Context.Text.Get(language, "proposal_settings");
    }
    public override ReservationPreview PreviewReservation(ReservationOrder order, string language)
    {
        var data = Read(order); var route = new List<AdvisorMapCoordinate>(); var drawings = new List<AdvisorMapDrawing>();
        if (order.StartPosition is AdvisorMapCoordinate start) route.Add(start);
        route.AddRange(data.Points.Select(p => p.Position));
        if (route.Count > 1) drawings.Add(AdvisorMapDrawing.Line(route));
        for (var i = 0; i < data.Points.Count; i++) drawings.Add(AdvisorMapDrawing.Point(data.Points[i].Position,
            data.Unit.Unit.Name + " / " + (order.Index + 1).ToString(CultureInfo.InvariantCulture) + "." + (i + 1).ToString(CultureInfo.InvariantCulture)));
        return new ReservationPreview(new AdvisorMapPreview(drawings), data.Points.Last().Position);
    }
    public override string ExecuteReservation(ReservationOrder order)
    {
        var data = Read(order); var changed = false;
        try { foreach (var point in data.Points) { Movement.AppendMovement(order.Order.Target, point); changed = true; } }
        catch (OperationException error) when (changed) { throw new InvalidOperationException("Movement stopped after reservation changes.", error); }
        return new JObject { ["target_ref"] = order.Order.Target, ["waypoint_count"] = data.Points.Count }.ToString(Formatting.None);
    }
}
