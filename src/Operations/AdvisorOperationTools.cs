using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Operation-owned schemas and translation. The dispatcher never interprets command arguments.
internal static class AdvisorOperationTools
{
    internal static IEnumerable<AdvisorToolBinding> Create(IAdvisorOperationSession session, AdvisorPrompts prompts)
    {
        foreach (var (name, action, description) in new[] {
            ("list_operations", "list", "operation_list_description"),
            ("describe_operation", "describe", "operation_describe_description"),
            ("get_operation_candidates", "candidates", "operation_candidates_description") })
        {
            var definition = Definition(session, name, action, prompts.Text("battle.json", description));
            yield return new AdvisorToolBinding(definition, args =>
            {
                if (args["action"] != null || args["source"] != null) return null;
                var item = (JObject)args.DeepClone(); item["source"] = "operation"; item["action"] = action;
                return new JArray(item);
            });
        }
        yield return new AdvisorToolBinding(Definition(session, "propose_operation", "execute",
            prompts.Text("battle.json", "operation_execution_description")), AdvisorOperationGateway.ToolName, (args, cancellation) =>
        {
            if (args["action"] != null || args["source"] != null)
                return Task.FromResult(new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = "invalid_operation_request" } });
            var request = (JObject)args.DeepClone(); request["action"] = "execute";
            return AdvisorOperationGateway.ProposeAsync(session, request, cancellation);
        });
    }
    private static JObject Definition(IAdvisorOperationSession session, string name, string action, string description)
    {
        var schema = AdvisorOperationGateway.ActionSchema(session.Tool, action);
        ((JObject)schema["properties"]!).Remove("action");
        var required = (JArray)schema["required"]!;
        required.Remove(required.Single(t => (string?)t == "action"));
        return new JObject { ["name"] = name, ["description"] = description, ["parameters"] = schema };
    }
}
