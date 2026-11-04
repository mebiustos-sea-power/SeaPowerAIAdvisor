using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Publications belong to one mission load. Snapshots contain values, never popup or unit references.
internal sealed class BattleMissionMessageHistory
{
    private object? mission;
    private readonly JArray messages = new();
    internal void Begin(object current)
    { mission = current; messages.Clear(); }
    internal void Clear()
    { mission = null; messages.Clear(); }
    internal void Publish(object current, string header, string body, DateTime publishedAt)
    {
        if (!ReferenceEquals(mission, current)) Begin(current);
        var text = string.Join("\n\n", new[] { header, body }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        if (text.Length == 0) return;
        messages.Add(new JObject
        {
            ["id"] = "message_" + (messages.Count + 1).ToString(CultureInfo.InvariantCulture),
            ["published_at_utc"] = publishedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["text"] = text
        });
    }
    internal JArray Capture(object current) => ReferenceEquals(mission, current) ? (JArray)messages.DeepClone() : new JArray();
}
