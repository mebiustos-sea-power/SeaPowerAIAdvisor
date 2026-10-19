using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// Owns the temporary footer layout and restores the game's original button on removal.
internal sealed class AdvisorContactButton : IDisposable
{
    private readonly Panel parent;
    private readonly Button original;
    private readonly StackPanel row;
    private readonly TextBlock caption;
    internal Button Control { get; }

    internal AdvisorContactButton(Button reset)
    {
        original = reset;
        parent = VisualTreeHelper.GetParent(reset) as Panel
            ?? throw new InvalidOperationException("The contact information footer is not a panel.");
        row = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = reset.HorizontalAlignment, VerticalAlignment = reset.VerticalAlignment };
        Grid.SetColumn(row, Grid.GetColumn(reset)); Grid.SetRow(row, Grid.GetRow(reset));
        Grid.SetColumnSpan(row, Grid.GetColumnSpan(reset)); Grid.SetRowSpan(row, Grid.GetRowSpan(reset));
        DockPanel.SetDock(row, DockPanel.GetDock(reset));
        caption = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Control = new Button { Content = caption, MinWidth = 100, MaxWidth = 280,
            Margin = reset.Margin, VerticalAlignment = VerticalAlignment.Stretch };
        var index = parent.Children.IndexOf(reset);
        parent.Children.Remove(reset);
        row.Children.Add(Control); row.Children.Add(reset);
        parent.Children.Insert(index, row);
    }
    internal void Update(string text, bool enabled)
    {
        if (caption.Text != text) caption.Text = text;
        Control.IsEnabled = enabled;
    }
    public void Dispose()
    {
        var index = parent.Children.IndexOf(row);
        if (index < 0) return;
        row.Children.Remove(original); parent.Children.Remove(row);
        parent.Children.Insert(index, original);
    }
}
