using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IBattleSonobuoyUnit
{
    int Id { get; }
    string Name { get; }
    string? UnavailableReason { get; }
    string Depth { get; }
    JObject Position { get; }
    IReadOnlyList<BattleSonobuoyAmmo> Ammunition();
    void Append(string ammunition, double latitude, double longitude);
}

internal sealed class BattleSonobuoyAmmo
{
    internal string Id { get; }
    internal string Name { get; }
    internal int Remaining { get; }
    internal int Available { get; }
    internal bool SingleDrop { get; }
    internal BattleSonobuoyAmmo(string id, string name, int remaining, int available, bool singleDrop)
    { Id = id; Name = name; Remaining = remaining; Available = available; SingleDrop = singleDrop; }
}

// One native compound reservation handles both travel and release. It never waits for deployment.
internal sealed class BattleSonobuoyOperation : IAdvisorOperation, IAdvisorOperationPreview, IAdvisorOperationArgumentNormalizer
{
    internal const string OperationId = "seapower.schedule_sonobuoy_drop";
    private readonly Func<IReadOnlyList<IBattleSonobuoyUnit>> units;
    private readonly Func<AdvisorInformation> information;
    internal BattleSonobuoyOperation(Func<IReadOnlyList<IBattleSonobuoyUnit>> units, Func<AdvisorInformation>? information = null)
    { this.units = units; this.information = information ?? (() => AdvisorInformation.Default); }
    private void CheckInformation()
    {
        var allowed = information();
        if (!allowed.Allows(InformationKind.BattleUnits) || !allowed.Allows(InformationKind.BattleWeapons))
            throw new AdvisorOperationException(403, "information_disabled");
    }
    public bool RequiresApproval => true;
    public string Id => OperationId;
    public string Description => AdvisorPrompts.Current.Text("battle.json", "operation_sonobuoy_drop");
    public JObject Definition() => new()
    {
        ["id"] = Id, ["description"] = Description,
        ["parameters"] = new JObject
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
        }
    };
    public JArray Candidates()
    {
        CheckInformation();
        return new JArray(units().Select(unit =>
        {
            var ammunition = unit.Ammunition();
            var reason = unit.UnavailableReason ?? (ammunition.Any(a => a.Available > 0 && a.SingleDrop) ? null : "no_available_sonobuoy");
            return new JObject
            {
                ["target_ref"] = "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture), ["display_name"] = unit.Name,
                ["available"] = reason == null, ["unavailable_reason"] = reason, ["depth"] = unit.Depth, ["position"] = unit.Position.DeepClone(),
                ["ammunition"] = new JArray(ammunition.Select(a => new JObject { ["id"] = a.Id, ["display_name"] = a.Name,
                    ["remaining_count"] = a.Remaining, ["available_count"] = a.Available, ["single_drop_supported"] = a.SingleDrop }))
            };
        }));
    }
    public JObject NormalizeArguments(string target, JObject arguments, AdvisorOperationReferences references, JArray resolutions)
    {
        CheckInformation();
        if (arguments["ammunition"]?.Type != JTokenType.String) return arguments;
        var requested = (string)arguments["ammunition"]!;
        var unit = units().FirstOrDefault(u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture) == target);
        if (unit == null) return arguments;
        var matches = references.Weapons.Match(requested, unit.Ammunition().Select(a => (a.Id, a.Name)), out var reason);
        if (matches.Length > 1) throw new AdvisorOperationException(409, "ambiguous_ammunition_reference");
        if (matches.Length == 1 && matches[0] != requested)
        {
            arguments["ammunition"] = matches[0];
            resolutions.Add(new JObject { ["argument"] = "ammunition", ["requested"] = requested, ["resolved"] = matches[0], ["reason"] = reason });
        }
        return arguments;
    }
    public void Validate(IReadOnlyList<AdvisorOperationOrder> orders)
    {
        foreach (var order in orders) _ = Read(order.Target, order.Arguments);
        foreach (var group in orders.GroupBy(o => (o.Target, Ammo: (string)o.Arguments["ammunition"]!)))
        {
            var data = Read(group.Key.Target, group.First().Arguments);
            if (data.Unit.Ammunition().Single(a => a.Id == group.Key.Ammo).Available < group.Count())
                throw new AdvisorOperationException(409, "no_available_sonobuoy");
        }
    }
    public string DescribeProposal(IReadOnlyList<AdvisorOperationOrder> orders, string language)
    {
        var text = AdvisorText.Load(language);
        var current = units();
        return string.Join("\n\n", orders.GroupBy(o => o.Target).Select(group =>
        {
            var unit = current.FirstOrDefault(u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture) == group.Key);
            var summary = text.Format("operation.sonobuoy_summary", unit?.Name ?? group.Key, group.Count()) + "\n" + text["operation.sonobuoy_append"];
            return summary + "\n\n" + string.Join("\n", group.Select((order, i) =>
            {
                var args = order.Arguments;
                var ammo = unit?.Ammunition().FirstOrDefault(a => a.Id == (string?)args["ammunition"]);
                return (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + (ammo?.Name ?? (string)args["ammunition"]!) +
                    " — " + ((double)args["latitude"]!).ToString("F5", CultureInfo.InvariantCulture) + ", " +
                    ((double)args["longitude"]!).ToString("F5", CultureInfo.InvariantCulture);
            }));
        }));
    }
    public AdvisorMapPreview Preview(IReadOnlyList<AdvisorOperationOrder> orders, string language)
    {
        var current = units();
        var points = orders.GroupBy(order => order.Target).SelectMany(group =>
        {
            var unit = current.FirstOrDefault(unit => "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture) == group.Key);
            return group.Select((order, index) =>
            {
                var arguments = order.Arguments;
                return AdvisorMapDrawing.Point(new AdvisorMapCoordinate((double)arguments["latitude"]!, (double)arguments["longitude"]!),
                    (unit?.Name ?? group.Key) + " · " + (index + 1).ToString(CultureInfo.InvariantCulture));
            });
        }).ToArray();
        return new AdvisorMapPreview(points, points.Length == 0 ? null : points[0].Coordinates[0]);
    }
    private (IBattleSonobuoyUnit Unit, string Ammunition, double Latitude, double Longitude) Read(string target, JObject arguments)
    {
        CheckInformation();
        if (arguments.Properties().Any(p => p.Name is not ("latitude" or "longitude" or "ammunition" or "count" or "placement" or "depth")) ||
            arguments["ammunition"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)arguments["ammunition"]) ||
            (arguments["count"] != null && (arguments["count"]!.Type != JTokenType.Integer || arguments["count"]!.ToString() != "1")) ||
            (arguments["placement"] != null && (arguments["placement"]!.Type != JTokenType.String || (string?)arguments["placement"] != "append")))
            throw new AdvisorOperationException(400, "invalid_sonobuoy_arguments");
        var latitude = Coordinate(arguments["latitude"], 90);
        var longitude = Coordinate(arguments["longitude"], 180);
        var unit = units().FirstOrDefault(u => "unit:" + u.Id.ToString(CultureInfo.InvariantCulture) == target)
            ?? throw new AdvisorOperationException(409, "target_unavailable");
        if (unit.UnavailableReason is string reason) throw new AdvisorOperationException(409, reason);
        var ammunition = (string)arguments["ammunition"]!;
        var ammo = unit.Ammunition().FirstOrDefault(a => a.Id == ammunition)
            ?? throw new AdvisorOperationException(409, "sonobuoy_not_carried");
        if (ammo.Available < 1) throw new AdvisorOperationException(409, "no_available_sonobuoy");
        if (!ammo.SingleDrop) throw new AdvisorOperationException(409, "single_drop_unavailable");
        return (unit, ammunition, latitude, longitude);
    }
    public JObject Execute(string target, JObject arguments)
    {
        var data = Read(target, arguments);
        // Ignore incoming depth choices; the player-controlled setting is read again at registration.
        var effectiveDepth = data.Unit.Depth;
        data.Unit.Append(data.Ammunition, data.Latitude, data.Longitude);
        return new JObject { ["target_ref"] = target, ["ammunition"] = data.Ammunition, ["count"] = 1,
            ["latitude"] = data.Latitude, ["longitude"] = data.Longitude, ["placement"] = "append", ["depth"] = effectiveDepth };
    }
    private static double Coordinate(JToken? value, double limit)
    {
        if (value == null || value.Type is not (JTokenType.Integer or JTokenType.Float) ||
            !double.TryParse(value.ToString(Newtonsoft.Json.Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            double.IsNaN(result) || double.IsInfinity(result) || Math.Abs(result) > limit)
            throw new AdvisorOperationException(400, "invalid_sonobuoy_arguments");
        return result;
    }
}
