using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Reads only rows and signature families already exposed by the open contact-information window.
internal static class BattleContactEvidenceReader
{
    internal static JObject Capture(object signatures, object metadata, Func<string, string> displayName)
    {
        var visible = Rows(Required(signatures, "Signatures"))
            .SelectMany(c => Bits(Required(c, "Family"))).ToHashSet();
        var rows = Rows(Required(signatures, "Candidates")).Select(row => new
        {
            Model = row,
            Features = Bits(Required(Required(row, "DetectedReference"), "Item2")).Where(visible.Contains).ToArray()
        }).ToArray();
        var displayed = rows.SelectMany(row => row.Features).ToHashSet();
        var observed = rows.Length == 0 ? new HashSet<int>() :
            Bits(Required(Required(rows[0].Model, "DetectedReference"), "Item1")).Where(displayed.Contains).ToHashSet();
        JObject Feature(int id)
        {
            var key = Lookup(Required(metadata, "SignatureIdMap"), id);
            return new JObject
            {
                ["reference_key"] = key,
                ["family"] = Lookup(Required(metadata, "FamilyMap"), key),
                ["display_name"] = displayName(key)
            };
        }
        var candidates = new JArray();
        foreach (var row in rows)
        {
            var expected = row.Features;
            candidates.Add(new JObject
            {
                ["display_name"] = (string)Required(row.Model, "ClassName"),
                ["reference_key"] = (string)Required(row.Model, "ClassRef"),
                ["matching_features"] = new JArray(expected.Where(observed.Contains).Select(Feature)),
                ["unconfirmed_features"] = new JArray(expected.Where(id => !observed.Contains(id)).Select(Feature))
            });
        }
        return new JObject
        {
            ["observed_features"] = new JArray(observed.OrderBy(i => i).Select(Feature)),
            ["candidates"] = candidates,
            ["candidates_truncated"] = (bool)Required(signatures, "Truncated"),
            ["undisplayed_candidate_count"] = (int)Required(signatures, "NumTruncated")
        };
    }

    private static IEnumerable<int> Bits(object signature)
    {
        // FixedList's interface enumerators throw; the game supports Length and indexed reads.
        var blocks = Required(signature, "Bits");
        var length = (int)Required(blocks, "Length");
        var item = blocks.GetType().GetProperty("Item", new[] { typeof(int) })
            ?? throw new MissingMemberException(blocks.GetType().FullName, "Item");
        for (var block = 0; block < length; block++)
        {
            var bits = (ulong)item.GetValue(blocks, new object[] { block })!;
            for (var bit = 0; bit < 64; bit++)
                if ((bits & (1UL << bit)) != 0) yield return block * 64 + bit;
        }
    }
    private static string Lookup(object map, object key) =>
        (string)(((IDictionary)map)[key] ?? throw new InvalidOperationException("A displayed signature reference is unavailable."));
}
