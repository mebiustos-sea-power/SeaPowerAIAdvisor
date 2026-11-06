using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

// Lifecycle fixtures describe desired people. Feed detached mock replies through the real stage parsers.
internal static class CrewTestGeneration
{
    internal static CrewBatch Appointments(params string[] ids) => new(new JObject
    {
        ["reply_language"] = "en", ["appointment_context"] = new JObject { ["captured_at_utc"] = "1985-06-26T20:57:00.0000000" },
        ["appointments"] = new JArray(ids.Select(id => new JObject { ["id"] = id, ["nation"] = "US", ["role"] = "captain", ["kind"] = "vessel" }))
    }, new Dictionary<string, CrewPerson?>(), ids);
    internal static CrewGeneration Read(CrewBatch batch, AiReply desired)
    {
        var call = desired.Calls.Count == 1 ? desired.Calls[0] : null;
        return batch.GenerateAsync("", input =>
        {
            if (call == null || call.Name != CrewBatch.ToolName) return Task.FromResult(desired);
            var name = (string)input.Tools.Single()["name"]!; var appointed = (JArray)call.Arguments["appointments"]!; JObject arguments;
            if (name == "assess_crew_fit")
                arguments = new JObject { ["appointments"] = new JArray(input.Data["registered_characters"]!.Select(c => new JObject
                { ["character_id"] = c["id"]!.DeepClone(), ["reason"] = "Fixture eligibility.", ["unit_ids"] = c["unit_ids"]!.DeepClone() })) };
            else if (name == "select_crew")
                arguments = new JObject { ["appointments"] = new JArray(appointed.OfType<JObject>().Select(c => new JObject(c.Properties().Where(p => p.Name is "id" or "character_id" or "rank").Select(p => new JProperty(p.Name, p.Value.DeepClone()))))) };
            else if (name == "submit_crew_people")
                arguments = new JObject { ["appointments"] = new JArray(appointed.OfType<JObject>().Select(c => new JObject(c.Properties().Where(p => p.Name is not ("character_id" or "rank")).Select(p => new JProperty(p.Name, p.Value.DeepClone()))))) };
            else arguments = new JObject { ["remarks"] = call.Arguments["remarks"]!.DeepClone() };
            return Task.FromResult(new AiReply("", new[] { new AiToolCall("fixture", name, arguments) }, desired.TotalTokens));
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
