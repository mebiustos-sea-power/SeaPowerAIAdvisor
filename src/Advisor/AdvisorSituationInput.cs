using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

// Input assembly is common policy. Game modes supply projections; adapters only encode the result.
internal static class AdvisorSituationInput
{
    internal static AdvisorProviderInput Upfront(IAdvisorQuerySession knowledge, AdvisorProviderInput input,
        out int informationBytes)
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
        var information = Data(knowledge.InitialInput());
        // Match on-demand accounting: game input, instructions and tool definitions, excluding user preferences/history.
        information["instructions"] = instructions;
        informationBytes = Bytes(information) + input.Tools.Sum(Bytes);
        return new AdvisorProviderInput(Data(input.Data), system, input.Tools);
    }
}
