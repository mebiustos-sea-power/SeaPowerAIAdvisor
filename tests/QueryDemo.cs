using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;
using static QueryTests;

internal static class QueryDemo
{
    internal static void Run(string campaign, string save, string definition, string output)
    {
        var snapshot = SituationTests.CheckSave(Assembly.LoadFrom(campaign), save, definition);
        var session = new AdvisorQuerySession(snapshot, "ja", "勝利のために優先すべき行動は？");
        var steps = new JArray();
        JObject Step(params JObject[] queries)
        {
            var request = Request(session, queries);
            var response = Call(session, queries);
            steps.Add(new JObject { ["request"] = request, ["response"] = response });
            return response;
        }
        var listed = Step(new JObject { ["query"] = "list_forces", ["limit"] = 3 });
        var forceIds = new JArray(listed["results"]![0]!["data"]!["items"]!.Take(2).Select(f => f["id"]!.DeepClone()));
        var ships = Step(new JObject { ["query"] = "list_ships", ["force_ids"] = forceIds, ["limit"] = 5 });
        var shipIds = new JArray(ships["results"]![0]!["data"]!["items"]!.Take(2).Select(s => s["id"]!.DeepClone()));
        if (shipIds.Count > 0)
            Step(Q("get_ships", "ids", shipIds), Q("get_forces", "ids", forceIds));
        var baseIndex = snapshot.Victory.Sides.Single(s => s.Side == snapshot.Side).Objectives
            .First(i => snapshot.Victory.Bases[i].OwnerSide != snapshot.Side);
        var related = Step(Q("get_base_observations", "ids", new JArray(SnapshotIds.Base(baseIndex))),
            new JObject { ["query"] = "list_contacts", ["limit"] = 2 }, new JObject { ["query"] = "list_reports", ["limit"] = 3 });
        var report = related["results"]![2]!["data"]!["items"]!.FirstOrDefault();
        if (report != null) Step(Q("get_reports", "ids", new JArray(report["id"]!.DeepClone())));
        var initial = session.InitialInput();
        using var fullText = new StringReader(AdvisorInputBuilder.Build(snapshot, "ja", "勝利のために優先すべき行動は？"));
        using var fullReader = new JsonTextReader(fullText) { DateParseHandling = DateParseHandling.None };
        var fullBytes = JsonPayload.Bytes(JObject.Load(fullReader));
        var trace = new JObject
        {
            ["demo_mode"] = "offline_scripted_queries",
            ["notice"] = "No AI, API key or network is used. This is a scripted demonstration against one captured player-known snapshot. Own ship catalog/ammunition services use test stubs.",
            ["initial_input"] = initial, ["steps"] = steps,
            ["summary"] = new JObject
            {
                ["rounds_used"] = session.RoundsUsed,
                ["initial_compact_utf8_bytes"] = JsonPayload.Bytes(initial),
                ["successful_payload_compact_utf8_bytes"] = session.ReturnedBytes,
                ["full_diagnostic_compact_utf8_bytes"] = fullBytes
            }
        };
        File.WriteAllText(output, trace.ToString(Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine("Offline query demo: " + output);
        Console.WriteLine(trace["summary"]!.ToString(Formatting.None));
    }
}
