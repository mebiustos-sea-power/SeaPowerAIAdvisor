using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Noesis;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Owns a single unit's edit draft and preview, not participation or registration templates.
internal sealed class CrewPersonEditor : IDisposable
{
    private readonly FrameworkElement root, form, list;
    private readonly CrewChatter chatter;
    private readonly CrewPortraits portraits;
    private readonly CrewText text;
    private readonly Func<string, string> common;
    private readonly Func<bool> canCommit;
    private readonly Action<Action> run, confirm;
    private readonly Action validateConnection;
    private readonly Action<Exception> log;
    private readonly Func<double> now;
    private readonly TypingScope typing;
    private readonly TextBox name, rank, personality;
    private readonly Image image;
    private readonly UiNotice notice;
    private CrewPersonDraft? draft;
    private string? id;
    private PortraitTexture? preview;
    private Task<string?>? selection;
    private bool changing;
    private int revision = -1;
    internal string? PortraitFile => draft?.PortraitFile;
    internal bool IsOpen => id != null;
    internal bool HasChanges => selection != null || draft?.HasChanges == true;
    private T Find<T>(string value) where T : class => root.FindName(value) as T ?? throw new InvalidOperationException("Missing Crew person control: " + value);
    internal CrewPersonEditor(FrameworkElement root, CrewChatter chatter, CrewPortraits portraits, CrewText text,
        Func<string, string> common, TypingScope typing, Func<bool> canCommit, Action<Action> run, Action<Action> confirm,
        Action validateConnection, Func<double> now, Action<Exception> log)
    {
        this.root = root; form = Find<FrameworkElement>("PersonPage"); list = Find<FrameworkElement>("TargetsList"); this.chatter = chatter; this.portraits = portraits; this.text = text; this.common = common;
        this.typing = typing; this.canCommit = canCommit; this.run = run; this.confirm = confirm;
        this.validateConnection = validateConnection; this.now = now; this.log = log;
        name = Find<TextBox>("PersonName"); rank = Find<TextBox>("PersonRank"); personality = Find<TextBox>("PersonPersonality");
        image = Find<Image>("PersonPortrait"); notice = new UiNotice(Find<TextBlock>("PersonStatus"), root);
        foreach (var input in new[] { name, rank, personality })
        {
            input.GotKeyboardFocus += (_, _) => run(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => run(() => typing.Release(input));
            input.TextChanged += (_, _) => { if (!changing && draft != null) { draft.Name = name.Text; draft.Rank = rank.Text; draft.Personality = personality.Text; } };
        }
        Bind("SavePerson", Save);
        Bind("BackPerson", () => Leave(Close));
        Bind("ChoosePersonPortrait", () =>
        {
            if (selection != null) return;
            typing.Release(); selection = PortraitFileDialog.OpenAsync(common("profile.choose_title"), common("profile.image_filter")); Controls();
        });
        Bind("CommonPersonPortrait", () => { draft!.UseCommonPortrait(); Preview(); notice.Show(""); });
        Bind("ReappointPerson", () => Leave(() =>
        {
            if (!canCommit() || chatter.Paused || chatter.Busy || id == null) return;
            validateConnection(); chatter.RequestReappointment(id, now());
            Load(chatter.Person(id) ?? throw new CrewException("person_unavailable")); Controls();
        }));
    }
    private void Bind(string control, Action action) => Find<Button>(control).Click += (_, _) => run(action);
    private void Leave(Action action) { if (selection != null) return; if (draft?.HasChanges == true) confirm(action); else action(); }
    internal void Open(string unitId)
    {
        var person = chatter.Person(unitId) ?? throw new CrewException("person_unavailable");
        id = unitId; Load(person); list.Visibility = Visibility.Collapsed; form.Visibility = Visibility.Visible; Controls();
    }
    private void Load(CrewPerson person)
    {
        typing.Release(); draft = new CrewPersonDraft(person); changing = true;
        try { name.Text = person.Name; rank.Text = person.Rank; personality.Text = person.Personality; }
        finally { changing = false; }
        notice.Show(""); Preview();
    }
    private void Preview()
    {
        var next = draft?.SelectedPortrait is byte[] bytes ? new PortraitTexture(bytes) : null;
        var source = next?.Source ?? portraits.GetFile(draft?.PortraitFile);
        image.Source = source; var previous = preview; preview = next; previous?.Dispose();
    }
    private void Controls()
    {
        revision = chatter.Version;
        var target = id == null ? null : System.Linq.Enumerable.FirstOrDefault(chatter.Targets, t => t.Id == id);
        Find<TextBlock>("PersonUnit").Text = target?.Name ?? text["error.person_unavailable"];
        var waiting = id != null && chatter.AppointmentRequested(id);
        var pending = Find<TextBlock>("PersonPending");
        pending.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        pending.Text = text[chatter.Busy ? "reappointing" : "appointment_pending"];
        // Pausing cancels the running request, so an edit may supersede its pending replacement.
        var editable = target != null && selection == null && (!waiting || chatter.Paused);
        Find<FrameworkElement>("PersonFields").IsEnabled = editable;
        Find<Button>("SavePerson").IsEnabled = editable;
        Find<Button>("ReappointPerson").IsEnabled = editable && !chatter.Busy && !chatter.Paused;
        Find<Button>("BackPerson").IsEnabled = selection == null;
    }
    private void Save()
    {
        if (draft == null || id == null || selection != null || !canCommit()) return;
        _ = draft.Build();
        string? imported = null;
        try
        {
            if (draft.SelectedPortrait is byte[] bytes) imported = portraits.Files.Import(bytes);
            var next = draft.Build(imported); chatter.UpdatePerson(id, draft.Original, next); Load(next);
            notice.ShowBriefly(common("settings.saved"));
        }
        finally { portraits.Files.Prune(chatter.PortraitFiles); }
    }
    internal void ShowError(string value) => notice.Show(value, true);
    internal void Tick()
    {
        if (!IsOpen) return;
        notice.Tick();
        if (selection?.IsCompleted == true)
        {
            var completed = selection; selection = null; revision = -1;
            try
            {
                var file = completed.GetAwaiter().GetResult();
                if (file != null && draft != null)
                {
                    var bytes = PortraitData.Read(file);
                    // Decode before replacing the draft or its currently displayed preview.
                    using (var verified = new PortraitTexture(bytes)) { }
                    draft.SelectPortrait(bytes); Preview(); notice.Show("");
                }
            }
            catch (Win32Exception e) { log(e); notice.Show(common("profile.error.portrait_dialog_failed"), true); }
        }
        var current = id == null ? null : chatter.Person(id);
        if (current != null && draft != null && !ReferenceEquals(current, draft.Original) && !HasChanges) Load(current);
        if (revision != chatter.Version) Controls();
    }
    internal void Close()
    {
        typing.Release(); image.Source = null; preview?.Dispose(); preview = null; draft = null; id = null;
        var old = selection; selection = null;
        if (old != null) _ = old.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); }, TaskScheduler.Default);
        form.Visibility = Visibility.Collapsed; list.Visibility = Visibility.Visible; notice.Show("");
    }
    public void Dispose() => Close();
}
