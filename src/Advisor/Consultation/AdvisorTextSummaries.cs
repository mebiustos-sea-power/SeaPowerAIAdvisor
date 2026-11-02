using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorTextSummarySource
{
    AdvisorTextSummarySession? TextSummaries { get; }
}

// Battle-owned memory. Entries are reused only for identical permitted sources; no file or provider state.
internal sealed class AdvisorTextSummaryCache
{
    internal sealed class Entry
    {
        internal readonly string Key, Reference, Original;
        internal string? Summary;
        internal Entry(string key, string reference, string original) { Key = key; Reference = reference; Original = original; }
    }
    private readonly object gate = new();
    private Dictionary<string, Entry> current = new(StringComparer.Ordinal);
    private int sequence;
    internal AdvisorTextSummarySession Open(JObject snapshot, AdvisorInformation information, bool enabled, DiagnosticLog? log)
    {
        lock (gate)
        {
            var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var bindings = new Dictionary<string, Entry>(StringComparer.Ordinal);
            void Add(string local, string kind, string stamp, string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var key = kind + "\0" + stamp + "\0" + text;
                if (!next.TryGetValue(key, out var entry) && !current.TryGetValue(key, out entry))
                    entry = new Entry(key, "text_" + (++sequence).ToString(CultureInfo.InvariantCulture), text);
                next[key] = entry; bindings.Add(local, entry);
            }
            if (information.Allows(InformationKind.BattleBriefing) && (bool?)snapshot["mission_briefing_unavailable"] != true)
                Add("briefing", "briefing", "", (string?)snapshot["mission_briefing"] ?? "");
            if (information.Allows(InformationKind.BattleReports))
                foreach (var report in (snapshot["published_reports"] as JArray ?? new JArray()).OfType<JObject>())
                    Add((string)report["id"]!, "report", (string)report["published_at_utc"]!, (string)report["text"]!);
            current = next;
            return new AdvisorTextSummarySession(this, bindings, enabled, log);
        }
    }
    internal string? Read(Entry entry) { lock (gate) return entry.Summary; }
    internal bool Store(Entry entry, string summary)
    {
        lock (gate)
        {
            if (!current.TryGetValue(entry.Key, out var actual) || !ReferenceEquals(actual, entry)) return false;
            entry.Summary = summary; return true;
        }
    }
    internal void Clear() { lock (gate) current.Clear(); }
}

// One consultation owns delivery/return bookkeeping. Standard-engine activation leaves independent workflows unchanged.
internal sealed class AdvisorTextSummarySession
{
    internal const string CompletionTool = "complete_advice";
    private readonly AdvisorTextSummaryCache cache;
    private readonly IReadOnlyDictionary<string, AdvisorTextSummaryCache.Entry> bindings;
    private readonly Dictionary<string, AdvisorTextSummaryCache.Entry> received = new(StringComparer.Ordinal);
    private readonly bool enabled;
    private readonly DiagnosticLog? log;
    private bool active, completionAvailable;
    internal AdvisorTextSummarySession(AdvisorTextSummaryCache cache, IReadOnlyDictionary<string, AdvisorTextSummaryCache.Entry> bindings, bool enabled, DiagnosticLog? log)
    { this.cache = cache; this.bindings = bindings; this.enabled = enabled; this.log = log; }
    internal void UseStandardEngine(bool standard) => active = standard && enabled;
    internal JObject Present(string local, string original)
    {
        if (!bindings.TryGetValue(local, out var entry))
            return new JObject { ["text"] = original, ["summary_required"] = false };
        var summary = active ? cache.Read(entry) : null;
        return new JObject { ["text"] = summary ?? original, ["summary_ref"] = entry.Reference,
            ["summary_required"] = active && summary == null };
    }
    internal AiProviderInput Prepare(AiProviderInput input, AdvisorPrompts prompts)
    {
        completionAvailable = active && bindings.Values.Any(entry => cache.Read(entry) == null);
        Observe(input.Data);
        if (!completionAvailable) return input;
        var references = bindings.Values.Select(entry => entry.Reference).Distinct().ToArray();
        var tool = new JObject
        {
            ["name"] = CompletionTool, ["description"] = "Return the final answer and requested compact text memories without another API round trip.",
            ["parameters"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("answer", "summaries"),
                ["properties"] = new JObject
                {
                    ["answer"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Final user-facing answer." },
                    ["summaries"] = new JObject
                    {
                        ["type"] = "array", ["maxItems"] = references.Length,
                        ["items"] = new JObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("summary_ref", "text"),
                            ["properties"] = new JObject
                            {
                                ["summary_ref"] = new JObject { ["type"] = "string", ["enum"] = new JArray(references) },
                                ["text"] = new JObject { ["type"] = "string", ["minLength"] = 1 }
                            }
                        }
                    }
                }
            }
        };
        return new AiProviderInput(input.Data, input.SystemInstruction + "\n" + prompts.Text("battle.json", "text_summaries"), input.Tools.Concat(new[] { tool }));
    }
    internal void Observe(JToken data)
    {
        if (!active) return;
        foreach (var row in (data is JContainer container ? container.DescendantsAndSelf() : Enumerable.Empty<JToken>()).OfType<JObject>())
        {
            if (row["summary_required"]?.Type != JTokenType.Boolean || (bool?)row["summary_required"] != true || row["summary_ref"]?.Type != JTokenType.String) continue;
            var entry = bindings.Values.FirstOrDefault(e => e.Reference == (string?)row["summary_ref"]);
            if (entry != null && row["text"]?.Type == JTokenType.String && (string?)row["text"] == entry.Original)
                received[entry.Reference] = entry;
        }
    }
    internal AiReply ReadReply(AiReply reply)
    {
        if (!completionAvailable) return reply;
        var final = reply.Calls.Where(c => c.Name == CompletionTool).ToArray();
        if (final.Length == 0)
        {
            if (reply.Calls.Count == 0) Missing(new HashSet<string>(StringComparer.Ordinal));
            return reply;
        }
        if (reply.Calls.Count != 1 || final.Length != 1) throw new AiConnectionException("invalid_function_call");
        var args = final[0].Arguments;
        if (args["answer"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args["answer"]))
            throw new AiConnectionException("invalid_response");
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        if (args["summaries"] is JArray rows)
        {
            var duplicates = rows.OfType<JObject>().Where(row => row["summary_ref"]?.Type == JTokenType.String).Select(row => (string)row["summary_ref"]!)
                .GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var item in rows)
            {
                if (item is not JObject row || row["summary_ref"]?.Type != JTokenType.String || row["text"]?.Type != JTokenType.String ||
                    row.Properties().Any(p => p.Name is not ("summary_ref" or "text"))) { Record("invalid_entry"); continue; }
                var reference = (string)row["summary_ref"]!; var text = ((string)row["text"]!).Trim();
                if (!received.TryGetValue(reference, out var entry) || duplicates.Contains(reference) || text.Length == 0 ||
                    text.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) { Record("invalid_entry"); continue; }
                if (cache.Store(entry, text)) accepted.Add(reference); else Record("source_changed");
            }
        }
        else Record("missing_entries");
        Missing(accepted);
        return new AiReply((string)args["answer"]!, Array.Empty<AiToolCall>(), reply.TotalTokens);
    }
    private void Missing(HashSet<string> accepted)
    { foreach (var reference in received.Keys.Where(id => !accepted.Contains(id))) Record("missing_summary:" + reference); }
    private void Record(string reason) => log?.Info("Advisor text compression: " + reason);
}
