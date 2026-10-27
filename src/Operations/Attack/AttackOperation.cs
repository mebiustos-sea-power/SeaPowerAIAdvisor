using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class AttackExtension : IAdvisorOperationExtension, IAdvisorOperationInstructions, IAdvisorExtensionVersion
{
    internal const string OperationId = "seapower.attack";
    public int ApiVersion => AdvisorApi.ContractVersion;
    public string Version => "1.0.0";
    public string GetInstructions() => AdvisorPrompts.Current.Text("battle.json", "operation_attack");
    public OperationDefinition GetDefinition() => new(OperationId, OperationExecutionMode.Immediate, "Issue attacks with selected ammunition and shot counts.", new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("mode", "attacks"),
        ["properties"] = new JObject
        {
            ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("append", "replace"),
                ["description"] = "append: keep attacks; replace: switch this unit's uncompleted attacks, keeping launched-weapon guidance and reservations. If this unit has further attack operations in the same plan, they must append." },
            ["attacks"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 40, ["items"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("target_ref", "ammunition", "rounds"),
                ["properties"] = new JObject
                {
                    ["target_ref"] = new JObject { ["type"] = "string", ["description"] = "The observed target's target_ref from attack candidates." },
                    ["ammunition"] = new JObject { ["type"] = "string", ["description"] = "A carried ammunition ID compatible with this target." },
                    ["rounds"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue }
                }
            } }
        }
    }.ToString(Formatting.None), AdvisorText.ReadResources(typeof(AttackExtension).Assembly,
        "SeaPowerAIAdvisor.Commands.Attack.Localization.", "en", "ja"), true, new OperationPlanPolicy(1, 1, 2, OperationPlanSelection.Single));
    public IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context) => new AttackOperation(context);
}

internal sealed class AttackOperation : CombatOperationBase, SeaPowerAIAdvisor.Api.IAdvisorOperationArgumentNormalizer
{
    internal AttackOperation(IAdvisorOperationContext context) : base(context) { }
    public override string GetCandidates()
    {
        CheckInformation(true);
        var targets = Combat.GetAttackTargets();
        return new JArray(Combat.GetAttackUnits().Select(unit =>
        {
            var row = Identity(unit.Unit);
            row["pending_attacks"] = new JArray(unit.PendingAttacks.Select(AttackJson));
            row["ammunition"] = new JArray(unit.Ammunition.Select(a => new JObject
            { ["id"] = a.Id, ["display_name"] = a.Name, ["remaining_count"] = a.Remaining, ["available_count"] = a.Available,
                ["available_after_switch_count"] = a.AvailableAfterSwitch, ["compatible_targets"] = new JArray(a.CompatibleTargets) }));
            row["targets"] = new JArray(targets.Where(t => unit.Ammunition.Any(a => a.CompatibleTargets.Contains(t.Target))).Select(t => new JObject
            { ["target_ref"] = t.Target, ["display_name"] = t.Name, ["kind"] = t.Kind, ["posture"] = t.Posture,
                ["position"] = t.Position is AdvisorMapCoordinate point ? new JObject { ["latitude"] = point.Latitude, ["longitude"] = point.Longitude } : null,
                ["dormant"] = t.Dormant, ["destroyed"] = t.Destroyed }));
            return row;
        })).ToString(Formatting.None);
    }

