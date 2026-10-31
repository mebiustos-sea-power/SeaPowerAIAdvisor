using System;
using System.Linq;
using System.IO;
using Noesis;

namespace SeaPowerAIAdvisor.Shared;

// Shared HUD tile visuals and placement; consultation state stays with the caller.
internal sealed class HudButton : IDisposable
{
    private readonly Grid grid;
    private readonly ColumnDefinition column = new() { Width = GridLength.Auto };
    internal ToggleButton Control { get; }
    internal Ellipse Indicator { get; }
    internal HudButton(Grid grid, ToggleButton reference, string label, string tooltip, float fontSize = 14, UIElement? before = null)
    {
        this.grid = grid;
        using var source = typeof(HudButton).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Shared.HudButton.xaml")
            ?? throw new FileNotFoundException("The embedded HUD button view is missing.");
        using var reader = new StreamReader(source);
        Control = GUI.ParseXaml(reader.ReadToEnd()) as ToggleButton ?? throw new InvalidOperationException("The HUD button view is invalid.");
        Indicator = Control.Content as Ellipse ?? throw new InvalidOperationException("The HUD button indicator is missing.");
        Control.Tag = label; Control.ToolTip = tooltip; Control.FontSize = fontSize;
        var count = grid.ColumnDefinitions.Count; var row = Grid.GetRow(reference);
        if (!grid.Children.Contains(reference) || count <= Grid.GetColumn(reference) || row >= grid.RowDefinitions.Count ||
            (before != null && !grid.Children.Contains(before)))
            throw new InvalidOperationException("The HUD toggle layout has changed.");
        var index = before == null ? count : Grid.GetColumn(before);
        Control.Width = double.IsNaN(reference.Width) ? reference.ActualWidth : reference.Width;
        Control.Height = double.IsNaN(reference.Height) ? reference.ActualHeight : reference.Height;
        Control.Margin = reference.Margin; Grid.SetColumn(Control, index); Grid.SetRow(Control, row);
        try
        {
            grid.ColumnDefinitions.Insert(index, column);
            foreach (var child in grid.Children.Cast<UIElement>())
            {
                var start = Grid.GetColumn(child); var span = Grid.GetColumnSpan(child);
                if (start >= index) Grid.SetColumn(child, start + 1);
                else if (span > 1 && (start + span > index || start + span == count)) Grid.SetColumnSpan(child, span + 1);
            }
            grid.Children.Add(Control);
        }
        catch { Dispose(); throw; }
    }
    internal void FitLabel(string compactLabel)
    {
        var text = GameUi.FindVisual<TextBlock>(Control) ?? throw new InvalidOperationException("The HUD label is unavailable.");
        var tile = GameUi.FindVisual<Border>(Control) ?? throw new InvalidOperationException("The HUD tile is unavailable.");
        text.Measure(new Size(float.PositiveInfinity, float.PositiveInfinity));
        var available = Math.Min(Control.Width, tile.MaxWidth) - tile.BorderThickness.Left - tile.BorderThickness.Right;
        if (text.DesiredSize.Width > available) Control.Tag = compactLabel;
    }
    public void Dispose()
    {
        grid.Children.Remove(Control); var index = grid.ColumnDefinitions.IndexOf(column); if (index < 0) return;
        foreach (var child in grid.Children.Cast<UIElement>())
        {
            var start = Grid.GetColumn(child); var span = Grid.GetColumnSpan(child);
            if (start > index) Grid.SetColumn(child, start - 1);
            else if (start < index && start + span > index) Grid.SetColumnSpan(child, span - 1);
        }
        grid.ColumnDefinitions.Remove(column);
    }
}
