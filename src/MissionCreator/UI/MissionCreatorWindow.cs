using System;
using Noesis;
using SeapowerUI;
using SeaPowerAIMissionCreator.Connections;
using SeaPowerAIMissionCreator.Development;

namespace SeaPowerAIMissionCreator.UI;

// The game owns window presentation and close behavior; no Advisor state is involved.
internal sealed class MissionCreatorWindow : IDisposable
{
    private readonly FrameworkElement view;
    private readonly WindowHost host;
    private readonly Window window;
    private readonly MissionCreatorConnectionView content;

    internal MissionCreatorWindow(FrameworkElement view, WindowHost host, MissionCreatorText text, MissionCreatorConnections connections, MissionCreatorLocalApiDriver driver, Action<Exception> report)
    {
        this.view = view; this.host = host;
        var width = Math.Min(760, view.ActualWidth - 40);
        var height = Math.Min(500, view.ActualHeight - 80);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The mission editor has not completed layout.");
        window = new Window
        {
            Header = text.Title, Content = new Grid(), Width = width, Height = height,
            MinWidth = Math.Min(360, width), MinHeight = Math.Min(240, height),
            MaxWidth = view.ActualWidth - 20, MaxHeight = view.ActualHeight - 40,
            X = (float)((view.ActualWidth - width) / 2), Y = (float)((view.ActualHeight - height) / 2),
            Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        window.DataContext = window;
        // The editor's standard windows import this dictionary within their own template scope.
        var resources = GUI.LoadXaml("Assets/Scripts/Campaign/Window.xaml") as ResourceDictionary
            ?? throw new InvalidOperationException("The game's editor window resources are unavailable.");
        window.Resources.MergedDictionaries.Add(resources);
        window.Template = resources["WindowLayoutNoScroll"] as ControlTemplate
            ?? throw new InvalidOperationException("The game's editor window template is unavailable.");
        content = new MissionCreatorConnectionView(connections, driver, text, report); window.Content = content.Root;
        window.Unloaded += (_, _) => content.Hidden();
    }

    internal void Show()
    {
        if (host.Windows.Contains(window)) return;
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!host.Windows.Contains(window)) throw new InvalidOperationException("The editor window host did not open Mission Creator.");
    }

    public void Dispose() { Close(); content.Dispose(); }

    internal void Close()
    {
        if (host.Windows.Contains(window)) window.CloseWindowNow();
    }
}