    private static JObject AttackJson(AttackInstruction a) => new()
    { ["target_ref"] = a.Target, ["ammunition"] = a.Ammunition, ["rounds"] = a.Rounds };
    private static (AttackUpdateMode Mode, IReadOnlyList<AttackInstruction> Attacks) Parse(OperationOrder order)
    {
        var args = JObject.Parse(order.ArgumentsJson);
        if (args.Count != 2 || (string?)args["mode"] is not ("append" or "replace") || args["attacks"] is not JArray attacks || attacks.Count is < 1 or > 40)
            throw new OperationException(400, "invalid_attack_arguments");
        var result = new List<AttackInstruction>();
        foreach (var item in attacks)
        {
            if (item is not JObject a || a.Count != 3 || a["target_ref"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)a["target_ref"]) ||
                a["ammunition"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)a["ammunition"]) ||
                a["rounds"]?.Type != JTokenType.Integer || !int.TryParse(a["rounds"]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rounds) || rounds < 1)
                throw new OperationException(400, "invalid_attack_arguments");
            result.Add(new AttackInstruction((string)a["target_ref"]!, (string)a["ammunition"]!, rounds));
        }
        return ((string?)args["mode"] == "replace" ? AttackUpdateMode.Replace : AttackUpdateMode.Append, result);
    }
    public OperationArgumentNormalization NormalizeArguments(OperationOrder order, SeaPowerAIAdvisor.Api.IAdvisorOperationReferences references)
    {
        CheckInformation(true);
        var args = JObject.Parse(order.ArgumentsJson); var resolutions = new List<OperationArgumentResolution>();
        if (args["attacks"] is not JArray attacks) return new OperationArgumentNormalization(order.ArgumentsJson);
        var unit = Combat.GetAttackUnits().SingleOrDefault(u => u.Unit.Target == order.Target);
        if (unit == null) return new OperationArgumentNormalization(order.ArgumentsJson);
        for (var i = 0; i < attacks.Count; i++)
        {
            if (attacks[i] is not JObject a) continue;
            var path = "attacks[" + i.ToString(CultureInfo.InvariantCulture) + "].";
            if (a["target_ref"]?.Type == JTokenType.String && references.ResolveUnit(((string)a["target_ref"]!).StartsWith("unit:", StringComparison.Ordinal)
                ? ((string)a["target_ref"]!).Substring(5) : (string)a["target_ref"]!) is string target && target != (string)a["target_ref"]!)
            { resolutions.Add(new OperationArgumentResolution(path + "target_ref", (string)a["target_ref"]!, target, "consultation_unit_reference")); a["target_ref"] = target; }
            if (a["ammunition"]?.Type != JTokenType.String) continue;
            var requested = (string)a["ammunition"]!;
            var match = references.MatchAmmunition(requested, unit.Ammunition.Select(ammo => new AmmunitionIdentity(ammo.Id, ammo.Name)).ToArray());
            if (match.Ids.Count > 1) throw new OperationException(409, "ambiguous_ammunition_reference");
            if (match.Ids.Count != 1 || match.Ids[0] == requested) continue;
            a["ammunition"] = match.Ids[0];
            resolutions.Add(new OperationArgumentResolution(path + "ammunition", requested, match.Ids[0], match.Reason));
        }
        return new OperationArgumentNormalization(args.ToString(Formatting.None), resolutions);
    }
    private static (AttackUnitInfo Unit, AttackUpdateMode Mode, IReadOnlyList<AttackInstruction> Attacks) Read(OperationOrder order,
        IReadOnlyList<AttackUnitInfo> units, IReadOnlyList<CombatTargetInfo> contacts)
    {
        var plan = Parse(order);
        var unit = units.SingleOrDefault(u => u.Unit.Target == order.Target) ?? throw new OperationException(409, "target_unavailable");
        if (unit.Unit.OrderUnavailableReason is string reason) throw new OperationException(409, reason);
        var targets = contacts.Where(t => !t.Destroyed).Select(t => t.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var a in plan.Attacks)
        {
            if (!targets.Contains(a.Target)) throw new OperationException(409, "attack_target_unavailable");
            var ammo = unit.Ammunition.SingleOrDefault(m => m.Id == a.Ammunition) ?? throw new OperationException(409, "ammunition_not_carried");
            if (!ammo.CompatibleTargets.Contains(a.Target)) throw new OperationException(409, "incompatible_attack_target");
        }
        return (unit, plan.Mode, plan.Attacks);
    }
    private (AttackUnitInfo Unit, AttackUpdateMode Mode, IReadOnlyList<AttackInstruction> Attacks)[] ReadOrders(IReadOnlyList<OperationOrder> orders)
    {
        CheckInformation(true); var units = Combat.GetAttackUnits(); var targets = Combat.GetAttackTargets();
        return orders.Select(order => Read(order, units, targets)).ToArray();
    }
    public override void Validate(IReadOnlyList<OperationOrder> orders) => ValidateAmmunition(ReadOrders(orders));
    private static void ValidateAmmunition(IEnumerable<(AttackUnitInfo Unit, AttackUpdateMode Mode, IReadOnlyList<AttackInstruction> Attacks)> plans)
    {
        // One initial switch may release old commitments; later attack segments must retain newly submitted work.
        foreach (var group in plans.GroupBy(p => p.Unit.Unit.Target))
        {
            var demands = new Dictionary<string, long>(StringComparer.Ordinal); var switched = false;
            foreach (var plan in group)
            {
                if (plan.Mode == AttackUpdateMode.Replace)
                {
                    if (demands.Count > 0) throw new OperationException(400, "attack_switch_after_new_attacks");
                    switched = true;
                }
                foreach (var a in plan.Attacks)
                {
                    demands.TryGetValue(a.Ammunition, out var n); demands[a.Ammunition] = n + a.Rounds;
                    var ammo = plan.Unit.Ammunition.Single(m => m.Id == a.Ammunition);
                    if (demands[a.Ammunition] > (switched ? ammo.AvailableAfterSwitch : ammo.Available))
                        throw new OperationException(409, "insufficient_attack_ammunition");
                }
            }
        }
    }
    public override string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language)
    {
        var plans = ReadOrders(orders); var targets = Combat.GetAttackTargets().ToDictionary(t => t.Target, StringComparer.Ordinal);
        return string.Join("\n\n", plans.Select(plan =>
            Context.Text.Format(language, "proposal_summary", plan.Unit.Unit.Name, Context.Text.Get(language, plan.Mode == AttackUpdateMode.Append ? "mode_append" : "mode_replace")) +
            "\n" + string.Join("\n", plan.Attacks.Select(a => "- " + Context.Text.Format(language, "attack_line", targets[a.Target].Name,
                plan.Unit.Ammunition.Single(m => m.Id == a.Ammunition).Name, a.Rounds))) + "\n\n" + Context.Text.Get(language, "execution_note")));
    }

    public override string Execute(OperationOrder order)
    {
        var plans = ReadOrders(new[] { order }); ValidateAmmunition(plans); var plan = plans[0]; Combat.IssueAttack(order.Target, plan.Mode, plan.Attacks);
        return new JObject { ["target_ref"] = order.Target, ["mode"] = plan.Mode == AttackUpdateMode.Append ? "append" : "replace",
            ["attack_orders_issued"] = new JArray(plan.Attacks.Select(AttackJson)) }.ToString(Formatting.None);
    }
}
