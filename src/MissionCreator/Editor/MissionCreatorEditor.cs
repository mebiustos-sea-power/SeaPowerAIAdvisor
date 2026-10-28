using System;
using System.Linq;
using Noesis;
using Noesis.Interactivity;
using SeapowerUI;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.UI;
using SeaPowerAIMissionCreator.Connections;
using SeaPowerAIMissionCreator.Development;

namespace SeaPowerAIMissionCreator.Editor;

// One editor view owns its button and window. Its own events keep this integration alive.
internal sealed class MissionCreatorEditor
{
    private readonly MissionEditorView view;
    private readonly Action<Exception> report;
    private Grid? parent, header;
    private TextBlock? title;
    private Button? button;
    private WindowHost? host;
    private MissionCreatorWindow? window;
    private MissionCreatorText? text;
    private readonly MissionCreatorConnections connections;
    private readonly MissionCreatorLocalApiDriver driver;

    internal MissionCreatorEditor(MissionEditorView view, MissionCreatorConnections connections, MissionCreatorLocalApiDriver driver, Action<Exception> report)
    {
        this.view = view; this.report = report; this.connections = connections; this.driver = driver;
        view.Loaded += Loaded;
        view.Unloaded += Unloaded;
        if (view.IsLoaded) Loaded(view, null!);
    }

    private void Loaded(object sender, RoutedEventArgs args) => Run(Mount);
    private void Unloaded(object sender, RoutedEventArgs args) => Unmount();
    private void Open(object sender, RoutedEventArgs args) => Run(() =>
    {
        window ??= new MissionCreatorWindow(view, host ?? throw new InvalidOperationException("The editor window host is unavailable."), text ?? throw new InvalidOperationException("Creator text is unavailable."), connections, driver, report);
        window.Show();
    });

    private void Mount()
    {
        if (header != null) return;
        var root = view.Content as Grid ?? throw new InvalidOperationException("The mission editor layout has changed.");
        var left = root.Children.OfType<Grid>().Single(g => Grid.GetColumn(g) == 0);
        var caption = left.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == 0);
        host = Interaction.GetBehaviors(view).OfType<WindowHost>().Single();
        text = MissionCreatorText.Load(GameLanguage.Read());
        parent = left; title = caption;
        header = new Grid { Name = "MissionCreatorHeader" };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        button = new Button
        {
            Name = "OpenMissionCreator", Content = text.Launcher,
            Margin = new Thickness(12, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3),
            MinHeight = 28, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(button, 1);
        button.Click += Open;
        try
        {
            left.Children.Remove(caption);
            header.Children.Add(caption); header.Children.Add(button);
            left.Children.Add(header);
        }
        catch { Unmount(); throw; }
    }

    private void Unmount()
    {
        Run(() => window?.Dispose()); window = null;
        Run(() =>
        {
            if (button != null) button.Click -= Open;
            if (header != null && title != null) header.Children.Remove(title);
            if (parent != null && header != null) parent.Children.Remove(header);
            if (parent != null && title != null && !parent.Children.Contains(title)) parent.Children.Add(title);
        });
        parent = null; header = null; title = null; button = null; host = null;
        // Keep Loaded subscribed: the game's menu can reuse an unloaded editor view.
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { report(error); }
    }
}
