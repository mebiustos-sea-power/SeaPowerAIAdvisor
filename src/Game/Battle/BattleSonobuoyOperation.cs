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
internal sealed class BattleSonobuoyOperation : IAdvisorOperation
{
    internal const string OperationId = "seapower.schedule_sonobuoy_drop";
    private readonly Func<IReadOnlyList<IBattleSonobuoyUnit>> units;
    internal BattleSonobuoyOperation(Func<IReadOnlyList<IBattleSonobuoyUnit>> units) => this.units = units;
    public string Id => OperationId;
    public JObject Definition() => new()
    {
        ["id"] = Id, ["description"] = AdvisorPrompts.Current.Text("battle.json", "operation_sonobuoy_drop"),
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
    public JArray Candidates() => new(units().Select(unit =>
    {
        var ammunition = unit.Ammunition();
        var reason = unit.UnavailableReason ?? (ammunition.Any(a => a.Available > 0 && a.SingleDrop) ? null : "no_available_sonobuoy");
        return new JObject
        {
            ["target_ref"] = "unit:" + unit.Id.ToString(CultureInfo.InvariantCulture), ["display_name"] = unit.Name,
            ["available"] = reason == null, ["unavailable_reason"] = reason, ["depth"] = unit.Depth,
            ["ammunition"] = new JArray(ammunition.Select(a => new JObject { ["id"] = a.Id, ["display_name"] = a.Name,
                ["remaining_count"] = a.Remaining, ["available_count"] = a.Available, ["single_drop_supported"] = a.SingleDrop }))
        };
    }));
    public JObject Execute(string target, JObject arguments)
    {
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
        // Ignore incoming depth choices; the player-controlled setting is read again at registration.
        var effectiveDepth = unit.Depth;
        unit.Append(ammunition, latitude, longitude);
        return new JObject { ["target_ref"] = target, ["ammunition"] = ammunition, ["count"] = 1,
            ["latitude"] = latitude, ["longitude"] = longitude, ["placement"] = "append", ["depth"] = effectiveDepth };
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
