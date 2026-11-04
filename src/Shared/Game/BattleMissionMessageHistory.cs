using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// One active mission owns the history. Stored publications and snapshots contain values, never UI references.
internal sealed class BattleMissionMessageHistory
{
    private readonly SaveDataFormat format;
    private object? mission;
    private JArray messages = new();
    internal BattleMissionMessageHistory(SaveDataFormat? format = null) => this.format = format ?? new SaveDataFormat(1, 1);
    internal void Begin(object current)
    { mission = current; messages.Clear(); }
    internal void Clear()
    { mission = null; messages.Clear(); }
    internal void Publish(object current, string header, string body, DateTime publishedAt)
    {
        if (!ReferenceEquals(mission, current)) Begin(current);
        var text = string.Join("\n\n", new[] { header, body }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        if (text.Length == 0) return;
        Append(messages, text, publishedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }
    internal JArray Capture(object current) => ReferenceEquals(mission, current) ? (JArray)messages.DeepClone() : new JArray();
    internal JObject? SaveData() => messages.Count == 0 ? null : new JObject
    {
        ["version"] = format.CurrentVersion,
        ["messages"] = new JArray(messages.Cast<JObject>().Select(row => new JObject
        { ["published_at_utc"] = row["published_at_utc"]!.DeepClone(), ["text"] = row["text"]!.DeepClone() }))
    };
    internal SaveDataUpgradeResult? Restore(JToken? source)
    {
        // Messages published during this load remain even when there is no saved history.
        if (source == null) return null;
        if (source is not JObject document) throw new FormatException("Invalid mission-message save data.");
        var result = format.Upgrade(document);
        if (result.Document == null) return result;
        if (mission == null) throw new InvalidOperationException("Mission messages cannot be restored before a mission begins.");
        if (result.Document["messages"] is not JArray saved) throw new FormatException("Saved mission messages must be an array.");
        var restored = new JArray();
        var remaining = new Dictionary<(string Text, string Time), int>();
        foreach (var token in saved)
        {
            if (token is not JObject row || row["text"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)row["text"]) ||
                row["published_at_utc"]?.Type != JTokenType.String || !DateTime.TryParseExact((string?)row["published_at_utc"],
                    "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                throw new FormatException("Invalid saved mission-message publication.");
            var key = ((string)row["text"]!, (string)row["published_at_utc"]!);
            Append(restored, key.Item1, key.Item2);
            remaining.TryGetValue(key, out var count); remaining[key] = count + 1;
        }
        // Match re-published saved popups by text and publication time, preserving repeated publications.
        foreach (var row in messages.Cast<JObject>())
        {
            var key = ((string)row["text"]!, (string)row["published_at_utc"]!);
            if (remaining.TryGetValue(key, out var count) && count > 0) remaining[key] = count - 1;
            else Append(restored, key.Item1, key.Item2);
        }
        messages = restored;
        return null;
    }
    // IDs are local snapshot references. Rebuilding them in order avoids collisions with load-time publications.
    private static void Append(JArray target, string text, string publishedAt) => target.Add(new JObject
    {
        ["id"] = "message_" + (target.Count + 1).ToString(CultureInfo.InvariantCulture),
        ["published_at_utc"] = publishedAt, ["text"] = text
    });
}
