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
    internal string Kind { get; }
    internal string ClassName { get; }
    internal bool FormationLeader { get; }
    internal IReadOnlyList<int> Members { get; }
    internal bool CanSpeak { get; }
    internal CrewTarget(string id, string name, string nation, string role, string kind, string className, bool formationLeader, IReadOnlyList<int> members, bool canSpeak)
    { Id = id; Name = name; Nation = nation; Role = role; Kind = kind; ClassName = className; FormationLeader = formationLeader; Members = members; CanSpeak = canSpeak; }
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
    private enum AppointmentRequest { None, Manual, Automatic, Replacement }
    private sealed class State
    {
        internal CrewTarget Target;
        internal bool Enabled;
        internal AppointmentRequest Appointment;
        internal CrewCharacter? Character;
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
    private readonly CrewCharacterLibrary? characters;
    private CancellationTokenSource? cancellation;
    private Task<CrewGeneration>? task;
    private (State State, int Revision, CrewTarget Target, AppointmentRequest Appointment, CrewCharacter? Character)[] active = Array.Empty<(State, int, CrewTarget, AppointmentRequest, CrewCharacter?)>();
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
    internal int RankBeforeVotes { get; private set; }
    internal int RankAfterVotes { get; private set; }
    internal int RankInvalidVotes { get; private set; }
    internal bool RankBeforeName { get; private set; } = true;
    internal string? RankPosition => people.Count == 0 ? null : RankBeforeName ? "before" : "after";
    internal bool Paused { get; private set; }
    internal bool HasEnabledTargets => states.Values.Any(s => s.Enabled);
    private bool HasSpeakers => states.Values.Any(s => s.Enabled && s.Target.CanSpeak);
    internal bool HasPendingAppointments => states.Values.Any(s => s.Appointment != AppointmentRequest.None);
    internal bool HasUnappointedTargets => states.Values.Any(s => !people.ContainsKey(s.Target.Id));
    internal bool AppointmentRequested(string id) => states.TryGetValue(id, out var s) && s.Appointment != AppointmentRequest.None;
    internal int Version { get; private set; }
    internal IReadOnlyList<CrewRemark> History => history;
    internal IReadOnlyList<CrewTarget> Targets => states.Values.Select(s => s.Target).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal CrewPerson? Person(string id) => people.TryGetValue(id, out var person) ? person : null;
    // UI resources may still be displayed by history; image files belong to the library.
    internal IEnumerable<string?> DisplayPortraitIds => people.Values.Select(p => p.PortraitId).Concat(history.Select(r => r.Submission.Person.PortraitId));
    internal void UpdatePerson(string id, CrewPerson expected, CrewPerson person)
    {
        if (!states.TryGetValue(id, out var state) || !people.TryGetValue(id, out var current)) throw new CrewException("person_unavailable");
        if (!ReferenceEquals(current, expected)) throw new CrewException("person_changed");
        if (current.RegisteredCharacterId != person.RegisteredCharacterId) throw new CrewException("person_invalid");
        state.Appointment = AppointmentRequest.None; state.Character = null; state.Revision++; Version++;
        people[id] = person;
    }
    internal void RequestReappointment(string id, double now)
    {
        if (!states.TryGetValue(id, out var state) || !people.ContainsKey(id)) throw new CrewException("person_unavailable");
        if (Paused || Busy) throw new CrewException("person_busy");
        state.Appointment = AppointmentRequest.Replacement; state.Character = null; state.Revision++;
        nextAppointments = now + 1; Error = null; Version++;
    }
    internal bool Enabled(string id) => states.TryGetValue(id, out var s) && s.Enabled;
    internal CrewChatter(Action<Exception> log, Random? random = null, CrewCharacterLibrary? characters = null)
    { this.log = log; this.random = random ?? new Random(); this.characters = characters; }
    internal bool CanAssignCharacter(string unitId, CrewCharacter character) => states.ContainsKey(unitId) &&
        (character.AllowDuplicates || Person(unitId)?.RegisteredCharacterId == character.Id || !states.Values.Any(s => s.Target.Id != unitId &&
            (Person(s.Target.Id)?.RegisteredCharacterId == character.Id || s.Character?.Id == character.Id)));
    internal void AssignCharacter(string unitId, string characterId, double now)
    {
        if (!states.TryGetValue(unitId, out var state)) throw new CrewException("person_unavailable");
        var character = characters?.Find(characterId) ?? throw new CrewException("character_unavailable");
        if (!CanAssignCharacter(unitId, character)) throw new CrewException("character_in_use");
        if (Busy || !character.IsComplete && Paused) throw new CrewException("person_busy");
        if (!character.IsComplete)
        {
            // Reserve the selected immutable definition. Later library edits affect new requests only.
            state.Character = character; state.Appointment = people.ContainsKey(unitId) ? AppointmentRequest.Replacement : AppointmentRequest.Manual;
            state.Revision++; nextAppointments = Math.Min(nextAppointments, now + 1); Error = null; Version++; return;
        }
        var replacement = people.ContainsKey(unitId);
        people[unitId] = character.Person("", "", ""); state.Character = null; state.Appointment = AppointmentRequest.None;
        if (!replacement) state.Enabled = true;
        state.Revision++; state.LastConsidered = 0; Error = null; Version++;
        if (HasSpeakers && double.IsPositiveInfinity(nextOpportunity)) nextOpportunity = OpportunityTime(now) + 1;
    }
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
            if (speechInFlight) CancelGeneration();
            pending.Clear();
        }
        if (!HasPendingAppointments) nextAppointments = double.PositiveInfinity;
    }
    internal void RequestAppointments(double now) => QueueAppointments(now + 1, AppointmentRequest.Manual);
    private void QueueAppointments(double when, AppointmentRequest request)
    {
        var changed = false;
        foreach (var state in states.Values.Where(s => !people.ContainsKey(s.Target.Id) &&
            (s.Appointment == AppointmentRequest.None || request == AppointmentRequest.Manual && s.Appointment == AppointmentRequest.Automatic)))
        { state.Appointment = request; changed = true; }
        if (!changed) return;
        nextAppointments = Math.Min(nextAppointments, when); Version++;
    }
    // The caller commits a complete selection; editing checkboxes never changes this state.
    internal void ApplyTargets(IReadOnlyCollection<string> selected, double now)
    {
        var ids = new HashSet<string>(selected, StringComparer.Ordinal);
        if (ids.Count != selected.Count || ids.Any(id => !states.ContainsKey(id) || !people.ContainsKey(id))) throw new ArgumentException("Invalid Chatter selection.", nameof(selected));
        if (states.Values.All(s => s.Enabled == ids.Contains(s.Target.Id))) return;
        var wasActive = HasEnabledTargets;
        foreach (var state in states.Values)
        {
            var value = ids.Contains(state.Target.Id); if (state.Enabled == value) continue;
            state.Enabled = value; state.Revision++; state.LastConsidered = 0;
        }
        if (!HasEnabledTargets)
        {
            if (speechInFlight) CancelGeneration();
            pending.Clear();
        }
        else if (!wasActive) nextOpportunity = OpportunityTime(now) + 1;
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
            nextOpportunity = HasSpeakers ? OpportunityTime(now) + 1 : double.PositiveInfinity;
            nextAppointments = HasPendingAppointments ? now + 1 : double.PositiveInfinity;
        }
        Version++;
    }
    private double OpportunityTime(double now) => now - gamePauseOffset -
        (gamePaused && gamePauseObservedAt is double observed ? now - observed : 0);
    private bool HasOpportunities(CrewSettings settings) => HasSpeakers || settings.AutoAppointments && people.Count > 0;
    internal void Tick(double now, bool simulationPaused, CrewSettings settings, string language, Func<IReadOnlyList<CrewTarget>, JObject> capture, Func<string, JObject> captureAppointments,
        Func<CrewBatch, string, CancellationToken, Action<long?>, Task<CrewGeneration>> generate)
    {
        if (retired) return;
        // Speech and automatic checks share the pause-aware clock; request freshness keeps using now.
        if (gamePaused && gamePauseObservedAt is double previous) gamePauseOffset += now - previous;
        gamePauseObservedAt = now; gamePaused = simulationPaused;
        var opportunityNow = OpportunityTime(now);
        if (informationKey != settings.Information.Key)
        {
            informationKey = settings.Information.Key; informationRevision++;
        }
        if (!settings.AutoAppointments)
        {
            foreach (var state in states.Values.Where(s => s.Appointment == AppointmentRequest.Automatic))
            { state.Appointment = AppointmentRequest.None; Version++; }
            if (!HasPendingAppointments) nextAppointments = double.PositiveInfinity;
        }
        if (Paused) return;
        if (!HasOpportunities(settings)) nextOpportunity = double.PositiveInfinity;
        else if (double.IsPositiveInfinity(nextOpportunity) && task == null && !HasPendingAppointments)
            nextOpportunity = opportunityNow + (HasSpeakers ? 1 : 60 * settings.IntervalMinutes);
        if (task?.IsCompleted == true)
        {
            var completed = task; task = null; var work = active; var hadSpeech = speechInFlight;
            var hadAutomaticAppointments = work.Any(w => w.Appointment == AppointmentRequest.Automatic);
            active = Array.Empty<(State, int, CrewTarget, AppointmentRequest, CrewCharacter?)>(); speechInFlight = false;
            bool Valid((State State, int Revision, CrewTarget Target, AppointmentRequest Appointment, CrewCharacter? Character) entry) =>
                states.TryGetValue(entry.Target.Id, out var current) && ReferenceEquals(current, entry.State);
            try
            {
                var result = completed.GetAwaiter().GetResult();
                foreach (var appointment in result.Appointments)
                {
                    var entry = work.Single(w => w.Target.Id == appointment.Key);
                    if (!Valid(entry) || entry.State.Appointment == AppointmentRequest.None || !ReferenceEquals(entry.Character, entry.State.Character)) continue;
                    var replacement = entry.Appointment == AppointmentRequest.Replacement;
                    if ((entry.State.Appointment == AppointmentRequest.Replacement) != replacement || people.ContainsKey(appointment.Key) != replacement) continue;
                    people[appointment.Key] = appointment.Value.Person;
                    entry.State.Appointment = AppointmentRequest.None; entry.State.Character = null;
                    if (!replacement) entry.State.Enabled = true;
                    entry.State.Revision++; entry.State.LastConsidered = 0;
                    if (appointment.Value.RankBeforeName is bool before)
                    { if (before) RankBeforeVotes++; else RankAfterVotes++; }
                    else RankInvalidVotes++;
                }
                if (!hadSpeech && !hadAutomaticAppointments && HasSpeakers && double.IsPositiveInfinity(nextOpportunity)) nextOpportunity = opportunityNow + 1;
                // Evaluate once after accepting the batch, so a tie preserves the previous shared order.
                if (RankBeforeVotes != RankAfterVotes) RankBeforeName = RankBeforeVotes > RankAfterVotes;
                foreach (var submission in result.Remarks)
                {
                    var entry = work.Single(w => w.Target.Id == submission.Id);
                    if (!Valid(entry) || now - startedAt > 180 || !entry.State.Enabled || entry.State.Revision != entry.Revision || !entry.State.Target.CanSpeak || !entry.State.Target.Members.SequenceEqual(entry.Target.Members)) continue;
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
                if (Paused) { nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity; }
                else
                {
                    if (!HasOpportunities(settings)) nextOpportunity = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(nextOpportunity) && (hadSpeech || hadAutomaticAppointments || !HasSpeakers))
                        nextOpportunity = opportunityNow + 60 * settings.IntervalMinutes;
                    if (!HasPendingAppointments) nextAppointments = double.PositiveInfinity;
                    else if (double.IsPositiveInfinity(nextAppointments)) nextAppointments = now + 1;
                }
                cancellation?.Dispose(); cancellation = null; Version++;
            }
        }
        if (Paused || task != null) return;
        var opportunityDue = !simulationPaused && opportunityNow >= nextOpportunity;
        if (opportunityDue && settings.AutoAppointments && people.Count > 0)
        {
            QueueAppointments(now, AppointmentRequest.Automatic);
            // An empty check is a normal opportunity and sends no request on its own.
            nextOpportunity = opportunityNow + 60 * settings.IntervalMinutes;
        }
        if (!opportunityDue && now < nextAppointments) return;
        var appointments = now < nextAppointments ? Array.Empty<State>() : states.Values.Where(s => s.Appointment != AppointmentRequest.None &&
            (!simulationPaused || s.Appointment is AppointmentRequest.Manual or AppointmentRequest.Replacement)).Take(CrewBatch.MaximumAppointments).ToArray();
        var candidates = HasPendingAppointments || !opportunityDue ? Array.Empty<State>() : states.Values.Where(s => s.Enabled && s.Target.CanSpeak && people.ContainsKey(s.Target.Id))
            .OrderBy(s => s.LastConsidered).ThenBy(_ => random.NextDouble()).Take(4).ToArray();
        if (appointments.Length == 0 && candidates.Length == 0) return;
        if (settings.ConnectionId == null) { Error = "connection_required"; SetPaused(true, now); return; }
        if (appointments.Length > 0)
        {
            nextAppointments = double.PositiveInfinity;
            if (appointments.Any(s => s.Appointment == AppointmentRequest.Automatic)) nextOpportunity = double.PositiveInfinity;
        }
        if (candidates.Length > 0)
        {
            nextOpportunity = double.PositiveInfinity; var round = ++selectionRound;
            foreach (var candidate in candidates) candidate.LastConsidered = round;
        }
        var data = candidates.Length == 0 ? new JObject() : settings.Information.Filter(capture(candidates.Select(s => s.Target).ToArray()));
        data["reply_language"] = language;
        JObject Identity(State state) => new() { ["id"] = state.Target.Id, ["display_name"] = state.Target.Name, ["nation"] = state.Target.Nation, ["role"] = state.Target.Role };
        if (appointments.Length > 0) data["appointment_context"] = captureAppointments(language).DeepClone();
        data["appointments"] = new JArray(appointments.Select(s =>
        {
            var row = Identity(s); row["kind"] = s.Target.Kind; row["class_name"] = s.Target.ClassName;
            row["formation_leader"] = s.Target.FormationLeader;
            return row;
        }));
        data["speakers"] = new JArray(candidates.Select(s =>
        {
            var row = Identity(s); var person = Person(s.Target.Id);
            row["member_unit_ids"] = new JArray(s.Target.Members.Select(id => "unit:" + id));
            row["person"] = person == null ? JValue.CreateNull() : person.Json();
            return row;
        }));
        if (candidates.Length > 0) data["recent_remarks"] = new JArray(history.Where(r => r.InformationRevision == informationRevision && ReferenceEquals(r.Submission.Person, Person(r.Target.Id))).TakeLast(3).Select(r => new JObject { ["id"] = r.Target.Id, ["text"] = r.Submission.Text }));
        active = appointments.Concat(candidates).Distinct().Select(s => (s, s.Revision, s.Target, s.Appointment, s.Character)).ToArray(); startedAt = now; speechInFlight = candidates.Length > 0;
        activeInformationRevision = informationRevision;
        cancellation = new CancellationTokenSource();
        var batch = new CrewBatch(data, candidates.ToDictionary(s => s.Target.Id, s => Person(s.Target.Id)), appointments.Select(s => s.Target.Id),
            preferExchange: candidates.Length >= 2 && random.NextDouble() < settings.ExchangeChancePercent / 100.0,
            characters: appointments.Where(s => s.Character != null).ToDictionary(s => s.Target.Id, s => s.Character!),
            automaticCharacters: AppointmentCandidates(appointments));
        var token = cancellation.Token; var connectionId = settings.ConnectionId;
        task = Task.Run(() => generate(batch, connectionId, token, Usage.Record), token); Version++;
    }
    private IReadOnlyDictionary<string, CrewCharacter[]> AppointmentCandidates(State[] appointments)
    {
        var automatic = appointments.Where(s => s.Character == null && s.Appointment != AppointmentRequest.Replacement).ToArray();
        if (automatic.Length == 0 || characters == null) return new Dictionary<string, CrewCharacter[]>();
        var inUse = new HashSet<string>(states.Values.SelectMany(s => new[] { Person(s.Target.Id)?.RegisteredCharacterId, s.Character?.Id })
            .OfType<string>(), StringComparer.Ordinal);
        var available = characters.Characters.Where(c => c.AiAppointment != "excluded" && (c.AllowDuplicates || !inUse.Contains(c.Id))).ToArray();
        return automatic.Select(s => (Id: s.Target.Id, Characters: available.Where(c => c.MatchesAssignmentTarget(s.Target.Kind)).ToArray()))
            .Where(p => p.Characters.Length > 0).ToDictionary(p => p.Id, p => p.Characters);
    }
    internal CrewRemark? NextNotification(double now)
    {
        if (Paused) return null;
        while (pending.Count > 0)
        {
            var remark = pending.Dequeue();
            if (remark.ExpiresAt > now && IsCurrent(remark)) return remark;
        }
        return null;
    }
    internal bool IsCurrent(CrewRemark remark) => states.TryGetValue(remark.Target.Id, out var state) && state.Enabled && state.Target.CanSpeak &&
        state.Revision == remark.Revision && state.Target.Members.SequenceEqual(remark.Target.Members);
    private void CancelGeneration()
    {
        cancellation?.Cancel();
        var oldTask = task; task = null; active = Array.Empty<(State, int, CrewTarget, AppointmentRequest, CrewCharacter?)>(); speechInFlight = false;
        var oldCancellation = cancellation; cancellation = null;
        if (oldTask == null) oldCancellation?.Dispose();
        else _ = oldTask.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); oldCancellation?.Dispose(); }, TaskScheduler.Default);
        foreach (var state in states.Values) state.Revision++;
        pending.Clear(); nextOpportunity = double.PositiveInfinity; nextAppointments = double.PositiveInfinity;
    }
    internal void Suspend()
    {
        CancelGeneration(); foreach (var state in states.Values) { state.Enabled = false; state.Appointment = AppointmentRequest.None; state.Character = null; }
        Paused = false; Error = null; Version++;
    }
    public void Dispose() { retired = true; Suspend(); states.Clear(); people.Clear(); history.Clear(); }
}
