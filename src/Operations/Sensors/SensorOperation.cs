using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class SensorExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.set_sensors";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_sensors");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate, "Set individual sensor controls.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("changes"),
        ["properties"] = new JObject { ["changes"] = new JObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 40,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("control_id", "value"), ["properties"] = new JObject
                {
                    ["control_id"] = new JObject { ["type"] = "string", ["description"] = "An ID from this target's live controls." },
                    ["value"] = new JObject { ["type"] = "string", ["enum"] = new JArray("on", "off", "above_layer", "below_layer"),
                        ["description"] = "One of that control's options." }
                } }
        } }
    }.ToString(Formatting.None), AdvisorText.ReadResources(typeof(SensorExtension).Assembly,
        "SeaPowerAIAdvisor.Commands.Sensors.Localization.", "en", "ja"), true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new SensorOperation(context);
}

// Only common presentation and access for these two sensor commands; no native sensor logic belongs here.
internal abstract class SensorOperationBase : IAdvisorOperationHandler, IAdvisorOperationActions
{
    protected readonly IAdvisorOperationContext Context;
    protected IAdvisorSensorSupport Sensors => Context.Game as IAdvisorSensorSupport
        ?? throw new InvalidOperationException("The game adapter does not provide sensor support.");
    protected SensorOperationBase(IAdvisorOperationContext context) => Context = context;
    protected IReadOnlyList<SensorUnitInfo> Units()
    {
        if (!Context.Game.IsInformationEnabled("battle_units") || !Context.Game.IsInformationEnabled("battle_sensors"))
            throw new OperationException(403, "information_disabled");
        return Sensors.GetSensorUnits();
    }
    protected SensorUnitInfo Unit(string target)
    {
        var unit = Units().SingleOrDefault(u => u.Unit.Target == target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        return unit;
    }
    protected static JObject Identity(SensorUnitInfo unit) => new()
    {
        ["target_ref"] = unit.Unit.Target, ["display_name"] = unit.Unit.Name,
        ["available"] = unit.Unit.OrderUnavailableReason == null, ["unavailable_reason"] = unit.Unit.OrderUnavailableReason,
        ["emcon"] = unit.Emcon
    };
    public void Dispose() { }
    public abstract string GetCandidates();
    public abstract void Validate(IReadOnlyList<OperationOrder> orders);
    public abstract string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language);
    public abstract string Execute(OperationOrder order);
}

internal sealed class SensorOperation : SensorOperationBase
{
    internal SensorOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates() => new JArray(Units().Select(unit =>
    {
        var row = Identity(unit);
        row["controls"] = new JArray(unit.Controls.Select(c => new JObject
        {
            ["id"] = c.Id, ["display_name"] = c.Name, ["kind"] = c.Kind, ["setting"] = c.Setting,
            ["role"] = c.Role, ["function"] = c.Function, ["value"] = c.Value, ["options"] = new JArray(c.Options), ["available"] = c.UnavailableReason == null,
            ["unavailable_reason"] = c.UnavailableReason
        }));
        return row;
    })).ToString(Formatting.None);
    private static IReadOnlyList<SensorControlChange> Parse(OperationOrder order)
    {
        var args = JObject.Parse(order.ArgumentsJson);
        if (args.Count != 1 || args["changes"] is not JArray changes || changes.Count < 1 || changes.Count > 40)
            throw new OperationException(400, "invalid_sensor_arguments");
        var result = new List<SensorControlChange>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in changes)
        {
            if (item is not JObject change || change.Count != 2 || change["control_id"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace((string?)change["control_id"]) || change["value"]?.Type != JTokenType.String ||
                (string?)change["value"] is not ("on" or "off" or "above_layer" or "below_layer") || !seen.Add((string)change["control_id"]!))
                throw new OperationException(400, "invalid_sensor_arguments");
            result.Add(new SensorControlChange((string)change["control_id"]!, (string)change["value"]!));
        }
        return result;
    }
    private (SensorUnitInfo Unit, IReadOnlyList<SensorControlChange> Changes) Read(OperationOrder order)
    {
        var changes = Parse(order); var unit = Unit(order.Target);
        foreach (var change in changes)
        {
            var control = unit.Controls.SingleOrDefault(c => c.Id == change.Control) ?? throw new OperationException(409, "sensor_control_unavailable");
            if (control.UnavailableReason is string reason) throw new OperationException(409, reason);
            if (!control.Options.Contains(change.Value)) throw new OperationException(409, "sensor_setting_unavailable");
        }
        return (unit, changes);
    }
    public override void Validate(IReadOnlyList<OperationOrder> orders)
    { foreach (var order in orders) Read(order); }
    public override string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language) => string.Join("\n\n", orders.Select(order =>
    {
        var data = Read(order);
        return Context.Text.Format(language, "proposal_summary", data.Unit.Unit.Name) + "\n" + string.Join("\n", data.Changes.Select(change =>
        {
            var c = data.Unit.Controls.Single(v => v.Id == change.Control);
            return "- " + c.Name + " / " + Context.Text.Get(language, "setting_" + c.Setting) + ": " + Context.Text.Get(language, "value_" + change.Value);
        }));
    }));
    public override string Execute(OperationOrder order)
    {
        var data = Read(order); Sensors.SetSensorControls(order.Target, data.Changes);
        return new JObject { ["target_ref"] = order.Target, ["requested_changes"] = new JArray(data.Changes.Select(c =>
            new JObject { ["control_id"] = c.Control, ["value"] = c.Value })) }.ToString(Formatting.None);
    }
}
