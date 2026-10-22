using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Partial results and reduced budgets are host options, not model-selected query arguments.
internal static class AdvisorQueryBatch
{
    internal static bool Partial(JObject request)
    {
        if (request["partial_results"] == null) return false;
        if (request["partial_results"]!.Type != JTokenType.Boolean) throw Invalid();
        return (bool)request["partial_results"]!;
    }
    internal static int Budget(JObject request, string name, int maximum)
    {
        if (request[name] == null) return maximum;
        if (request[name]!.Type != JTokenType.Integer || !int.TryParse(request[name]!.ToString(), out var value) || value < 0 || value > maximum)
            throw Invalid();
        return value;
    }
    internal static JArray Execute(JArray requests, bool partial, Func<JToken, JObject> dispatch)
    {
        var results = new JArray();
        foreach (var request in requests)
        {
            JObject result;
            try { result = dispatch(request); if (partial) result["status"] = "ok"; }
            catch (AdvisorQueryException error) when (partial)
            {
                result = new JObject { ["query"] = request is JObject item ? item["query"]?.DeepClone() : null,
                    ["status"] = "error", ["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message } };
            }
            if (partial) result["index"] = results.Count;
            results.Add(result);
        }
        return results;
    }
    private static AdvisorQueryException Invalid() => new("invalid_arguments", "invalid_query_budget");
}
