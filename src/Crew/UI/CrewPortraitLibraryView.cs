using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noesis;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// The same paged gallery serves library management and image selection. Only Add/Delete change storage.
internal sealed class CrewPortraitLibraryView : IDisposable
{
    private const int PageSize = 12;
    private readonly FrameworkElement form;
    private readonly CrewPortraits portraits;
    private readonly CrewText text;
    private readonly Func<string, string> common;
    private readonly Action<Action> run;
    private readonly Action<Action, string, string> confirm;
    private readonly Action<Exception> log;
    private readonly Action<string?>? select;
    private readonly UiNotice notice;
    private readonly Image previewImage;
    private Task<string?>? selection;
    private PortraitTexture? preview;
    private byte[]? prepared;
    private string[] ids = Array.Empty<string>(), displayed = Array.Empty<string>();
    private readonly HashSet<string> unreadable = new(StringComparer.Ordinal);
    private string? selected;
    private int page, libraryRevision = -1;
    internal bool IsOpen { get; private set; }
    internal bool HasChanges => selection != null || prepared != null;
    internal int PortraitRevision { get; private set; }
    internal IEnumerable<string> DisplayedIds => displayed;
    private CrewPortraitLibrary Library => portraits.Library;
    private T Find<T>(string name) where T : class => form.FindName(name) as T ?? throw new InvalidOperationException("Missing portrait library control: " + name);
    internal static FrameworkElement Create()
    {
        using var source = typeof(CrewPortraitLibraryView).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.CrewPortraitLibrary.xaml")
            ?? throw new FileNotFoundException("The portrait library view is missing.");
        using var reader = new StreamReader(source);
        return GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement ?? throw new InvalidOperationException("The portrait library view is invalid.");
    }
    internal CrewPortraitLibraryView(FrameworkElement form, FrameworkElement resources, CrewPortraits portraits, CrewText text,
        Func<string, string> common, Action<Action> run, Action<Action, string, string> confirm, Action<Exception> log,
        Action<string?>? select = null, Action? back = null)
    {
        this.form = form; this.portraits = portraits; this.text = text; this.common = common; this.run = run;
        this.confirm = confirm; this.log = log; this.select = select;
        notice = new UiNotice(Find<TextBlock>("Status"), resources); previewImage = Find<Image>("Preview");
        Find<TextBlock>("Title").Text = text[select == null ? "portraits" : "portrait.select_title"];
        Find<FrameworkElement>("Selection").Visibility = select == null ? Visibility.Collapsed : Visibility.Visible;
        Bind("Add", () =>
        {
            if (HasChanges) return;
            selection = PortraitFileDialog.OpenAsync(text["portrait.choose_title"], common("profile.image_filter")); Controls();
        });
        Bind("CancelImport", () => { ClearImport(); Controls(); });
        Bind("SaveImport", () =>
        {
            if (prepared == null) return;
            selected = Library.Add(prepared); ClearImport(); Refresh(true); notice.ShowBriefly(common("settings.saved"));
        });
        Bind("Delete", () =>
        {
            if (selected == null || HasChanges) return;
            var id = selected;
            confirm(() => { Library.Delete(id); if (selected == id) selected = null; Refresh(); notice.ShowBriefly(text["portrait.deleted"]); },
                text["portrait.delete_question"], text["portrait.delete"]);
        });
        Bind("Previous", () => { if (page > 0) { page--; Draw(); } });
        Bind("Next", () => { if ((page + 1) * PageSize < ids.Length) { page++; Draw(); } });
        Bind("Back", () => back?.Invoke());
        Bind("Select", () =>
        {
            if (selected == null || !Library.Contains(selected))
            { Refresh(); notice.Show(text["error.portrait_selection_missing"], true); return; }
            select?.Invoke(selected);
        });
    }
    private void Bind(string name, Action action) => Find<Button>(name).Click += (_, _) => run(action);
    internal void Open(string? initial = null)
    {
        IsOpen = true; selected = initial; page = 0; notice.Show(""); Refresh(true);
    }
    private void Refresh(bool locateSelection = false)
    {
        libraryRevision = Library.Version;
        ids = Library.Ids().ToArray(); unreadable.Clear();
        if (locateSelection && selected != null) page = Math.Max(0, Array.IndexOf(ids, selected)) / PageSize;
        page = Math.Min(page, Math.Max(0, (ids.Length - 1) / PageSize));
        Draw();
    }
    private void Draw()
    {
        var panel = Find<WrapPanel>("Images"); panel.Children.Clear();
        displayed = ids.Skip(page * PageSize).Take(PageSize).ToArray(); PortraitRevision++;
        foreach (var id in displayed)
        {
            FrameworkElement content;
            try
            {
                content = new Image { Source = portraits.GetImage(id), Width = 96, Height = 96, Stretch = Stretch.Uniform };
                unreadable.Remove(id);
            }
            catch (PortraitDataException error)
            {
                log(error); unreadable.Add(id);
                content = new TextBlock { Text = text["portrait.unreadable"], TextWrapping = TextWrapping.Wrap, Width = 96, VerticalAlignment = VerticalAlignment.Center };
            }
            var border = new Border { Child = content, BorderThickness = new Thickness(2), Padding = new Thickness(3),
                BorderBrush = selected == id ? new SolidColorBrush(Color.FromArgb(255, 235, 151, 64)) : Brushes.Transparent };
            var button = new Button { Content = border, Width = 116, Height = 116, Margin = new Thickness(4), Padding = new Thickness(2), ToolTip = text["portrait.select"] };
            button.Click += (_, _) => run(() => { selected = id; Draw(); }); panel.Children.Add(button);
        }
        Find<TextBlock>("Count").Text = string.Format(text["portrait.count"], ids.Length);
        Find<TextBlock>("Page").Text = string.Format(text["portrait.page"], ids.Length == 0 ? 0 : page + 1, (ids.Length + PageSize - 1) / PageSize);
        Find<FrameworkElement>("Empty").Visibility = ids.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Controls();
    }
    private void Controls()
    {
        Find<Button>("Add").IsEnabled = !HasChanges;
        Find<Button>("Delete").IsEnabled = !HasChanges && selected != null && ids.Contains(selected);
        Find<FrameworkElement>("GalleryScroll").Visibility = prepared == null ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("ImportScroll").Visibility = prepared == null ? Visibility.Collapsed : Visibility.Visible;
        Find<FrameworkElement>("Paging").Visibility = prepared == null ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("Previous").IsEnabled = !HasChanges && page > 0;
        Find<Button>("Next").IsEnabled = !HasChanges && (page + 1) * PageSize < ids.Length;
        Find<Button>("Select").IsEnabled = !HasChanges && selected != null && ids.Contains(selected) && !unreadable.Contains(selected);
        Find<Button>("Back").IsEnabled = selection == null;
    }
    internal void ShowError(string message) => notice.Show(message, true);
    internal void Tick()
    {
        if (!IsOpen) return;
        notice.Tick();
        if (selection?.IsCompleted == true)
        {
            var completed = selection; selection = null;
            try
            {
                var file = completed.GetAwaiter().GetResult();
                if (file != null)
                {
                    var bytes = PortraitResize.Fit(PortraitData.Read(file), CrewPortraitLibrary.MaximumDimension);
                    var next = new PortraitTexture(bytes);
                    previewImage.Source = next.Source; var old = preview; preview = next; old?.Dispose(); prepared = bytes;
                    Find<TextBlock>("SourceName").Text = System.IO.Path.GetFileName(file); notice.Show("");
                }
            }
            catch (Win32Exception e) { log(e); notice.Show(common("profile.error.portrait_dialog_failed"), true); }
            finally { Controls(); }
        }
        if (libraryRevision != Library.Version) Refresh();
    }
    private void ClearImport()
    {
        previewImage.Source = null; preview?.Dispose(); preview = null; prepared = null;
        Find<TextBlock>("SourceName").Text = "";
    }
    internal void Close()
    {
        IsOpen = false; ClearImport(); Find<WrapPanel>("Images").Children.Clear(); displayed = Array.Empty<string>(); unreadable.Clear(); PortraitRevision++;
        var old = selection; selection = null;
        if (old != null) _ = old.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); }, TaskScheduler.Default);
    }
    public void Dispose() => Close();
}
