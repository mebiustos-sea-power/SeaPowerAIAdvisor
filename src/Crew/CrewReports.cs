using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Crew;

internal sealed class CrewTarget
{
    internal string Id { get; }
    internal string Name { get; }
    internal string Nation { get; }
    internal string Role { get; }
    internal IReadOnlyList<int> Members { get; }
    internal CrewTarget(string id, string name, string nation, string role, IReadOnlyList<int> members)
    { Id = id; Name = name; Nation = nation; Role = role; Members = members; }
}
internal sealed class CrewReport
{
    internal CrewTarget Target { get; }
    internal CrewSubmission Submission { get; }
    internal DateTime ReceivedAt { get; } = DateTime.Now;
    internal double ExpiresAt { get; }
    internal int Revision { get; }
    internal CrewReport(CrewTarget target, CrewSubmission submission, double expiresAt, int revision)
    { Target = target; Submission = submission; ExpiresAt = expiresAt; Revision = revision; }
}

// Session-owned scheduling uses only the caller's monotonic real-time clock, never simulation time or pause state.
internal sealed class CrewReports : IDisposable
{
    private sealed class State
    {
        internal CrewTarget Target;
        internal bool Enabled;
        internal int Revision;
        internal double Due;
        internal State(CrewTarget target) => Target = target;
    }
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CrewPerson> people = new(StringComparer.Ordinal);
    private readonly List<CrewReport> history = new();
    private readonly Queue<CrewReport> pending = new();
    private readonly Random random;
    private readonly Action<Exception> log;
    private CancellationTokenSource? cancellation;
    private Task<IReadOnlyList<CrewSubmission>>? task;
    private (State State, int Revision, CrewTarget Target)[] active = Array.Empty<(State, int, CrewTarget)>();
    private bool retired;
    private double startedAt;
    internal string? Error { get; private set; }
    internal bool Busy => task != null;
    internal int Version { get; private set; }
    internal IReadOnlyList<CrewReport> History => history;
    internal IReadOnlyList<CrewTarget> Targets => states.Values.Select(s => s.Target).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal CrewPerson? Person(string id) => people.TryGetValue(id, out var person) ? person : null;
    internal bool Enabled(string id) => states.TryGetValue(id, out var s) && s.Enabled;
    internal CrewReports(Action<Exception> log, Random? random = null) { this.log = log; this.random = random ?? new Random(); }
    internal void Refresh(IReadOnlyList<CrewTarget> targets)
    {
        var ids = new HashSet<string>(targets.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var id in states.Keys.Where(id => !ids.Contains(id)).ToArray()) { states.Remove(id); Version++; }
        foreach (var target in targets)
            if (states.TryGetValue(target.Id, out var state))
            { if (state.Target.Name != target.Name || !state.Target.Members.SequenceEqual(target.Members)) Version++; state.Target = target; }
            else { states.Add(target.Id, new State(target)); Version++; }
    }
    internal void SetEnabled(string id, bool enabled, double now)
    {
        if (!states.TryGetValue(id, out var state)) return;
        state.Enabled = enabled; state.Revision++; state.Due = now + 1; Error = null; Version++;
    }
    internal void Tick(double now, CrewSettings settings, string language, Func<IReadOnlyList<CrewTarget>, JObject> capture,
        Func<CrewBatch, string, CancellationToken, Task<IReadOnlyList<CrewSubmission>>> generate)
    {
        if (retired) return;
        if (task?.IsCompleted == true)
        {
            var completed = task; task = null; var work = active; active = Array.Empty<(State, int, CrewTarget)>();
            try
            {
                foreach (var submission in completed.GetAwaiter().GetResult())
                {
                    var entry = work.Single(w => w.Target.Id == submission.Id);
                    if (!states.TryGetValue(submission.Id, out var current) || !ReferenceEquals(current, entry.State) ||
                        !current.Enabled || current.Revision != entry.Revision || !current.Target.Members.SequenceEqual(entry.Target.Members) || now - startedAt > 180) continue;
                    people[submission.Id] = submission.Person;
                    var report = new CrewReport(entry.Target, submission, now + 120, entry.Revision);
                    history.Add(report); if (history.Count > 200) history.RemoveAt(0);
                    pending.Enqueue(report); while (pending.Count > 16) pending.Dequeue();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                log(e);
                foreach (var entry in work)
                    if (states.TryGetValue(entry.Target.Id, out var current) && ReferenceEquals(current, entry.State) && current.Enabled && current.Revision == entry.Revision)
                    { current.Enabled = false; Error = e is CrewException crew ? crew.Code : e is Shared.AiConnectionException ai ? ai.Code : "unexpected_error"; }
            }
            finally
            {
                foreach (var entry in work) if (states.TryGetValue(entry.Target.Id, out var current) && ReferenceEquals(current, entry.State) && current.Revision == entry.Revision)
                    current.Due = now + 60 * (settings.MinimumMinutes + random.NextDouble() * (settings.MaximumMinutes - settings.MinimumMinutes));
                cancellation?.Dispose(); cancellation = null; Version++;
            }
        }
        if (task != null) return;
        var due = states.Values.Where(s => s.Enabled && s.Due <= now).OrderBy(s => s.Due).Take(4).ToArray();
        if (due.Length == 0) return;
        if (settings.ConnectionId == null) { foreach (var s in due) s.Enabled = false; Error = "connection_required"; Version++; return; }
        var data = capture(due.Select(s => s.Target).ToArray());
        data["reply_language"] = language;
        data["reporters"] = new JArray(due.Select(s =>
        {
            var person = Person(s.Target.Id);
            return new JObject { ["id"] = s.Target.Id, ["display_name"] = s.Target.Name,
                ["nation"] = s.Target.Nation, ["role"] = s.Target.Role, ["member_unit_ids"] = new JArray(s.Target.Members.Select(id => "unit:" + id)),
                ["person"] = person == null ? JValue.CreateNull() : new JObject { ["name"] = person.Name, ["personality"] = person.Personality } };
        }));
        active = due.Select(s => (s, s.Revision, s.Target)).ToArray(); startedAt = now;
        cancellation = new CancellationTokenSource();
        var batch = new CrewBatch(data, due.ToDictionary(s => s.Target.Id, s => Person(s.Target.Id)));
        var token = cancellation.Token; var connectionId = settings.ConnectionId;
        task = Task.Run(() => generate(batch, connectionId, token), token);
        Version++;
    }
    internal CrewReport? NextNotification(double now)
    {
        while (pending.Count > 0)
        {
            var report = pending.Dequeue();
            if (report.ExpiresAt > now && states.TryGetValue(report.Target.Id, out var state) && state.Enabled && state.Revision == report.Revision) return report;
        }
        return null;
    }
    internal void Suspend()
    {
        cancellation?.Cancel();
        var oldTask = task; task = null; active = Array.Empty<(State, int, CrewTarget)>();
        var oldCancellation = cancellation; cancellation = null;
        if (oldTask == null) oldCancellation?.Dispose();
        else _ = oldTask.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); oldCancellation?.Dispose(); }, TaskScheduler.Default);
        foreach (var state in states.Values) { state.Enabled = false; state.Revision++; }
        pending.Clear(); Error = null; Version++;
    }
    public void Dispose() { retired = true; Suspend(); states.Clear(); people.Clear(); history.Clear(); }
}
