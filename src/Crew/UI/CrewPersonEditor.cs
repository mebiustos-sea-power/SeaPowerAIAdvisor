using System;
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
    private readonly Action<string?, Action<string?>> choosePortrait;
    private readonly TypingScope typing;
    private readonly TextBox name, rank, personality;
    private readonly Image image;
    private readonly UiNotice notice;
    private CrewPersonDraft? draft;
    private string? id;
    private bool changing;
    private int revision = -1;
    internal string? PortraitId => draft?.PortraitId;
    internal int PortraitRevision { get; private set; }
    internal bool IsOpen => id != null;
    internal bool HasChanges => draft?.HasChanges == true;
    private T Find<T>(string value) where T : class => root.FindName(value) as T ?? throw new InvalidOperationException("Missing Crew person control: " + value);
    internal CrewPersonEditor(FrameworkElement root, CrewChatter chatter, CrewPortraits portraits, CrewText text,
        Func<string, string> common, TypingScope typing, Func<bool> canCommit, Action<Action> run, Action<Action> confirm,
        Action<string?, Action<string?>> choosePortrait)
    {
        this.root = root; form = Find<FrameworkElement>("PersonPage"); list = Find<FrameworkElement>("TargetsList"); this.chatter = chatter; this.portraits = portraits; this.text = text; this.common = common;
        this.typing = typing; this.canCommit = canCommit; this.run = run; this.confirm = confirm;
        this.choosePortrait = choosePortrait;
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
            var current = draft; if (current == null) return;
            typing.Release(); choosePortrait(current.PortraitId, portraitId =>
            {
                if (!ReferenceEquals(draft, current)) throw new CrewException("person_changed");
                current.SelectPortrait(portraitId); RefreshPortrait(); notice.Show("");
            });
        });
        Bind("CommonPersonPortrait", () => { draft!.SelectPortrait(null); RefreshPortrait(); notice.Show(""); });
    }
    private void Bind(string control, Action action) => Find<Button>(control).Click += (_, _) => run(action);
    private void Leave(Action action) { if (draft?.HasChanges == true) confirm(action); else action(); }
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
        notice.Show(""); RefreshPortrait();
    }
    internal void RefreshPortrait()
    {
        if (!IsOpen) return;
        image.Source = portraits.GetImage(draft?.PortraitId); PortraitRevision++;
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
        var editable = target != null && (!waiting || chatter.Paused);
        Find<FrameworkElement>("PersonFields").IsEnabled = editable;
        Find<Button>("SavePerson").IsEnabled = editable;
    }
    private void Save()
    {
        if (draft == null || id == null || !canCommit()) return;
        var next = draft.Build(); chatter.UpdatePerson(id, draft.Original, next); Load(next);
        notice.ShowBriefly(common("settings.saved"));
    }
    internal void ShowError(string value) => notice.Show(value, true);
    internal void Tick()
    {
        if (!IsOpen) return;
        notice.Tick();
        var current = id == null ? null : chatter.Person(id);
        if (current != null && draft != null && !ReferenceEquals(current, draft.Original) && !HasChanges) Load(current);
        if (revision != chatter.Version) Controls();
    }
    internal void Close()
    {
        typing.Release(); image.Source = null; draft = null; id = null; PortraitRevision++;
        form.Visibility = Visibility.Collapsed; list.Visibility = Visibility.Visible; notice.Show("");
    }
    public void Dispose() => Close();
}
