using System;
using System.Collections.Generic;
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
    private readonly Action<Action> guard;
    private readonly Action releaseInput, closed;
    private readonly Action<AdvisorUpdateNoticeException> report;
    private AdvisorUpdateNotice? current;
    private bool preview;
    internal AdvisorUpdateNoticeWindow(FrameworkElement view, WindowHost host, Window owner, AdvisorUpdateNoticeStore store,
        Func<AdvisorText> text, Action<Action> guard, Action releaseInput, Action closed, Action<AdvisorUpdateNoticeException> report)
    {
        this.view = view; this.host = host; this.owner = owner; this.store = store; this.text = text;
        this.guard = guard; this.releaseInput = releaseInput; this.closed = closed; this.report = report;
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
        window.Resources["Brush.Window.Header"] = new SolidColorBrush(Color.FromArgb(255, 40, 84, 58));
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
        var version = typeof(AdvisorUpdateNoticeWindow).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The advisor assembly version is missing.");
        window.Header = text().Format(notice.TitleKey, version.ToString(3)); close.Content = text()["action.close"];
        scroll.Content = RenderBody(text()[notice.BodyKey]);
        var width = Math.Min(520, view.ActualWidth - 40); var height = Math.Min(620, view.ActualHeight - 80);
        window.Width = width; window.Height = height;
        window.MinWidth = Math.Min(360, width); window.MinHeight = Math.Min(220, height);
        window.MaxWidth = view.ActualWidth - 20; window.MaxHeight = view.ActualHeight - 40;
        // Native maximization uses automatic (NaN) dimensions and preserves the normal X/Y.
        var centerX = owner.Maximized ? view.ActualWidth / 2 : owner.X + owner.Width / 2;
        var centerY = owner.Maximized ? view.ActualHeight / 2 : owner.Y + owner.Height / 2;
        window.X = (float)Math.Max(10, Math.Min(view.ActualWidth - width - 10, centerX - width / 2));
        window.Y = (float)Math.Max(10, Math.Min(view.ActualHeight - height - 10, centerY - height / 2));
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
    // Only developer-authored notices attach browser actions; conversation links retain their own policy.
    private FrameworkElement RenderBody(string markdown)
    {
        var links = new List<(string Name, string Url)>();
        var markup = Shared.MarkdownRenderer.Render(markdown, (destination, label) =>
        {
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
            var name = "UpdateNoticeLink" + links.Count;
            links.Add((name, uri.AbsoluteUri));
            return Shared.UiMarkup.Element("Hyperlink", new XAttribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"), name),
                new XAttribute("TextDecorations", "Underline"), new XAttribute("Focusable", "False"), label);
        });
        var body = GUI.ParseXaml(markup.ToString(SaveOptions.DisableFormatting)) as FrameworkElement
            ?? throw new InvalidOperationException("The Markdown notice view is not a FrameworkElement.");
        foreach (var link in links)
            Find<Hyperlink>(body, link.Name).Click += (_, e) => { e.Handled = true; guard(() => UnityEngine.Application.OpenURL(link.Url)); };
        return body;
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
