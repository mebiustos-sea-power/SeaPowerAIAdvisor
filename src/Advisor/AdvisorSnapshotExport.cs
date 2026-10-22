using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.AdvisorQueryLimits;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorSnapshotSource
{
    JArray ExportSituation();
}

// Host wrappers observe successful full reads without replaying them through the partial-query gateway.
internal interface IAdvisorSnapshotReadObserver
{
    void RecordSnapshotRead(JObject request, JObject response);
}

// Materialize the same permission-filtered projections used by on-demand reads.
// Query-sized chunks are an internal detail; this never consumes the consultation's read budget.
internal sealed class AdvisorSnapshotExport
{
    private readonly Func<JObject, JToken> read;
    private readonly JArray results = new();
    private readonly Dictionary<string, HashSet<string>> references = new(StringComparer.Ordinal);
    internal AdvisorSnapshotExport(Func<JObject, JToken> read) { this.read = read; }
    internal void Single(string query)
    { Add(query, read(new JObject { ["query"] = query })); }
    internal void Details(string query, IEnumerable<string> ids)
    {
        var selected = ids.ToArray();
        for (var offset = 0; offset < selected.Length; offset += MaxIdsPerQuery)
            Add(query, read(new JObject { ["query"] = query, ["ids"] = new JArray(selected.Skip(offset).Take(MaxIdsPerQuery)) }));
    }
    internal void Pages(string query)
    {
        var items = new JArray();
        var offset = 0;
        while (true)
        {
            var page = (JObject)read(new JObject { ["query"] = query, ["offset"] = offset, ["limit"] = MaxPageSize });
            var rows = (JArray)page["items"]!;
            foreach (var item in rows) items.Add(item.DeepClone());
            if ((bool?)page["has_more"] != true) break;
            var next = (int?)page["next_offset"];
            if (!next.HasValue || next <= offset) throw new InvalidOperationException("Snapshot pagination did not advance.");
            offset = next.Value;
        }
        Add(query, new JObject { ["items"] = items, ["total_count"] = items.Count });
    }
    internal void Unavailable(string query, string code)
    { results.Add(new JObject { ["query"] = query, ["error"] = new JObject { ["code"] = code } }); }
    private void Add(string query, JToken data)
    {
        var copy = data.DeepClone();
        if (copy is JObject bundle)
            foreach (var collection in new[] { "weapon_references", "sensor_references", "routes" })
            {
                if (bundle[collection] is not JArray definitions) continue;
                if (!references.TryGetValue(collection, out var known)) references.Add(collection, known = new HashSet<string>(StringComparer.Ordinal));
                foreach (var row in definitions.OfType<JObject>().ToArray())
                {
                    var id = (string?)row["id"] ?? throw new InvalidOperationException("A snapshot reference ID is missing.");
                    if (!known.Add(id)) row.Remove();
                }
            }
        results.Add(new JObject { ["query"] = query, ["data"] = copy });
    }
    internal JArray Data() => (JArray)results.DeepClone();
    internal static JArray From(IAdvisorQuerySession session) => session is IAdvisorSnapshotSource source
        ? source.ExportSituation() : throw new AdvisorConnectionException("situation_delivery_unavailable");
}
