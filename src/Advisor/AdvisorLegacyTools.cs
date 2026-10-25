using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Compatibility for existing standard extensions. New adapters use the split definitions.
internal static class AdvisorLegacyTools
{
    internal const string ReadName = "query_game";
    internal static IEnumerable<AdvisorToolBinding> Create(JObject snapshotTool, IAdvisorOperationSession? operations,
        AdvisorPrompts prompts, bool situationReads, bool fullSnapshot = false)
    {
        var tool = (JObject)snapshotTool.DeepClone(); tool["name"] = ReadName;
        tool["description"] = prompts.Text("common.json", situationReads ? "read_tool_description" : "operation_read_tool_description");
        var variants = new JArray();
        if (fullSnapshot)
        {
            tool["description"] = prompts.Text("common.json", "snapshot_tool_description") +
                (operations == null ? "" : " " + prompts.Text("common.json", "operation_read_tool_description"));
            variants.Add(WithSource(new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("query"), ["properties"] = new JObject {
                    ["query"] = new JObject { ["type"] = "string", ["enum"] = new JArray(AdvisorReadSession.SnapshotQuery) } } }, "situation"));
        }
        if (situationReads)
            foreach (var schema in AdvisorRequestShape.Variants((JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!))
                variants.Add(WithSource(schema, "situation"));
        if (operations != null)
            foreach (var schema in AdvisorRequestShape.Variants(AdvisorOperationGateway.ReadSchema(operations.Tool)))
                variants.Add(WithSource(schema, "operation"));
        if (variants.Count > 0)
        {
            tool["parameters"]!["properties"]!["requests"]!["items"] = new JObject { ["anyOf"] = variants };
            yield return new AdvisorToolBinding(tool, args => args.Properties().Any(p => p.Name != "requests") ||
                args["requests"] is not JArray batch || batch.Count == 0 ? null : (JArray)batch.DeepClone());
        }
        if (operations != null)
        {
            var execution = (JObject)operations.Tool.DeepClone();
            execution["description"] = prompts.Text("battle.json", "operation_execution_description");
            execution["parameters"] = AdvisorOperationGateway.ActionSchema(operations.Tool, "submit");
            yield return new AdvisorToolBinding(execution, "propose_operation_plans",
                (args, cancellation) => AdvisorOperationGateway.SubmitAsync(operations, args, cancellation));
        }
    }
    private static JObject WithSource(JObject schema, string source)
    {
        var result = (JObject)schema.DeepClone();
        ((JObject)result["properties"]!).AddFirst(new JProperty("source", new JObject { ["type"] = "string", ["enum"] = new JArray(source) }));
        ((JArray)result["required"]!).Insert(0, "source");
        return result;
    }
}
