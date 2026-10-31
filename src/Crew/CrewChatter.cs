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
    internal bool CanSpeak { get; }
    internal CrewTarget(string id, string name, string nation, string role, IReadOnlyList<int> members, bool canSpeak)
    { Id = id; Name = name; Nation = nation; Role = role; Members = members; CanSpeak = canSpeak; }
}
internal sealed class CrewRemark
{
    internal CrewTarget Target { get; }
    internal CrewSubmission Submission { get; }
    internal DateTime ReceivedAt { get; } = DateTime.Now;
    internal double ExpiresAt { get; }
    internal int Revision { get; }
    internal int InformationRevision { get; }
    internal CrewRemark(CrewTarget target, CrewSubmission submission, double expiresAt, int revision, int informationRevision)
    { Target = target; Submission = submission; ExpiresAt = expiresAt; Revision = revision; InformationRevision = informationRevision; }
}

// Session-owned scheduling receives monotonic real time and game pause state, never simulation speed.
internal sealed class CrewChatter : IDisposable
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
    private readonly List<CrewRemark> history = new();
    private readonly Queue<CrewRemark> pending = new();
    private readonly Random random;
    private readonly Action<Exception> log;
    private CancellationTokenSource? cancellation;
    private Task<CrewGeneration>? task;
    private (State State, int Revision, CrewTarget Target)[] active = Array.Empty<(State, int, CrewTarget)>();
    private bool retired, speechInFlight;
    private double startedAt, nextOpportunity = double.PositiveInfinity, nextAppointments = double.PositiveInfinity;
    private double gamePauseOffset;
    private double? gamePauseObservedAt;
    private bool gamePaused;
    private long selectionRound;
    private string informationKey = CrewInformation.Default.Key;
    private int informationRevision, activeInformationRevision;
    internal string? Error { get; private set; }
    internal bool Busy => task != null;
    internal CrewUsage Usage { get; } = new();
    internal bool Paused { get; private set; }
    internal bool HasEnabledTargets => states.Values.Any(s => s.Enabled);
    private bool HasSpeakers => states.Values.Any(s => s.Enabled && s.Target.CanSpeak);
    private bool NeedsAppointment => states.Values.Any(s => s.Enabled && !people.ContainsKey(s.Target.Id));
    internal int Version { get; private set; }
    internal IReadOnlyList<CrewRemark> History => history;
    internal IReadOnlyList<CrewTarget> Targets => states.Values.Select(s => s.Target).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal CrewPerson? Person(string id) => people.TryGetValue(id, out var person) ? person : null;
    internal bool Enabled(string id) => states.TryGetValue(id, out var s) && s.Enabled;
    internal CrewChatter(Action<Exception> log, Random? random = null) { this.log = log; this.random = random ?? new Random(); }
    internal void Refresh(IReadOnlyList<CrewTarget> targets)
    {
        var ids = new HashSet<string>(targets.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var id in states.Keys.Where(id => !ids.Contains(id)).ToArray()) { states.Remove(id); Version++; }
        foreach (var target in targets)
            if (states.TryGetValue(target.Id, out var state))
            { if (state.Target.Name != target.Name || state.Target.CanSpeak != target.CanSpeak || !state.Target.Members.SequenceEqual(target.Members)) Version++; state.Target = target; }
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
        if (ids.Count != selected.Count || ids.Any(id => !states.ContainsKey(id))) throw new ArgumentException("Invalid Chatter selection.", nameof(selected));
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
            if (!wasActive) nextOpportunity = OpportunityTime(now) + 1;
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
            Error = null;
            nextOpportunity = HasEnabledTargets ? OpportunityTime(now) + 1 : double.PositiveInfinity;
            nextAppointments = NeedsAppointment ? now + 1 : double.PositiveInfinity;
        }
        Version++;
    }
    private double OpportunityTime(double now) => now - gamePauseOffset -
        (gamePaused && gamePauseObservedAt is double observed ? now - observed : 0);
    internal void Tick(double now, bool simulationPaused, CrewSettings settings, string language, Func<IReadOnlyList<CrewTarget>, JObject> capture,
        Func<CrewBatch, string, CancellationToken, Action<long?>, Task<CrewGeneration>> generate)
    {
        if (retired) return;
        // Exclude paused wall time only from speech scheduling; request freshness keeps using now.
        if (gamePaused && gamePauseObservedAt is double previous) gamePauseOffset += now - previous;
        gamePauseObservedAt = now; gamePaused = simulationPaused;
        var opportunityNow = OpportunityTime(now);
        if (informationKey != settings.Information.Key)
        {
            informationKey = settings.Information.Key; informationRevision++;
        }
        if (Paused) return;
        if (!HasSpeakers) nextOpportunity = double.PositiveInfinity;
        else if (double.IsPositiveInfinity(nextOpportunity) && !speechInFlight) nextOpportunity = opportunityNow + 1;
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
                foreach (var submission in result.Remarks)
                {
                    var entry = work.Single(w => w.Target.Id == submission.Id);
                    if (!Valid(entry) || !entry.State.Target.CanSpeak || !entry.State.Target.Members.SequenceEqual(entry.Target.Members)) continue;
                    var bound = new CrewSubmission(submission.Id, people[submission.Id], submission.Text);
                    var remark = new CrewRemark(entry.Target, bound, now + 120, entry.Revision, activeInformationRevision);
                    history.Add(remark); if (history.Count > 200) history.RemoveAt(0);
                    pending.Enqueue(remark); while (pending.Count > 16) pending.Dequeue();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                log(e);
                Error = e is CrewException crew ? crew.Code : e is Shared.AiConnectionException ai ? ai.Code : "unexpected_error";
                SetPaused(true, now);
            }
            finally
            {
                // Selection changes may already have scheduled fresh work while this request was in flight.
                if (Paused || !HasEnabledTargets) { nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity; }
                else
                {
                    if (hadSpeech && double.IsPositiveInfinity(nextOpportunity))
                        nextOpportunity = opportunityNow + 60 * settings.IntervalMinutes * (0.75 + random.NextDouble() * 0.5);
                    if (!NeedsAppointment) nextAppointments = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(nextAppointments)) nextAppointments = now + 1;
                }
                cancellation?.Dispose(); cancellation = null; Version++;
            }
        }
        if (Paused || task != null || ((simulationPaused || opportunityNow < nextOpportunity) && now < nextAppointments)) return;
        var appointments = now < nextAppointments ? Array.Empty<State>() : states.Values.Where(s => s.Enabled && !people.ContainsKey(s.Target.Id)).Take(CrewBatch.MaximumAppointments).ToArray();
        var candidates = simulationPaused || opportunityNow < nextOpportunity ? Array.Empty<State>() : states.Values.Where(s => s.Enabled && s.Target.CanSpeak && (people.ContainsKey(s.Target.Id) || appointments.Contains(s)))
            .OrderBy(s => s.LastConsidered).ThenBy(_ => random.NextDouble()).Take(4).ToArray();
        if (appointments.Length == 0 && candidates.Length == 0) return;
        if (settings.ConnectionId == null) { Error = "connection_required"; SetPaused(true, now); return; }
        if (appointments.Length > 0) nextAppointments = double.PositiveInfinity;
        if (candidates.Length > 0)
        {
            nextOpportunity = double.PositiveInfinity; var round = ++selectionRound;
            foreach (var candidate in candidates) candidate.LastConsidered = round;
        }
        var data = candidates.Length == 0 ? new JObject() : settings.Information.Filter(capture(candidates.Select(s => s.Target).ToArray()));
        data["reply_language"] = language;
        JObject Identity(State state) => new() { ["id"] = state.Target.Id, ["display_name"] = state.Target.Name, ["nation"] = state.Target.Nation, ["role"] = state.Target.Role };
        data["appointments"] = new JArray(appointments.Select(Identity));
        data["speakers"] = new JArray(candidates.Select(s =>
        {
            var row = Identity(s); var person = Person(s.Target.Id);
            row["member_unit_ids"] = new JArray(s.Target.Members.Select(id => "unit:" + id));
            row["person"] = person == null ? JValue.CreateNull() : new JObject { ["name"] = person.Name, ["personality"] = person.Personality };
            return row;
        }));
        if (candidates.Length > 0) data["recent_remarks"] = new JArray(history.Where(r => r.InformationRevision == informationRevision).TakeLast(3).Select(r => new JObject { ["id"] = r.Target.Id, ["text"] = r.Submission.Text }));
        active = appointments.Concat(candidates).Distinct().Select(s => (s, s.Revision, s.Target)).ToArray(); startedAt = now; speechInFlight = candidates.Length > 0;
        activeInformationRevision = informationRevision;
        cancellation = new CancellationTokenSource();
        var batch = new CrewBatch(data, candidates.ToDictionary(s => s.Target.Id, s => Person(s.Target.Id)), appointments.Select(s => s.Target.Id),
            preferExchange: candidates.Length >= 2 && random.NextDouble() < settings.ExchangeChancePercent / 100.0);
        var token = cancellation.Token; var connectionId = settings.ConnectionId;
        task = Task.Run(() => generate(batch, connectionId, token, Usage.Record), token); Version++;
    }
    internal CrewRemark? NextNotification(double now)
    {
        if (Paused) return null;
        while (pending.Count > 0)
        {
            var remark = pending.Dequeue();
            if (remark.ExpiresAt > now && states.TryGetValue(remark.Target.Id, out var state) && state.Enabled && state.Target.CanSpeak &&
                state.Revision == remark.Revision && state.Target.Members.SequenceEqual(remark.Target.Members)) return remark;
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
