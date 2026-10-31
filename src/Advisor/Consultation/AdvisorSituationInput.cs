using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Input assembly is common policy. Game modes supply projections; adapters only encode the result.
internal static class AdvisorSituationInput
{
    private static readonly string[] envelope = { "schema_version", "mode", "reply_language", "question", "consultation_id" };
    private static readonly string[] controls = { "instructions", "query_tool", "knowledge_catalog", "query_limits", "interaction_mode" };
    internal static JObject Snapshot(IAdvisorQuerySession knowledge)
    {
        var complete = Upfront(knowledge, new AiProviderInput(knowledge.InitialInput(), "", System.Array.Empty<JObject>())).Data;
        foreach (var key in envelope.Concat(controls)) complete.Remove(key);
        return complete;
    }
    internal static AiProviderInput OnRequest(IAdvisorQuerySession knowledge, AiProviderInput input)
    {
        JObject Minimal(JObject original)
        {
            var result = (JObject)original.DeepClone();
            foreach (var property in knowledge.InitialInput().Properties())
                if (!envelope.Contains(property.Name)) result.Remove(property.Name);
            result["interaction_mode"] = "on_request";
            return result;
        }
        var instructions = (JObject)knowledge.InitialInput()["instructions"]!.DeepClone();
        instructions.Remove("retrieval");
        instructions["retrieval_policy"] = knowledge.Prompts.Text("common.json", "on_request_retrieval_policy");
        return new AiProviderInput(Minimal(input.Data), knowledge.Prompts.SystemInstruction(instructions), input.Tools);
    }

    internal static AiProviderInput Upfront(IAdvisorQuerySession knowledge, AiProviderInput input)
    {
        var snapshot = AdvisorSnapshotExport.From(knowledge);
        JObject Data(JObject original)
        {
            var result = (JObject)original.DeepClone();
            result["interaction_mode"] = "upfront";
            result.Remove("instructions"); result.Remove("query_tool");
            result.Remove("knowledge_catalog"); result.Remove("query_limits");
            result["situation_data"] = snapshot.DeepClone();
            if (result["situation"]?["coverage"] is JObject coverage)
            {
                coverage["included"] = new JArray((coverage["included"] as JArray ?? new JArray())
                    .Concat(coverage["available_on_request"] as JArray ?? new JArray()).Values<string>().Distinct());
                coverage.Remove("available_on_request");
                coverage.Remove("weapon_reference_delivery"); coverage.Remove("sensor_reference_delivery");
            }
            return result;
        }
        var instructions = (JObject)knowledge.InitialInput()["instructions"]!.DeepClone();
        instructions.Remove("retrieval");
        instructions["retrieval_policy"] = knowledge.Prompts.Text("common.json", "upfront_retrieval_policy");
        var system = knowledge.Prompts.SystemInstruction(instructions);
        return new AiProviderInput(Data(input.Data), system, input.Tools);
    }
}
