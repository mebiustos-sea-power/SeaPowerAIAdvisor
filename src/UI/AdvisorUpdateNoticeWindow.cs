using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

// The native window owns presentation and dismissal; read receipts remain independent of the UI.
internal sealed class AdvisorUpdateNoticeWindow
{
    private readonly FrameworkElement view;
    private readonly WindowHost host;
    private readonly Window owner, window;
    private readonly TextBlock failure;
    private readonly ScrollViewer scroll;
    private readonly Button close;
    private readonly AdvisorUpdateNoticeStore store;
    private readonly Func<AdvisorText> text;
    private readonly Action releaseInput, closed;
    private readonly Action<AdvisorUpdateNoticeException> report;
    private AdvisorUpdateNotice? current;
    private bool preview;
    internal AdvisorUpdateNoticeWindow(FrameworkElement view, WindowHost host, Window owner, AdvisorUpdateNoticeStore store,
        Func<AdvisorText> text, Action<Action> guard, Action releaseInput, Action closed, Action<AdvisorUpdateNoticeException> report)
    {
        this.view = view; this.host = host; this.owner = owner; this.store = store; this.text = text;
        this.releaseInput = releaseInput; this.closed = closed; this.report = report;
        using var source = typeof(AdvisorUpdateNoticeWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.AdvisorUpdateNotice.xaml")
            ?? throw new FileNotFoundException("The update notice window resource is missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var content = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement
            ?? throw new InvalidOperationException("The update notice view is not a FrameworkElement.");
        failure = Find<TextBlock>(content, "UpdateNoticeFailure");
        scroll = Find<ScrollViewer>(content, "UpdateNoticeScroll"); close = Find<Button>(content, "CloseUpdateNotice");
        var ownerContent = owner.Content as FrameworkElement
            ?? throw new InvalidOperationException("The advisor window content is unavailable.");
        failure.Foreground = ownerContent.Resources["AdvisorErrorBrush"] as Brush
            ?? throw new InvalidOperationException("The advisor error brush is missing.");
        window = new Window
        {
            Content = content, Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        window.DataContext = window;
        window.Template = view.TryFindResource("WindowLayoutNoScroll") as ControlTemplate
            ?? throw new InvalidOperationException("The game's window template is unavailable.");
        close.Click += (_, _) => guard(Dismiss);
        window.OnClosed += (_, _) => guard(OnClosed);
    }
    internal bool IsOpen => host.Windows.Contains(window);
    internal bool IsPreview => IsOpen && preview;
    internal bool Show(bool preview = false)
    {
        if (IsOpen) return true;
        var notice = AdvisorUpdateNotice.Current;
        if (notice == null) return false;
        failure.Text = "";
        try { if (store.Unread(notice, preview) == null) return false; }
        catch (AdvisorUpdateNoticeException error) { failure.Text = text()["update_notice.error." + error.Code]; report(error); }
        window.Header = text()[notice.TitleKey]; close.Content = text()["action.close"];
        scroll.Content = GUI.ParseXaml(AdvisorMarkdown.Render(text()[notice.BodyKey]).ToString(SaveOptions.DisableFormatting)) as FrameworkElement
            ?? throw new InvalidOperationException("The Markdown notice view is not a FrameworkElement.");
        var width = Math.Min(520, view.ActualWidth - 40); var height = Math.Min(340, view.ActualHeight - 80);
        window.Width = width; window.Height = height;
        window.MinWidth = Math.Min(360, width); window.MinHeight = Math.Min(220, height);
        window.MaxWidth = view.ActualWidth - 20; window.MaxHeight = view.ActualHeight - 40;
        window.X = (float)Math.Max(10, Math.Min(view.ActualWidth - width - 10, owner.X + (owner.Width - width) / 2));
        window.Y = (float)Math.Max(10, Math.Min(view.ActualHeight - height - 10, owner.Y + (owner.Height - height) / 2));
        releaseInput(); current = notice; this.preview = preview; owner.IsEnabled = false;
        try
        {
            WindowHostCommands.OpenWindowCommand.Execute(window, view);
            if (!IsOpen) throw new InvalidOperationException("The game window host did not open the update notice.");
            scroll.ScrollToHome(); close.Focus();
        }
        catch { Hide(); throw; }
        return true;
    }
    internal void Dismiss() { if (IsOpen) window.CloseWindowNow(); }
    private void OnClosed()
    {
        var notice = current; var wasPreview = preview; current = null; preview = false;
        owner.IsEnabled = true;
        if (notice == null) return;
        try { store.MarkRead(notice, wasPreview); }
        catch (AdvisorUpdateNoticeException error) { report(error); }
        closed();
    }
    // Clear the pending acknowledgement before native close events during fault or scene teardown.
    internal void Hide()
    {
        current = null; preview = false;
        try { if (IsOpen) window.CloseWindowNow(); }
        finally { owner.IsEnabled = true; }
    }
    private static T Find<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T
        ?? throw new InvalidOperationException("Missing update notice control: " + name);
}
