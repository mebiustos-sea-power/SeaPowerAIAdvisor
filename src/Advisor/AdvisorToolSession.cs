using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// The standard engine sees one registry, regardless of the number of tools or the provider format.
internal sealed class AdvisorToolSession
{
    internal const int MaxCallsPerResponse = 40;
    private readonly IAdvisorQuerySession knowledge;
    private readonly IAdvisorOperationSession? operations;
    private IReadOnlyDictionary<string, AdvisorToolBinding> bindings = new Dictionary<string, AdvisorToolBinding>();
    internal AdvisorReadSession Reads { get; }
    internal AdvisorToolSession(IAdvisorQuerySession knowledge, IAdvisorOperationSession? operations, AdvisorProviderInput input)
    {
        this.knowledge = knowledge; this.operations = operations;
        Reads = new AdvisorReadSession(knowledge, operations, input);
        Prepare(AdvisorSituationDelivery.OnDemand);
    }
    internal AdvisorProviderInput Prepare(AdvisorSituationDelivery mode, bool legacy = false)
    {
        var snapshotTool = (JObject)knowledge.InitialInput()["query_tool"]!;
        var tools = new List<AdvisorToolBinding>();
        if (legacy) tools.AddRange(AdvisorLegacyTools.Create(snapshotTool, operations, knowledge.Prompts, mode == AdvisorSituationDelivery.OnDemand));
        else
        {
            if (mode == AdvisorSituationDelivery.OnDemand)
            {
                snapshotTool["name"] = "query_situation";
                snapshotTool["description"] = knowledge.Prompts.Text("common.json", "situation_tool_description");
                tools.Add(new AdvisorToolBinding(snapshotTool, SituationRequests));
            }
            if (operations != null) tools.AddRange(AdvisorOperationTools.Create(operations, knowledge.Prompts));
        }
        var input = Reads.Prepare(mode, tools.Select(t => t.Definition).ToArray());
        bindings = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        return input;
    }
    private static JArray? SituationRequests(JObject args)
    {
        if (args.Properties().Any(p => p.Name != "requests") || args["requests"] is not JArray batch || batch.Count == 0) return null;
        return new JArray(batch.Select(item =>
        {
            if (item is not JObject query) return item.DeepClone();
            if (query["source"] != null) { var invalid = (JObject)query.DeepClone(); invalid["source"] = "invalid"; return invalid; }
            var value = (JObject)query.DeepClone(); value["source"] = "situation"; return value;
        }));
    }
    internal async Task<AdvisorToolDispatch> ExecuteAsync(IReadOnlyList<AdvisorToolCall> calls, CancellationToken cancellation,
        Action<string[], bool> progress, Action<JObject, JObject> situationRead)
    {
        cancellation.ThrowIfCancellationRequested();
        if (calls.Count > MaxCallsPerResponse) throw new AdvisorConnectionException("tool_call_limit");
        if (calls.Any(c => string.IsNullOrWhiteSpace(c.Id) || !bindings.ContainsKey(c.Name)) ||
            calls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count)
            throw new AdvisorConnectionException("invalid_function_call");
        var readCalls = calls.Where(c => bindings[c.Name].Read != null).ToArray();
        var results = new Dictionary<string, AdvisorToolResult>(StringComparer.Ordinal);
        var queries = new List<string>(); var completed = new List<string>();
        AdvisorCompletionReason? limit = null;
        if (readCalls.Length > 0)
        {
            var batch = new JArray(); var lengths = new List<int>(); var valid = true;
            foreach (var call in readCalls)
            {
                var items = bindings[call.Name].Read!(call.Arguments);
                if (items == null) { valid = false; break; }
                lengths.Add(items.Count); foreach (var item in items) batch.Add(item.DeepClone());
            }
            var request = valid ? new JObject { ["requests"] = batch } : new JObject();
            var pending = Reads.Queries(request);
            var hasSituation = valid && Reads.AllowsSituationReads && batch.OfType<JObject>().Any(q => q["source"]?.Type == JTokenType.String && (string?)q["source"] == "situation");
            progress(pending, hasSituation);
            var response = await Reads.ExecuteAsync(request, cancellation, situationRead).ConfigureAwait(false);
            queries.AddRange(pending);
            if (response["results"] is JArray rows)
                completed.AddRange(rows.OfType<JObject>().Where(r => (string?)r["status"] == "ok").Select(r => pending[(int)r["index"]!]));
            var offset = 0;
            for (var i = 0; i < readCalls.Length; i++)
            {
                var reply = (JObject)response.DeepClone();
                if (response["results"] is JArray all)
                {
                    var selected = new JArray(all.Skip(offset).Take(lengths[i]).Select(r => r.DeepClone()));
                    for (var n = 0; n < selected.Count; n++) selected[n]!["index"] = n;
                    reply["results"] = selected; offset += lengths[i];
                }
                results.Add(readCalls[i].Id, new AdvisorToolResult(readCalls[i], reply));
            }
            var error = (string?)response["error"]?["code"];
            if (error == "payload_limit") limit = AdvisorCompletionReason.PayloadLimit;
            else if (error == "round_limit") limit = AdvisorCompletionReason.RoundLimit;
        }
        foreach (var call in calls.Where(c => bindings[c.Name].Execute != null))
        {
            cancellation.ThrowIfCancellationRequested();
            var binding = bindings[call.Name]; progress(new[] { binding.ProgressQuery }, false);
            var response = await binding.Execute!(call.Arguments, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            results.Add(call.Id, new AdvisorToolResult(call, response));
            queries.Add(binding.ProgressQuery); completed.Add(binding.ProgressQuery);
        }
        return new AdvisorToolDispatch(calls.Select(c => results[c.Id]).ToArray(), queries.ToArray(), completed.ToArray(), limit);
    }
}

internal sealed class AdvisorToolDispatch
{
    internal IReadOnlyList<AdvisorToolResult> Results { get; }
    internal string[] Queries { get; }
    internal string[] CompletedQueries { get; }
    internal AdvisorCompletionReason? Limit { get; }
    internal AdvisorToolDispatch(IReadOnlyList<AdvisorToolResult> results, string[] queries, string[] completed, AdvisorCompletionReason? limit)
    { Results = results; Queries = queries; CompletedQueries = completed; Limit = limit; }
}
