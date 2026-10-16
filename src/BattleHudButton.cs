using System;
using System.IO;
using System.Linq;
using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

// Own the added HUD column with its button so scene teardown can restore the shared layout.
internal sealed class BattleHudButton : IDisposable
{
    private readonly Grid grid;
    private readonly ColumnDefinition column;
    internal ToggleButton Control { get; }

    internal BattleHudButton(MainGameView view, string tooltip)
    {
        grid = view.FindName("DisplayAreaGrid") as Grid
            ?? throw new InvalidOperationException("The battle HUD grid is unavailable.");
        var reference = view.FindName("UnitStatusWindow") as ToggleButton
            ?? throw new InvalidOperationException("The battle HUD toggle button is unavailable.");
        var columnIndex = grid.ColumnDefinitions.Count;
        var row = Grid.GetRow(reference);
        if (!grid.Children.Contains(reference) || columnIndex <= Grid.GetColumn(reference) ||
            row >= grid.RowDefinitions.Count)
            throw new InvalidOperationException("The battle HUD toggle layout has changed.");

        using var source = typeof(BattleHudButton).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.BattleHudButton.xaml")
            ?? throw new FileNotFoundException("The embedded battle advisor button view is missing.");
        using var reader = new StreamReader(source);
        Control = GUI.ParseXaml(reader.ReadToEnd()) as ToggleButton
            ?? throw new InvalidOperationException("The battle advisor button view is not a ToggleButton.");
        Control.ToolTip = tooltip;
        Control.Width = reference.Width;
        Control.Height = reference.Height;
        Control.Margin = reference.Margin;
        Grid.SetColumn(Control, columnIndex);
        Grid.SetRow(Control, row);
        column = new ColumnDefinition { Width = GridLength.Auto };
        try
        {
            grid.ColumnDefinitions.Add(column);
            foreach (var child in grid.Children.Cast<UIElement>())
            {
                var span = Grid.GetColumnSpan(child);
                if (span > 1 && Grid.GetColumn(child) + span == columnIndex)
                    Grid.SetColumnSpan(child, span + 1);
            }
            grid.Children.Add(Control);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        grid.Children.Remove(Control);
        var columnIndex = grid.ColumnDefinitions.IndexOf(column);
        if (columnIndex < 0) return;
        // Remove our column from the current layout, including columns appended later by another mod.
        foreach (var child in grid.Children.Cast<UIElement>())
        {
            var start = Grid.GetColumn(child);
            var span = Grid.GetColumnSpan(child);
            if (start > columnIndex) Grid.SetColumn(child, start - 1);
            else if (start < columnIndex && start + span > columnIndex)
                Grid.SetColumnSpan(child, span - 1);
        }
        grid.ColumnDefinitions.Remove(column);
    }
}
