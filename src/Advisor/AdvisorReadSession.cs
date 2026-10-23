using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

// Read execution and its shared data budget. Tool bindings normalize wire arguments before dispatch.
internal sealed class AdvisorReadSession
{
    private readonly IAdvisorQuerySession knowledge;
    private readonly IAdvisorOperationSession? operations;
    private readonly HashSet<string> knownQueries;
    private readonly AdvisorProviderInput original;
    private int returnedBytes, initialBytes;
    private JObject? snapshot;
    private bool snapshotSupplied;
    internal const string SnapshotQuery = "get_situation";
    private AdvisorSituationDelivery delivery;
    internal AdvisorProviderInput Input { get; private set; }
    internal bool AllowsSituationReads => delivery == AdvisorSituationDelivery.OnDemand;

    internal AdvisorReadSession(IAdvisorQuerySession knowledge, IAdvisorOperationSession? operations, AdvisorProviderInput input)
    {
        this.knowledge = knowledge; this.operations = operations; original = Input = input;
        var snapshotTool = (JObject)knowledge.InitialInput()["query_tool"]!;
        knownQueries = AdvisorRequestShape.Values((JObject)snapshotTool["parameters"]!["properties"]!["requests"]!["items"]!, "query").Append(SnapshotQuery).ToHashSet(StringComparer.Ordinal);
    }

    internal AdvisorProviderInput Prepare(AdvisorSituationDelivery mode, IReadOnlyList<JObject> tools)
    {
        if (mode is not (AdvisorSituationDelivery.OnDemand or AdvisorSituationDelivery.Upfront or AdvisorSituationDelivery.OnRequest))
            throw new AdvisorConnectionException("settings_invalid");
        var input = new AdvisorProviderInput(original.Data, original.SystemInstruction, tools);
        int size;
        if (mode == AdvisorSituationDelivery.Upfront) input = AdvisorSituationInput.Upfront(knowledge, input, out size);
        else if (mode == AdvisorSituationDelivery.OnRequest) input = AdvisorSituationInput.OnRequest(knowledge, input, out size);
        else
        {
            var initial = knowledge.InitialInput();
            size = Bytes(initial) + Math.Max(0, tools.Sum(Bytes) - Bytes(initial["query_tool"]!));
        }
        // Initial information is charged once; a fallback can add a larger snapshot or tool set, never reset the budget.
        var increase = Math.Max(0, size - initialBytes);
        if (increase > MaxTotalBytes - returnedBytes) throw new AdvisorConnectionException("situation_payload_limit");
        returnedBytes += increase; initialBytes = Math.Max(initialBytes, size);
        delivery = mode;
        return Input = input;
    }

    internal string[] Queries(JObject request) => (request["requests"] as JArray ?? new JArray()).Select(QueryName).ToArray();
    private string QueryName(JToken token)
    {
        if (token is not JObject item || item["source"]?.Type != JTokenType.String) return "unknown_query";
        if ((string?)item["source"] == "operation") return item["action"]?.Type == JTokenType.String
            ? (string?)item["action"] switch {
                "list" => "list_operations", "describe" => "describe_operation", "candidates" => "get_operation_candidates",
                _ => AdvisorOperationGateway.ToolName }
            : AdvisorOperationGateway.ToolName;
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
        int? snapshotIndex = null;
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
                    else if (delivery == AdvisorSituationDelivery.OnRequest)
                    {
                        if (args.Properties().Any(p => p.Name != "query") || args["query"]?.Type != JTokenType.String || (string?)args["query"] != SnapshotQuery)
                            results[i] = ItemError(i, "invalid_read_request");
                        else if (snapshotSupplied || snapshotIndex.HasValue)
                            results[i] = new JObject { ["index"] = i, ["query"] = SnapshotQuery, ["status"] = "ok",
                                ["data"] = new JObject { ["status"] = "already_supplied" } };
                        else
                        {
                            snapshot ??= AdvisorSituationInput.Snapshot(knowledge);
                            results[i] = new JObject { ["index"] = i, ["query"] = SnapshotQuery, ["status"] = "ok", ["data"] = snapshot.DeepClone() };
                            snapshotIndex = i;
                        }
                    }
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
            // A full snapshot uses the existing total-data ceiling. Ordinary read responses retain their smaller ceiling.
            if (bytes > (snapshotIndex.HasValue ? MaxTotalBytes : MaxResponseBytes)) return Error(snapshotIndex.HasValue ? "payload_limit" : "response_too_large");
            if (bytes > MaxTotalBytes - returnedBytes) return Error("payload_limit");
        }
        else if (bytes > MaxResponseBytes || bytes > MaxTotalBytes - returnedBytes)
            throw new InvalidOperationException("Read response exceeded its reserved budget.");
        cancellation.ThrowIfCancellationRequested();
        returnedBytes += bytes;
        if (snapshotIndex.HasValue)
        {
            var readRequest = new JObject { ["consultation_id"] = knowledge.Id,
                ["requests"] = new JArray(new JObject { ["query"] = SnapshotQuery }) };
            var row = (JObject)results[snapshotIndex.Value]!.DeepClone(); row["index"] = 0;
            var readResponse = Envelope("ok"); readResponse["results"] = new JArray(row);
            if (knowledge is IAdvisorSnapshotReadObserver observer) observer.RecordSnapshotRead(readRequest, readResponse);
            situationRead?.Invoke(readRequest, readResponse);
            snapshotSupplied = true;
        }
        return response;
    }
    private JObject ItemError(int index, string code)
    { return new JObject { ["index"] = index, ["status"] = "error", ["error"] = new JObject { ["code"] = code } }; }
    private JObject Error(string code)
    { var result = Envelope("error"); result["error"] = new JObject { ["code"] = code }; return result; }
    private JObject Envelope(string status) => new() { ["status"] = status, ["consultation_id"] = knowledge.Id,
        ["round"] = knowledge.RoundsUsed, ["remaining_rounds"] = Math.Max(0, knowledge.MaxRounds - knowledge.RoundsUsed) };
}
