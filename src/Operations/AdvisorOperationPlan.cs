using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal enum AdvisorPlanSelection { Single, Multiple }

// Optional command policy; its counts apply to complete alternative plans, not to individual orders.
internal interface IAdvisorOperationPlans
{
    AdvisorOperationPlanPolicy PlanPolicy { get; }
}

internal sealed class AdvisorOperationPlanPolicy
{
    internal static readonly AdvisorOperationPlanPolicy Default = new(1, 1, 1, AdvisorPlanSelection.Single);
    internal int Target { get; }
    internal int Minimum { get; }
    internal int Maximum { get; }
    internal AdvisorPlanSelection Selection { get; }
    internal AdvisorOperationPlanPolicy(int target, int minimum, int maximum, AdvisorPlanSelection selection)
    {
        if (minimum < 1 || maximum < minimum || maximum > AdvisorOperationPlanBatch.MaximumPlans || !Enum.IsDefined(typeof(AdvisorPlanSelection), selection))
            throw new ArgumentOutOfRangeException(nameof(minimum), "Invalid operation plan policy.");
        if (target < minimum || target > maximum)
            throw new ArgumentOutOfRangeException(nameof(target), "Plan target must be within the accepted range.");
        Target = target; Minimum = minimum; Maximum = maximum; Selection = selection;
    }
    internal JObject Json() => new() { ["target"] = Target, ["minimum"] = Minimum, ["maximum"] = Maximum,
        ["selection"] = Selection == AdvisorPlanSelection.Single ? "single" : "multiple" };
}

internal sealed class AdvisorOperationPlan
{
    internal JArray ReservationUpdates { get; }
    internal string Id { get; }
    internal string Title { get; }
    internal string Summary { get; }
    internal IReadOnlyList<AdvisorOperationOrder> ImmediateOrders { get; }
    internal IReadOnlyList<AdvisorOperationOrder> ReservationOrders { get; }
    internal IReadOnlyList<AdvisorOperationOrder> Orders => ReservationOrders.Concat(ImmediateOrders).ToArray();
    internal AdvisorOperationPlan(string id, string title, string summary, IEnumerable<AdvisorOperationOrder> immediateOrders,
        IEnumerable<AdvisorOperationOrder> reservationOrders, JArray reservationUpdates)
    {
        Id = id; Title = title; Summary = summary;
        ImmediateOrders = Array.AsReadOnly(immediateOrders.ToArray());
        ReservationOrders = Array.AsReadOnly(reservationOrders.ToArray());
        ReservationUpdates = (JArray)reservationUpdates.DeepClone();
    }
}

// The wire contract is shared by tool publication and request validation. Command arguments stay opaque here.
internal static class AdvisorOperationPlanBatch
{
    internal const int MaximumPlans = 8;
    internal const int MaximumOrders = 40;
    internal static readonly string[] OperationFields = { "reservation_operations", "immediate_operations" };
    internal static JObject Schema(JObject target, string requestDescription) => Object(new JObject
    {
        ["request_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 40,
            ["pattern"] = "^[A-Za-z0-9._-]+$", ["description"] = requestDescription },
        ["plans"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumPlans, ["items"] = Object(new JObject
        {
            ["plan_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 40, ["pattern"] = "^[A-Za-z0-9._-]+$" },
            ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 },
            ["summary"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1200, ["description"] = "Purpose and main trade-off." },
            ["reservation_operations"] = Orders(target, "Reservation-mode commands register game reservations on acceptance, before all immediate operations. Preserve their intended queue order; use [] if unused. Both operation arrays together must contain 1 to 40 operations."),
            ["immediate_operations"] = Orders(target, "Immediate-mode commands execute on acceptance after all reservations are registered, without waiting for reserved actions to finish. Preserve their intended order; use [] if unused."),
            ["reservation_updates"] = new JObject { ["type"] = "array", ["maxItems"] = MaximumOrders,
                ["description"] = "One queue policy per target using reservation_sequence commands. Replacement happens once before that target's first reservation segment. Use [] when no command participates in shared reservation composition.",
                ["items"] = ReservationPolicy(target) }
        }) }
    });
    private static JObject ReservationPolicy(JObject target)
    {
        var schema = Object(new JObject
        {
            ["target_ref"] = target.DeepClone(),
            ["placement"] = new JObject { ["type"] = "string", ["enum"] = new JArray("append", "replace") },
            ["loop_from"] = Object(new JObject
            {
                ["segment"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumOrders,
                    ["description"] = "1-based reservation_operations segment counting only THIS target's commands in this proposal, never existing game reservations." },
                ["point"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 40,
                    ["description"] = "1-based point inside that segment. Repeat from here through this target's final segment; all repeated commands must support loops. Omit loop_from for no loop." }
            })
        });
        schema["required"] = new JArray("target_ref", "placement");
        return schema;
    }
    private static JObject Orders(JObject target, string description) => new()
    {
        ["type"] = "array", ["maxItems"] = MaximumOrders, ["description"] = description,
        ["items"] = Object(new JObject
        {
            ["operation_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
            ["target_ref"] = target.DeepClone(), ["arguments"] = new JObject { ["type"] = "object" }
        })
    };
    private static JObject Object(JObject fields) => new() { ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray(fields.Properties().Select(p => p.Name)), ["properties"] = fields };
    internal static void Validate(JObject request)
    {
        Fields(request, "request_id", "plans");
        Identifier(request, "request_id");
        if (request["plans"] is not JArray plans || plans.Count < 1 || plans.Count > MaximumPlans) Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in (JArray)request["plans"]!)
        {
            if (item is not JObject plan) { Invalid(); return; }
            Fields(plan, "plan_id", "title", "summary", "reservation_updates", "immediate_operations", "reservation_operations");
            if (plan["reservation_updates"] is not JArray updates || updates.Count > MaximumOrders) Invalid();
            Identifier(plan, "plan_id");
            if (!ids.Add((string)plan["plan_id"]!)) throw new AdvisorOperationException(400, "duplicate_plan_id");
            Text(plan, "title", 120); Text(plan, "summary", 1200);
            var total = 0;
            foreach (var field in OperationFields)
            {
                if (plan[field] is not JArray orders) { Invalid(); return; }
                total += orders.Count;
                foreach (var value in orders)
                {
                    if (value is not JObject order) { Invalid(); return; }
                    Fields(order, "operation_id", "target_ref", "arguments");
                    Text(order, "operation_id", 200); Text(order, "target_ref", 200);
                    if (order["arguments"] is not JObject) Invalid();
                }
            }
            if (total < 1 || total > MaximumOrders) Invalid();
        }
    }
    private static void Fields(JObject value, params string[] names)
    { if (value.Count != names.Length || value.Properties().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) Invalid(); }
    private static void Text(JObject value, string key, int maximum)
    { if (value[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value[key]) || ((string)value[key]!).Length > maximum) Invalid(); }
    private static void Identifier(JObject value, string key)
    {
        Text(value, key, 40);
        if (((string)value[key]!).Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))) Invalid();
    }
    private static void Invalid() => throw new AdvisorOperationException(400, "invalid_plan_submission");
}
