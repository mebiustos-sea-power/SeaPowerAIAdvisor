using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Noesis;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Character forms own registration drafts; selection only returns an ID to the appointment owner.
internal sealed class CrewCharacterLibraryView : IDisposable
{
    private const int PageSize = 10;
    private static readonly string[] Genders = { "", "male", "female" };
    private static readonly string[] AssignmentTargets = { "any", "ship", "aircraft" };
    private static readonly string[] AiModes = { "excluded", "normal", "priority" };
    private readonly FrameworkElement form;
    private readonly CrewCharacterLibrary library;
    private readonly CrewPortraits portraits;
    private readonly CrewText text;
    private readonly Func<string, string> common;
    private readonly TypingScope typing;
    private readonly Action<Action> run;
    private readonly Func<bool> canCommit;
    private readonly Action<Action, string, string> confirm;
    private readonly Action<string?, Action<string?>> choosePortrait;
    private readonly Action<Exception> log;
    private readonly Action<string>? select;
    private readonly Func<CrewCharacter, bool>? canSelect;
    private readonly UiNotice notice;
    private readonly TextBox name, rank, personality, nationality;
    private readonly ComboBox gender, aiAppointment, assignmentTarget;
    private readonly CheckBox allowDuplicates;
    private CrewCharacter? editing;
    private bool isNew;
    private string? selected, portraitId, baseline;
    private string[] displayed = Array.Empty<string>();
    private int page, libraryVersion = -1, portraitVersion = -1;
    internal bool IsOpen { get; private set; }
    internal int PortraitRevision { get; private set; }
    internal IEnumerable<string?> DisplayedIds => displayed.Append(editing == null ? null : portraitId);
    private string Values => new JArray(name.Text, rank.Text, personality.Text, nationality.Text, gender.SelectedIndex, aiAppointment.SelectedIndex, assignmentTarget.SelectedIndex, allowDuplicates.IsChecked == true, portraitId ?? "").ToString();
    internal bool HasChanges => editing != null && (isNew || Values != baseline);
    private T Find<T>(string id) where T : class => form.FindName(id) as T ?? throw new InvalidOperationException("Missing character control: " + id);
    internal static FrameworkElement Create()
    {
        using var stream = typeof(CrewCharacterLibraryView).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.CrewCharacterLibrary.xaml")
            ?? throw new FileNotFoundException("The character library view is missing.");
        using var reader = new StreamReader(stream);
        return GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement ?? throw new InvalidOperationException("The character library view is invalid.");
    }
    internal CrewCharacterLibraryView(FrameworkElement form, FrameworkElement resources, CrewCharacterLibrary library, CrewPortraits portraits,
        CrewText text, Func<string, string> common, TypingScope typing, Func<bool> canCommit, Action<Action> run, Action<Action, string, string> confirm,
        Action<string?, Action<string?>> choosePortrait, Action<Exception> log, Action<string>? select = null, Action? back = null,
        Func<CrewCharacter, bool>? canSelect = null)
    {
        this.form = form; this.library = library; this.portraits = portraits; this.text = text; this.common = common; this.typing = typing;
        this.run = run; this.canCommit = canCommit; this.confirm = confirm; this.choosePortrait = choosePortrait; this.log = log; this.select = select; this.canSelect = canSelect;
        name = Find<TextBox>("Name"); rank = Find<TextBox>("Rank"); personality = Find<TextBox>("Personality"); nationality = Find<TextBox>("Nationality");
        gender = Find<ComboBox>("Gender"); aiAppointment = Find<ComboBox>("AiAppointment"); allowDuplicates = Find<CheckBox>("AllowDuplicates");
        assignmentTarget = Find<ComboBox>("AssignmentTarget");
        notice = new UiNotice(Find<TextBlock>("Status"), resources);
        foreach (var code in Genders) gender.Items.Add(new ComboBoxItem { Content = text["character.gender." + (code.Length == 0 ? "unspecified" : code)] });
        foreach (var code in AssignmentTargets) assignmentTarget.Items.Add(new ComboBoxItem { Content = text["character.assignment." + code] });
        foreach (var code in AiModes) aiAppointment.Items.Add(new ComboBoxItem { Content = text["character.ai." + code] });
        foreach (var input in new[] { name, rank, personality, nationality })
        {
            input.GotKeyboardFocus += (_, _) => run(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => run(() => typing.Release(input));
        }
        Bind("Add", () => OpenForm(new CrewCharacter(Guid.NewGuid().ToString("N")), true));
        Bind("Edit", () => OpenForm(Selected() ?? throw new CrewException("character_unavailable"), false));
        Bind("Delete", () =>
        {
            var id = Selected()?.Id ?? throw new CrewException("character_unavailable");
            confirm(() => { library.Delete(id); selected = null; Draw(); notice.ShowBriefly(text["character.deleted"]); }, text["character.delete_question"], text["portrait.delete"]);
        });
        Bind("BackForm", () => { if (HasChanges) confirm(CloseForm, common("settings.discard_question"), common("settings.discard_action")); else CloseForm(); });
        Bind("Save", () =>
        {
            if (editing == null || !canCommit()) return;
            var next = new CrewCharacter(editing.Id, name.Text, rank.Text, personality.Text, Choice(Genders, gender.SelectedIndex), nationality.Text,
                portraitId, Choice(AiModes, aiAppointment.SelectedIndex), allowDuplicates.IsChecked == true, Choice(AssignmentTargets, assignmentTarget.SelectedIndex));
            library.Save(next); selected = next.Id; OpenForm(next, false); notice.ShowBriefly(common("settings.saved"));
        });
        Bind("ChoosePortrait", () =>
        {
            var current = editing; if (current == null) return;
            typing.Release(); choosePortrait(portraitId, id =>
            {
                if (!ReferenceEquals(editing, current)) throw new CrewException("character_unavailable");
                portraitId = id; RefreshPortrait();
            });
        });
        Bind("CommonPortrait", () => { portraitId = null; RefreshPortrait(); });
        Bind("Previous", () => { page--; Draw(); }); Bind("Next", () => { page++; Draw(); });
        Bind("Select", () =>
        {
            var character = Selected() ?? throw new CrewException("character_unavailable");
            if (canSelect?.Invoke(character) == false) throw new CrewException("character_assignment_unavailable");
            select?.Invoke(character.Id);
        });
        Bind("Back", () => back?.Invoke());
    }
    private static string Choice(string[] choices, int index) => index >= 0 && index < choices.Length ? choices[index] : throw new CrewException("character_invalid");
    private void Bind(string id, Action action) => Find<Button>(id).Click += (_, _) => run(action);
    private CrewCharacter? Selected() => selected == null ? null : library.Find(selected);
    internal void Open(string? initial = null)
    {
        IsOpen = true; selected = initial; page = 0; editing = null; notice.Show(""); Draw();
    }
    private void OpenForm(CrewCharacter character, bool create)
    {
        typing.Release(); editing = character; isNew = create; portraitId = character.PortraitId;
        name.Text = character.Name; rank.Text = character.Rank; personality.Text = character.Personality; nationality.Text = character.Nationality;
        gender.SelectedIndex = Array.IndexOf(Genders, character.Gender); aiAppointment.SelectedIndex = Array.IndexOf(AiModes, character.AiAppointment);
        assignmentTarget.SelectedIndex = Array.IndexOf(AssignmentTargets, character.AssignmentTarget);
        allowDuplicates.IsChecked = character.AllowDuplicates; baseline = Values; notice.Show(""); Controls(); RefreshPortrait();
    }
    private void CloseForm() { typing.Release(); editing = null; Find<Image>("Portrait").Source = null; PortraitRevision++; Draw(); notice.Show(""); }
    internal void RefreshPortrait()
    {
        if (editing == null) return;
        var image = Find<Image>("Portrait"); image.Source = null;
        try { image.Source = portraits.GetImage(portraitId); }
        catch (PortraitDataException e) { log(e); notice.Show(common("profile.error." + e.Code), true); }
        PortraitRevision++;
    }
    private void Draw()
    {
        libraryVersion = library.Version; portraitVersion = portraits.Library.Version;
        var items = library.Characters; if (Selected() == null) selected = null;
        page = Math.Max(0, Math.Min(page, Math.Max(0, (items.Count - 1) / PageSize)));
        Find<TextBlock>("Title").Text = text[select == null ? "characters" : "character.select_title"];
        var panel = Find<StackPanel>("Characters"); panel.Children.Clear();
        var shown = items.Skip(page * PageSize).Take(PageSize).ToArray(); displayed = shown.Select(c => c.PortraitId).Where(id => id != null).Select(id => id!).ToArray(); PortraitRevision++;
        var ordinal = page * PageSize;
        foreach (var character in shown)
        {
            ordinal++; var row = new StackPanel { Orientation = Orientation.Horizontal };
            try { row.Children.Add(new Image { Source = portraits.GetImage(character.PortraitId), Width = 48, Height = 48, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0) }); }
            catch (PortraitDataException e) { log(e); row.Children.Add(new TextBlock { Text = text["portrait.unreadable"], Width = 60, TextWrapping = TextWrapping.Wrap }); }
            var label = character.Name.Length == 0 ? string.Format(text["character.unnamed"], ordinal) : character.Name;
            var words = new StackPanel(); words.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold });
            if (character.Rank.Length > 0) words.Children.Add(new TextBlock { Text = character.Rank });
            if (select != null && canSelect?.Invoke(character) == false) words.Children.Add(new TextBlock { Text = text["character.unavailable"], FontSize = 12, Opacity = .7f });
            row.Children.Add(words);
            var button = new Button { Content = new Border { Child = row, BorderThickness = new Thickness(2), Padding = new Thickness(5),
                BorderBrush = selected == character.Id ? new SolidColorBrush(Color.FromArgb(255, 235, 151, 64)) : Brushes.Transparent },
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => run(() => { selected = character.Id; Draw(); }); panel.Children.Add(button);
        }
        Find<TextBlock>("Empty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Find<TextBlock>("Page").Text = string.Format(text["portrait.page"], items.Count == 0 ? 0 : page + 1, (items.Count + PageSize - 1) / PageSize);
        Controls();
    }
    private void Controls()
    {
        var formOpen = editing != null;
        Find<FrameworkElement>("Actions").Visibility = select == null && !formOpen ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("ListScroll").Visibility = formOpen ? Visibility.Collapsed : Visibility.Visible;
        Find<FrameworkElement>("FormScroll").Visibility = formOpen ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("FormButtons").Visibility = formOpen ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("Paging").Visibility = formOpen ? Visibility.Collapsed : Visibility.Visible;
        Find<FrameworkElement>("Selection").Visibility = select != null ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("Edit").IsEnabled = Find<Button>("Delete").IsEnabled = Selected() != null;
        Find<Button>("Previous").IsEnabled = page > 0;
        Find<Button>("Next").IsEnabled = (page + 1) * PageSize < library.Characters.Count;
        Find<Button>("Select").IsEnabled = Selected() is CrewCharacter character && canSelect?.Invoke(character) != false;
    }
    internal void RefreshSelection() { if (IsOpen && select != null) Draw(); }
    internal void ShowError(string message) => notice.Show(message, true);
    internal void Tick()
    {
        if (!IsOpen) return;
        notice.Tick();
        if (libraryVersion != library.Version || portraitVersion != portraits.Library.Version)
        { Draw(); RefreshPortrait(); }
        else if (select != null) Controls();
    }
    internal void Close()
    {
        IsOpen = false; editing = null; selected = null; baseline = null; displayed = Array.Empty<string>();
        Find<StackPanel>("Characters").Children.Clear(); Find<Image>("Portrait").Source = null; typing.Release(); PortraitRevision++;
    }
    public void Dispose() => Close();
}
