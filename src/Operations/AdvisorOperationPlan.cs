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
    internal static readonly AdvisorOperationPlanPolicy Default = new(1, 1, AdvisorPlanSelection.Single);
    internal int Minimum { get; }
    internal int Maximum { get; }
    internal AdvisorPlanSelection Selection { get; }
    internal AdvisorOperationPlanPolicy(int minimum, int maximum, AdvisorPlanSelection selection)
    {
        if (minimum < 1 || maximum < minimum || maximum > AdvisorOperationPlanBatch.MaximumPlans || !Enum.IsDefined(typeof(AdvisorPlanSelection), selection))
            throw new ArgumentOutOfRangeException(nameof(minimum), "Invalid operation plan policy.");
        Minimum = minimum; Maximum = maximum; Selection = selection;
    }
    internal JObject Json() => new() { ["minimum"] = Minimum, ["maximum"] = Maximum,
        ["selection"] = Selection == AdvisorPlanSelection.Single ? "single" : "multiple" };
}

internal sealed class AdvisorOperationPlan
{
    private readonly List<AdvisorOperationOrder> orders;
    internal string Id { get; }
    internal string Title { get; }
    internal string Summary { get; }
    internal IReadOnlyList<AdvisorOperationOrder> Orders => orders.AsReadOnly();
    internal AdvisorOperationPlan(string id, string title, string summary, IEnumerable<AdvisorOperationOrder> orders)
    { Id = id; Title = title; Summary = summary; this.orders = orders.ToList(); }
    internal void Add(AdvisorOperationOrder order) => orders.Add(order);
}

// The wire contract is shared by tool publication and request validation. Command arguments stay opaque here.
internal static class AdvisorOperationPlanBatch
{
    internal const int MaximumPlans = 8;
    internal const int MaximumOrders = 40;
    internal static JObject Schema(JObject target, string requestDescription) => Object(new JObject
    {
        ["request_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 40,
            ["pattern"] = "^[A-Za-z0-9._-]+$", ["description"] = requestDescription },
        ["plans"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumPlans, ["items"] = Object(new JObject
        {
            ["plan_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 40, ["pattern"] = "^[A-Za-z0-9._-]+$" },
            ["title"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 },
            ["summary"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1200, ["description"] = "Purpose and main trade-off." },
            ["operations"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaximumOrders, ["items"] = Object(new JObject
            {
                ["operation_id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
                ["target_ref"] = target.DeepClone(), ["arguments"] = new JObject { ["type"] = "object" }
            }) }
        }) }
    });
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
            Fields(plan, "plan_id", "title", "summary", "operations");
            Identifier(plan, "plan_id");
            if (!ids.Add((string)plan["plan_id"]!)) throw new AdvisorOperationException(400, "duplicate_plan_id");
            Text(plan, "title", 120); Text(plan, "summary", 1200);
            if (plan["operations"] is not JArray orders || orders.Count < 1 || orders.Count > MaximumOrders) Invalid();
            foreach (var value in (JArray)plan["operations"]!)
            {
                if (value is not JObject order) { Invalid(); return; }
                Fields(order, "operation_id", "target_ref", "arguments");
                Text(order, "operation_id", 200); Text(order, "target_ref", 200);
                if (order["arguments"] is not JObject) Invalid();
            }
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
