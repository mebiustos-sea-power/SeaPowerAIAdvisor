using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

// This view has no dependency on conversations, profiles, prompts or game-state capture.
internal sealed class AdvisorErrorWindow
{
    private readonly FrameworkElement view;
    private readonly WindowHost host;
    private readonly Window window;
    private readonly Button copy;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double copiedUntil;
    private readonly Action opened;

    internal AdvisorErrorWindow(FrameworkElement view, WindowHost host, AdvisorRuntime runtime, AdvisorText text, Action<bool> setChecked, Action restart)
    {
        this.view = view; this.host = host;
        using var source = typeof(AdvisorErrorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.AdvisorError.xaml")
            ?? throw new FileNotFoundException("The embedded advisor error view is missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var content = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement
            ?? throw new InvalidOperationException("The advisor error view is not a FrameworkElement.");
        Named<TextBlock>(content, "ErrorMessage").Text = text["failure.message"];
        var report = runtime.Report ?? throw new InvalidOperationException("There is no advisor failure to report.");
        Named<TextBlock>(content, "ErrorDetails").Text = report;
        copy = Named<Button>(content, "CopyError");
        copy.Content = text["action.copy"];
        copy.Click += (_, _) => runtime.Present(() =>
        {
            if (!copy.IsEnabled) return;
            UnityEngine.GUIUtility.systemCopyBuffer = report;
            copiedUntil = clock.Elapsed.TotalSeconds + 2;
            copy.IsEnabled = false;
        });
        var restartButton = Named<Button>(content, "RestartAdvisor");
        restartButton.Content = text["failure.restart"];
        restartButton.Click += (_, _) => runtime.Present(restart);
        var close = Named<Button>(content, "CloseError");
        close.Content = text["action.close"];
        close.Click += (_, _) => runtime.Present(Close);
        var width = Math.Min(680, view.ActualWidth - 40);
        var height = Math.Min(520, view.ActualHeight - 80);
        window = new Window
        {
            Header = text["failure.title"] + " v" + typeof(AdvisorErrorWindow).Assembly.GetName().Version!.ToString(3),
            Content = content, Width = width, Height = height,
            MinWidth = Math.Min(360, width), MinHeight = Math.Min(240, height),
            MaxWidth = view.ActualWidth - 20, MaxHeight = view.ActualHeight - 40,
            X = (float)Math.Max(10, view.ActualWidth - width - 30), Y = 60,
            Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        window.OnClosed += (_, _) => runtime.Present(() => setChecked(false));
        opened = () => setChecked(true);
        window.DataContext = window;
        window.Template = view.TryFindResource("WindowLayoutNoScroll") as ControlTemplate
            ?? throw new InvalidOperationException("The game's window template is unavailable.");
    }

    internal bool IsOpen => host.Windows.Contains(window);
    internal void Show()
    {
        if (IsOpen) return;
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!IsOpen) throw new InvalidOperationException("The game window host did not open the error report.");
        opened();
    }
    internal void Close() { if (IsOpen) window.CloseWindowNow(); }
    internal void Tick()
    {
        if (!copy.IsEnabled && clock.Elapsed.TotalSeconds >= copiedUntil) copy.IsEnabled = true;
    }
    private static T Named<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T
        ?? throw new InvalidOperationException("The advisor error control is missing: " + name);
}
