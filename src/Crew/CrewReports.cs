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
    internal bool CanReport { get; }
    internal CrewTarget(string id, string name, string nation, string role, IReadOnlyList<int> members, bool canReport)
    { Id = id; Name = name; Nation = nation; Role = role; Members = members; CanReport = canReport; }
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
        internal long LastConsidered;
        internal State(CrewTarget target) => Target = target;
    }
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CrewPerson> people = new(StringComparer.Ordinal);
    private readonly List<CrewReport> history = new();
    private readonly Queue<CrewReport> pending = new();
    private readonly Random random;
    private readonly Action<Exception> log;
    private CancellationTokenSource? cancellation;
    private Task<CrewGeneration>? task;
    private (State State, int Revision, CrewTarget Target)[] active = Array.Empty<(State, int, CrewTarget)>();
    private bool retired, speechInFlight;
    private double startedAt, nextOpportunity = double.PositiveInfinity, nextAppointments = double.PositiveInfinity;
    private long selectionRound;
    internal string? Error { get; private set; }
    internal bool Busy => task != null;
    internal bool Paused { get; private set; }
    internal bool HasEnabledTargets => states.Values.Any(s => s.Enabled);
    private bool HasReporters => states.Values.Any(s => s.Enabled && s.Target.CanReport);
    private bool NeedsAppointment => states.Values.Any(s => s.Enabled && !people.ContainsKey(s.Target.Id));
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
            { if (state.Target.Name != target.Name || state.Target.CanReport != target.CanReport || !state.Target.Members.SequenceEqual(target.Members)) Version++; state.Target = target; }
            else { states.Add(target.Id, new State(target)); Version++; }
        if (!HasEnabledTargets)
        {
            if (task != null || pending.Count > 0) CancelGeneration();
            nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity;
        }
        else if (!NeedsAppointment) nextAppointments = double.PositiveInfinity;
    }
    // The caller commits a complete selection; editing checkboxes never changes this state.
    internal void ApplyTargets(IReadOnlyCollection<string> selected, double now)
    {
        var ids = new HashSet<string>(selected, StringComparer.Ordinal);
        if (ids.Count != selected.Count || ids.Any(id => !states.ContainsKey(id))) throw new ArgumentException("Invalid Reports selection.", nameof(selected));
        if (states.Values.All(s => s.Enabled == ids.Contains(s.Target.Id))) return;
        var wasActive = HasEnabledTargets;
        foreach (var state in states.Values)
        {
            var value = ids.Contains(state.Target.Id); if (state.Enabled == value) continue;
            state.Enabled = value; state.Revision++; state.LastConsidered = 0;
        }
        if (!HasEnabledTargets) CancelGeneration();
        else
        {
            if (!wasActive) nextOpportunity = now + 1;
            if (NeedsAppointment) nextAppointments = Math.Min(nextAppointments, now + 1);
            else nextAppointments = double.PositiveInfinity;
        }
        Error = null; Version++;
    }
    internal void SetPaused(bool value, double now)
    {
        if (Paused == value) return;
        Paused = value;
        if (value) CancelGeneration();
        else
        {
            nextOpportunity = HasEnabledTargets ? now + 1 : double.PositiveInfinity;
            nextAppointments = NeedsAppointment ? now + 1 : double.PositiveInfinity;
        }
        Version++;
    }
    internal void Tick(double now, CrewSettings settings, string language, Func<IReadOnlyList<CrewTarget>, JObject> capture,
        Func<CrewBatch, string, CancellationToken, Task<CrewGeneration>> generate)
    {
        if (retired || Paused) return;
        if (!HasReporters) nextOpportunity = double.PositiveInfinity;
        else if (double.IsPositiveInfinity(nextOpportunity) && !speechInFlight) nextOpportunity = now + 1;
        if (task?.IsCompleted == true)
        {
            var completed = task; task = null; var work = active; var hadSpeech = speechInFlight;
            active = Array.Empty<(State, int, CrewTarget)>(); speechInFlight = false;
            bool Valid((State State, int Revision, CrewTarget Target) entry) =>
                states.TryGetValue(entry.Target.Id, out var current) && ReferenceEquals(current, entry.State) && current.Enabled &&
                current.Revision == entry.Revision && now - startedAt <= 180;
            try
            {
                var result = completed.GetAwaiter().GetResult();
                foreach (var appointment in result.Appointments)
                {
                    var entry = work.Single(w => w.Target.Id == appointment.Key);
                    if (Valid(entry) && !people.ContainsKey(appointment.Key)) people.Add(appointment.Key, appointment.Value);
                }
                foreach (var submission in result.Reports)
                {
                    var entry = work.Single(w => w.Target.Id == submission.Id);
                    if (!Valid(entry) || !entry.State.Target.CanReport || !entry.State.Target.Members.SequenceEqual(entry.Target.Members)) continue;
                    var bound = new CrewSubmission(submission.Id, people[submission.Id], submission.Text);
                    var report = new CrewReport(entry.Target, bound, now + 120, entry.Revision);
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
                // Selection changes may already have scheduled fresh work while this request was in flight.
                if (!HasEnabledTargets) { nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity; }
                else
                {
                    if (hadSpeech && double.IsPositiveInfinity(nextOpportunity))
                        nextOpportunity = now + 60 * settings.IntervalMinutes * (0.75 + random.NextDouble() * 0.5);
                    if (!NeedsAppointment) nextAppointments = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(nextAppointments)) nextAppointments = now + 1;
                }
                cancellation?.Dispose(); cancellation = null; Version++;
            }
        }
        if (task != null || now < Math.Min(nextOpportunity, nextAppointments)) return;
        var appointments = now < nextAppointments ? Array.Empty<State>() : states.Values.Where(s => s.Enabled && !people.ContainsKey(s.Target.Id)).Take(CrewBatch.MaximumAppointments).ToArray();
        var candidates = now < nextOpportunity ? Array.Empty<State>() : states.Values.Where(s => s.Enabled && s.Target.CanReport && (people.ContainsKey(s.Target.Id) || appointments.Contains(s)))
            .OrderBy(s => s.LastConsidered).ThenBy(_ => random.NextDouble()).Take(4).ToArray();
        if (appointments.Length == 0 && candidates.Length == 0) return;
        if (settings.ConnectionId == null) { foreach (var s in states.Values) s.Enabled = false; Error = "connection_required"; CancelGeneration(); Version++; return; }
        if (appointments.Length > 0) nextAppointments = double.PositiveInfinity;
        if (candidates.Length > 0)
        {
            nextOpportunity = double.PositiveInfinity; var round = ++selectionRound;
            foreach (var candidate in candidates) candidate.LastConsidered = round;
        }
        var data = candidates.Length == 0 ? new JObject() : capture(candidates.Select(s => s.Target).ToArray());
        data["reply_language"] = language;
        JObject Identity(State state) => new() { ["id"] = state.Target.Id, ["display_name"] = state.Target.Name, ["nation"] = state.Target.Nation, ["role"] = state.Target.Role };
        data["appointments"] = new JArray(appointments.Select(Identity));
        data["reporters"] = new JArray(candidates.Select(s =>
        {
            var row = Identity(s); var person = Person(s.Target.Id);
            row["member_unit_ids"] = new JArray(s.Target.Members.Select(id => "unit:" + id));
            row["person"] = person == null ? JValue.CreateNull() : new JObject { ["name"] = person.Name, ["personality"] = person.Personality };
            return row;
        }));
        if (candidates.Length > 0) data["recent_remarks"] = new JArray(history.TakeLast(3).Select(r => new JObject { ["id"] = r.Target.Id, ["text"] = r.Submission.Text }));
        active = appointments.Concat(candidates).Distinct().Select(s => (s, s.Revision, s.Target)).ToArray(); startedAt = now; speechInFlight = candidates.Length > 0;
        cancellation = new CancellationTokenSource();
        var batch = new CrewBatch(data, candidates.ToDictionary(s => s.Target.Id, s => Person(s.Target.Id)), appointments.Select(s => s.Target.Id));
        var token = cancellation.Token; var connectionId = settings.ConnectionId;
        task = Task.Run(() => generate(batch, connectionId, token), token); Version++;
    }
    internal CrewReport? NextNotification(double now)
    {
        if (Paused) return null;
        while (pending.Count > 0)
        {
            var report = pending.Dequeue();
            if (report.ExpiresAt > now && states.TryGetValue(report.Target.Id, out var state) && state.Enabled && state.Target.CanReport &&
                state.Revision == report.Revision && state.Target.Members.SequenceEqual(report.Target.Members)) return report;
        }
        return null;
    }
    private void CancelGeneration()
    {
        cancellation?.Cancel();
        var oldTask = task; task = null; active = Array.Empty<(State, int, CrewTarget)>(); speechInFlight = false;
        var oldCancellation = cancellation; cancellation = null;
        if (oldTask == null) oldCancellation?.Dispose();
        else _ = oldTask.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); oldCancellation?.Dispose(); }, TaskScheduler.Default);
        foreach (var state in states.Values) state.Revision++;
        pending.Clear(); nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity;
    }
    internal void Suspend()
    {
        CancelGeneration(); foreach (var state in states.Values) state.Enabled = false;
        Paused = false; Error = null; Version++;
    }
    public void Dispose() { retired = true; Suspend(); states.Clear(); people.Clear(); history.Clear(); }
}
