using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// An appointment's vote is transient; the session retains the person and shared vote totals.
internal sealed class CrewAppointment
{
    internal CrewPerson Person { get; }
    internal bool? RankBeforeName { get; }
    internal CrewAppointment(CrewPerson person, string? nameWithRank)
    {
        Person = person;
        if (string.IsNullOrWhiteSpace(nameWithRank)) return;
        var form = nameWithRank!.Trim();
        var before = form.StartsWith(person.Rank, StringComparison.OrdinalIgnoreCase);
        var after = form.EndsWith(person.Rank, StringComparison.OrdinalIgnoreCase);
        RankBeforeName = before == after ? null : before;
    }
}
internal sealed class CrewSubmission
{
    internal string Id { get; }
    internal CrewPerson Person { get; }
    internal string Text { get; }
    internal CrewSubmission(string id, CrewPerson person, string text) { Id = id; Person = person; Text = text; }
}
internal sealed class CrewGeneration
{
    internal IReadOnlyDictionary<string, CrewAppointment> Appointments { get; }
    internal IReadOnlyList<CrewSubmission> Remarks { get; }
    internal CrewGeneration(IReadOnlyDictionary<string, CrewAppointment> appointments, IEnumerable<CrewSubmission> remarks)
    { Appointments = new Dictionary<string, CrewAppointment>(appointments); Remarks = Array.AsReadOnly(remarks.ToArray()); }
}

// A detached job is either an appointment batch or a speech batch. Results publish only after generation completes.
internal sealed class CrewBatch
{
    internal const string ToolName = "submit_chatter";
    internal const int MaximumSpeakers = 2;
    internal const int MaximumAppointments = 32;
    private readonly JObject data;
    private readonly IReadOnlyDictionary<string, CrewPerson?> people;
    private readonly CrewAppointments? appointments;
    internal JObject Snapshot => (JObject)data.DeepClone();
    internal bool HasAppointments => appointments != null;
    internal CrewBatch(JObject data, IReadOnlyDictionary<string, CrewPerson?> people, IEnumerable<string> appointments, bool preferExchange = false,
        IReadOnlyDictionary<string, CrewCharacter>? characters = null, IReadOnlyDictionary<string, CrewCharacter[]>? automaticCharacters = null)
    {
        this.data = (JObject)data.DeepClone(); this.data["prefer_exchange"] = preferExchange;
        this.people = new Dictionary<string, CrewPerson?>(people);
        var ids = appointments.ToArray();
        if (ids.Length > 0) this.appointments = new CrewAppointments(this.data, ids, characters, automaticCharacters);
        else if (characters?.Count > 0 || automaticCharacters?.Count > 0) throw new ArgumentException("Characters supplied without appointments.");
        if (ids.Length > 0 && people.Count > 0 || people.Values.Any(p => p == null)) throw new ArgumentException("Appointment and speech jobs must be separate.");
    }
    internal async Task<CrewGeneration> GenerateAsync(string prompt, Func<AiProviderInput, Task<AiReply>> send, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (appointments != null) return await appointments.GenerateAsync(prompt, send, cancellation).ConfigureAwait(false);
        var reply = await send(Input(prompt)).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return Read(reply);
    }
    internal AiProviderInput Input(string connectionPrompt)
    {
        if (appointments != null) throw new InvalidOperationException("Appointments use staged generation.");
        var exchange = (bool)data["prefer_exchange"]! ?
            "Create a short exchange: one speaker voices a feeling about the supplied situation; a second responds to that person with acknowledgment, empathy or a contrasting feeling, not advice, orders or new factual claims. If no natural exchange fits, use independent remarks or silence. " : "";
        var instruction = "Use reply_language for remarks. " +
            "Choose which, if any, of the input speakers would naturally speak now; if empty, return remarks: []. Use their existing personality. " + exchange +
            "Generate only spoken dialogue, without private thoughts or narration. Use one brief in-character sentence, occasionally two: a restrained feeling, concern, or impression rather than a status summary or tactical advice. " +
            "Let personality shape the remark; feelings are fictional, but their factual basis must come from the supplied player-known situation. " +
            "Silence is preferable to filler or repetition; submit an empty remarks array when no speaker has a worthwhile remark. " +
            "Use recent_remarks to avoid repetition, not as current observations. Make this response self-contained. Shared fleet contacts do not prove personal detection. active_threat_alerts are current warnings, not hits or sensor failure; illumination alone does not establish a missile launch. " +
            "air_operations flags include handling and waiting, not completed launches or recoveries. " +
            "posture states allegiance: enemy is confirmed hostile, unknown is undetermined. class_identified describes only class/type recognition. " +
            "Missing values are unknown; ammunition counts are inventory, not launch readiness, and launched_weapons counts are current weapons, not new launches or hits. Never invent events, damage, detections, or mission completion, or claim or announce actions or orders. " +
            "Describe unit facts only for the speaker's own unit or, for leaders, its listed formation members. " +
            "Use submit_chatter once, without questions or other commentary.";
        var speakerId = new JObject { ["type"] = "string", ["description"] = "A unit ID from input speakers." };
        if (people.Count > 0) speakerId["enum"] = new JArray(people.Keys);
        var input = (JObject)data.DeepClone(); input.Remove("appointments");
        return CrewProtocol.Input(input, instruction, connectionPrompt, ToolName, "Submit optional spoken remarks in display order; an empty remarks array means silence.", "remarks",
            CrewProtocol.Rows(new JObject { ["id"] = speakerId, ["text"] = CrewProtocol.Text("The person's remark, in display order within this response.") }, Math.Min(MaximumSpeakers, people.Count)));
    }
    internal CrewGeneration Read(AiReply reply)
    {
        if (appointments != null) throw new InvalidOperationException("Appointments use staged generation.");
        var rows = CrewProtocol.Read(reply, ToolName, "remarks", Math.Min(MaximumSpeakers, people.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal); var remarks = new List<CrewSubmission>();
        foreach (var item in rows)
        {
            if (item is not JObject row || row.Count != 2) throw new CrewException("invalid_response");
            var key = CrewProtocol.Text(row, "id", 80);
            if (!people.TryGetValue(key, out var person) || !seen.Add(key)) throw new CrewException("invalid_response");
            if (row["text"]?.Type != JTokenType.String) throw new CrewException("invalid_response");
            var value = ((string)row["text"]!).Trim();
            if (value.Length == 0 || value.Length > 1200 || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new CrewException("invalid_response");
            remarks.Add(new CrewSubmission(key, person!, value));
        }
        return new CrewGeneration(new Dictionary<string, CrewAppointment>(), remarks);
    }
}
