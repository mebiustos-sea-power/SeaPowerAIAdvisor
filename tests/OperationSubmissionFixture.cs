using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

// Builds the public batch from the tests' internal order fixtures. No incremental staging is performed.
internal static class OperationSubmissionFixture
{
    internal static JObject Batch(params JObject[] orders)
    {
        var updates = new JArray(); var targets = new HashSet<string>(StringComparer.Ordinal);
        var steps = new JArray(orders.Select(order =>
        {
            var operation = (string?)order["operation"] ?? (string)order["operation_id"]!;
            var target = (string)order["target_ref"]!; var args = (JObject)order["arguments"]!.DeepClone();
            if (operation is SonobuoyDropExtension.OperationId or MovementExtension.OperationId or FormationReturnExtension.OperationId)
            {
                if (targets.Add(target)) updates.Add(new JObject { ["target_ref"] = target, ["placement"] = args["placement"]?.DeepClone() ?? new JValue("append") });
                args.Remove("placement");
            }
            return new JObject { ["operation_id"] = operation, ["target_ref"] = target, ["arguments"] = args };
        }));
        return new JObject { ["request_id"] = orders.Length == 0 ? "empty" : orders[0]["request_id"]?.DeepClone() ?? new JValue("submission"),
            ["plans"] = new JArray(new JObject { ["plan_id"] = "one", ["title"] = "Test plan", ["summary"] = "Test operations",
                ["reservation_updates"] = updates, ["operations"] = steps }) };
    }
    internal static JObject Submit(AdvisorOperations service, string owner, params JObject[] orders) => service.SubmitPlans(owner, Batch(orders));
    internal static JObject DeveloperRequest(params JObject[] orders)
    { var request = Batch(orders); request["scope_id"] = orders[0]["scope_id"]!.DeepClone(); return request; }
    internal static JObject Adopt(AdvisorOperations service, params JObject[] orders) => service.SubmitAndApprove(DeveloperRequest(orders));
    internal static JObject AiRequest(JObject order)
    { var batch = Batch(order); batch["action"] = "submit"; return batch; }
    internal static JObject Arguments(JObject batch) => (JObject)batch["plans"]![0]!["operations"]![0]!["arguments"]!;
    internal static JToken? TargetResolution(JObject result) => result["target_ref_resolution"] ??
        (result["plan_resolutions"] as JArray)?.FirstOrDefault(x => x["target_ref_resolution"] != null)?["target_ref_resolution"];
    internal static JArray ArgumentResolutions(JObject result) => (JArray?)(result["argument_resolutions"] ??
        (result["plan_resolutions"] as JArray)?.FirstOrDefault(x => x["argument_resolutions"] != null)?["argument_resolutions"]) ?? new JArray();
    internal static JToken Data(JObject result, int index = 0) => result["results"]![index]!["data"]!;
}
