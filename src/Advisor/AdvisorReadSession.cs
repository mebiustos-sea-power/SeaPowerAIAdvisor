using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

// The standard engine's read-only facade. Snapshot reads and live operation lookups keep their own owners.
internal sealed class AdvisorReadSession
{
    internal const string ToolName = "query_game";
    private readonly IAdvisorQuerySession knowledge;
    private readonly IAdvisorOperationSession? operations;
    private readonly HashSet<string> knownQueries;
    private int returnedBytes;
    internal AdvisorProviderInput Input { get; private set; }
    internal bool AllowsSituationReads => delivery == AdvisorSituationDelivery.OnDemand;
    private readonly AdvisorProviderInput onDemand;
    private AdvisorProviderInput? upfront;
    private AdvisorSituationDelivery delivery;
    private int initialBytes;

    internal AdvisorReadSession(IAdvisorQuerySession knowledge, IAdvisorOperationSession? operations, AdvisorProviderInput input)
    {
        this.knowledge = knowledge; this.operations = operations;
        var tool = (JObject)(input.Tool ?? throw new InvalidOperationException("The snapshot query definition is missing.")).DeepClone();
        tool["name"] = ToolName;
        tool["description"] = knowledge.Prompts.Text("common.json", "read_tool_description");
        var item = (JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!;
        knownQueries = AdvisorRequestShape.Values(item, "query").ToHashSet(StringComparer.Ordinal);
        var variants = new JArray(AdvisorRequestShape.Variants(item).Select(s => WithSource(s, "situation")));
        JObject? executionTool = null;
        if (operations != null)
        {
            foreach (var variant in AdvisorRequestShape.Variants(AdvisorOperationGateway.ReadSchema(operations.Tool)))
                variants.Add(WithSource(variant, "operation"));
            executionTool = (JObject)operations.Tool.DeepClone();
            executionTool["description"] = knowledge.Prompts.Text("battle.json", "operation_execution_description");
            executionTool["parameters"] = AdvisorOperationGateway.ExecutionSchema(operations.Tool);
        }
        tool["parameters"]!["properties"]!["requests"]!["items"] = new JObject { ["anyOf"] = variants };
        Input = new AdvisorProviderInput(input.Data, input.SystemInstruction, tool, executionTool);
        // The information budget excludes chat history and user preferences; the sending budget covers the whole request.
        returnedBytes = Bytes(knowledge.InitialInput()) + Math.Max(0, Bytes(tool) - Bytes(input.Tool!)) +
            (executionTool == null ? 0 : Bytes(executionTool));
        if (returnedBytes > MaxTotalBytes) throw new InvalidOperationException("Mandatory read input exceeds the consultation payload limit.");
        onDemand = Input; initialBytes = returnedBytes;
    }

    internal AdvisorProviderInput Prepare(AdvisorSituationDelivery mode)
    {
        if (mode == AdvisorSituationDelivery.OnDemand) { delivery = mode; return Input = onDemand; }
        if (mode != AdvisorSituationDelivery.Upfront) throw new AdvisorConnectionException("settings_invalid");
        if (upfront == null)
        {
            var prepared = AdvisorSituationInput.Upfront(knowledge, onDemand, operations, out var size);
            var increase = Math.Max(0, size - initialBytes);
            if (increase > MaxTotalBytes - returnedBytes) throw new AdvisorConnectionException("situation_payload_limit");
            returnedBytes += increase; initialBytes = Math.Max(initialBytes, size); upfront = prepared;
        }
        delivery = mode;
        return Input = upfront;
    }

    private static JObject WithSource(JObject schema, string source)
    {
        var result = (JObject)schema.DeepClone();
        ((JObject)result["properties"]!).AddFirst(new JProperty("source", new JObject { ["type"] = "string", ["enum"] = new JArray(source) }));
        ((JArray)result["required"]!).Insert(0, "source");
        return result;
    }

    internal string[] Queries(JObject request) => (request["requests"] as JArray ?? new JArray()).Select(QueryName).ToArray();
    private string QueryName(JToken token)
    {
        if (token is not JObject item || item["source"]?.Type != JTokenType.String) return "unknown_query";
        if ((string?)item["source"] == "operation") return AdvisorOperationGateway.ToolName;
        return (string?)item["source"] == "situation" && item["query"]?.Type == JTokenType.String && knownQueries.Contains((string)item["query"]!)
            ? (string)item["query"]! : "unknown_query";
    }

    internal async Task<JObject> ExecuteAsync(JObject request, CancellationToken cancellation, Action<JObject, JObject>? situationRead = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.Properties().Any(p => p.Name != "requests") || request["requests"] is not JArray batch ||
            batch.Count < 1 || batch.Count > MaxQueriesPerRound) return Error("invalid_arguments");
        if (Bytes(request) > MaxRequestBytes) return Error("request_too_large");
        if (returnedBytes >= MaxTotalBytes) return Error("payload_limit");
        var results = new JObject?[batch.Count];
        var queries = new JArray();
        var positions = new List<int>();
        for (var i = 0; i < batch.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (batch[i] is not JObject item || item["source"]?.Type != JTokenType.String)
            { results[i] = ItemError(i, "invalid_read_request"); continue; }
            var args = (JObject)item.DeepClone(); args.Remove("source");
            switch ((string)item["source"]!)
            {
                case "situation":
                    if (delivery == AdvisorSituationDelivery.Upfront) results[i] = ItemError(i, "situation_already_supplied");
                    else { queries.Add(args); positions.Add(i); }
                    break;
                case "operation":
                    if (operations == null) { results[i] = ItemError(i, "unit_operations_disabled"); break; }
                    if (args["action"]?.Type != JTokenType.String || (string?)args["action"] is not ("list" or "describe" or "candidates"))
                    { results[i] = ItemError(i, "invalid_read_request"); break; }
                    var data = await operations.ExecuteAsync(args, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    results[i] = (string?)data["status"] == "error" ? (JObject)data.DeepClone()
                        : new JObject { ["status"] = "ok", ["data"] = data };
                    results[i]!["index"] = i;
                    break;
                default: results[i] = ItemError(i, "invalid_read_request"); break;
            }
        }
        var otherRows = new JArray(results.Where(r => r != null).Select(r => r!));
        var response = Envelope("ok");
        if (queries.Count > 0)
        {
            // Reserve the exact cost of appending other rows before the snapshot commits first-delivery references.
            var overhead = otherRows.Count == 0 ? 0 : Bytes(otherRows) - 1;
            if (overhead >= MaxResponseBytes) return Error("response_too_large");
            if (overhead >= MaxTotalBytes - returnedBytes) return Error("payload_limit");
            var queryRequest = new JObject { ["consultation_id"] = knowledge.Id, ["requests"] = queries,
                ["partial_results"] = true, ["response_limit_bytes"] = MaxResponseBytes - overhead,
                ["remaining_data_bytes"] = MaxTotalBytes - returnedBytes - overhead };
            var read = knowledge.Execute(queryRequest);
            situationRead?.Invoke(queryRequest, read);
            cancellation.ThrowIfCancellationRequested();
            if ((string?)read["status"] == "error") return read;
            if (read["results"] is not JArray rows || rows.Count != positions.Count)
                throw new InvalidOperationException("The situation gateway returned an invalid result batch.");
            response = (JObject)read.DeepClone();
            for (var i = 0; i < positions.Count; i++)
            {
                var row = (JObject)rows[i].DeepClone(); row["index"] = positions[i]; results[positions[i]] = row;
            }
        }
        response["results"] = new JArray(results.Select(r => r ?? throw new InvalidOperationException("A read result is missing.")));
        var bytes = Bytes(response);
        if (queries.Count == 0)
        {
            if (bytes > MaxResponseBytes) return Error("response_too_large");
            if (bytes > MaxTotalBytes - returnedBytes) return Error("payload_limit");
        }
        else if (bytes > MaxResponseBytes || bytes > MaxTotalBytes - returnedBytes)
            throw new InvalidOperationException("Read response exceeded its reserved budget.");
        returnedBytes += bytes;
        return response;
    }
    private JObject ItemError(int index, string code)
    { return new JObject { ["index"] = index, ["status"] = "error", ["error"] = new JObject { ["code"] = code } }; }
    private JObject Error(string code)
    { var result = Envelope("error"); result["error"] = new JObject { ["code"] = code }; return result; }
    private JObject Envelope(string status) => new() { ["status"] = status, ["consultation_id"] = knowledge.Id,
        ["round"] = knowledge.RoundsUsed, ["remaining_rounds"] = Math.Max(0, knowledge.MaxRounds - knowledge.RoundsUsed) };
}
