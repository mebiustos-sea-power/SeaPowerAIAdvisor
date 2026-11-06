using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Owns Crew's save format and keeps restored data available while the feature or its UI is inactive.
internal sealed class CrewSaveData
{
    private readonly SaveDataFormat format;
    private CrewSavedState? saved;
    private CrewChatter? live;
    internal CrewSaveData(SaveDataFormat? format = null) => this.format = format ?? new SaveDataFormat(1, 1);
    internal void Reset() { live = null; saved = null; }
    internal bool IsAttached(CrewChatter chatter) => ReferenceEquals(live, chatter);
    internal void Attach(CrewChatter chatter, double now)
    {
        if (live != null) throw new InvalidOperationException("Crew save data already has a live session.");
        if (saved != null) chatter.Restore(saved, now);
        live = chatter; saved = null;
    }
    internal void Detach(CrewChatter chatter)
    {
        // A previous load's late UI cleanup must not replace the new load's restored data.
        if (!ReferenceEquals(live, chatter)) return;
        saved = chatter.CaptureSaveState(); live = null;
    }
    internal JObject? Capture()
    {
        var state = live?.CaptureSaveState() ?? saved;
        if (state == null || state.IsEmpty) return null;
        return new JObject
        {
            ["version"] = format.CurrentVersion, ["paused"] = state.Paused,
            ["rank_order"] = new JObject { ["before"] = state.BeforeVotes, ["after"] = state.AfterVotes,
                ["invalid"] = state.InvalidVotes, ["before_name"] = state.RankBeforeName },
            ["people"] = new JArray(state.People.OrderBy(p => p.UnitId, StringComparer.Ordinal).Select(p => new JObject
            {
                ["unit_id"] = p.UnitId, ["enabled"] = p.Enabled,
                ["name"] = p.Person.Name, ["rank"] = p.Person.Rank, ["personality"] = p.Person.Personality,
                ["portrait_id"] = p.Person.PortraitId, ["character_id"] = p.Person.RegisteredCharacterId
            }))
        };
    }
    internal SaveDataUpgradeResult? Restore(JToken? source)
    {
        if (source == null) { Reset(); return null; }
        if (source is not JObject document) throw new FormatException("Invalid Crew save data.");
        var result = format.Upgrade(document);
        if (result.Document == null) { Reset(); return result; }
        var value = result.Document;
        if (value["rank_order"] is not JObject order || value["people"] is not JArray rows)
            throw new FormatException("Invalid saved Crew state.");
        var people = new List<CrewAssignment>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in rows)
        {
            if (token is not JObject row) throw new FormatException("Invalid saved Crew assignment.");
            var id = Text(row, "unit_id");
            if (!id.StartsWith("unit:", StringComparison.Ordinal) ||
                !int.TryParse(id.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                id != "unit:" + number.ToString(CultureInfo.InvariantCulture) || !ids.Add(id))
                throw new FormatException("Invalid or duplicate saved Crew unit ID.");
            var person = new CrewPerson(Text(row, "name"), Text(row, "rank"), Text(row, "personality"),
                OptionalText(row, "portrait_id"), OptionalText(row, "character_id"));
            people.Add(new CrewAssignment(id, person, Flag(row, "enabled")));
        }
        var before = Count(order, "before"); var after = Count(order, "after"); var beforeName = Flag(order, "before_name");
        if (before != after && beforeName != (before > after)) throw new FormatException("Inconsistent saved Crew rank order.");
        var restored = new CrewSavedState(people, Flag(value, "paused"), before, after, Count(order, "invalid"), beforeName);
        live = null; saved = restored;
        return null;
    }
    private static string Text(JObject value, string key) => value[key]?.Type == JTokenType.String
        ? (string)value[key]! : throw new FormatException("Invalid saved Crew field: " + key);
    private static string? OptionalText(JObject value, string key) => value[key] == null || value[key]!.Type == JTokenType.Null ? null : Text(value, key);
    private static bool Flag(JObject value, string key) => value[key]?.Type == JTokenType.Boolean
        ? (bool)value[key]! : throw new FormatException("Invalid saved Crew flag: " + key);
    private static int Count(JObject value, string key) => value[key]?.Type == JTokenType.Integer &&
        int.TryParse(value[key]!.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
        ? count : throw new FormatException("Invalid saved Crew count: " + key);
}
