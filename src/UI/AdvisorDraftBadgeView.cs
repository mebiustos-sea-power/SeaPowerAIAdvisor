using System;
using System.Collections.Generic;
using Noesis;

namespace SeaPowerAIAdvisor;

// Overlay native controls and reserve only the editor's inner padding; its frame never changes size.
internal sealed class AdvisorDraftBadgeView
{
    private readonly TextBox question;
    private readonly ScrollViewer area;
    private readonly WrapPanel panel;
    private readonly Action<int> removeUnit;
    private readonly Action<AdvisorLocation> removeLocation;
    private readonly Action<Action> guard;
    private readonly Brush background;
    private readonly Brush border = new SolidColorBrush(Color.FromArgb(255, 96, 112, 128));

    internal AdvisorDraftBadgeView(TextBox question, ScrollViewer area, WrapPanel panel, Brush background, Action<int> removeUnit, Action<AdvisorLocation> removeLocation, Action<Action> guard)
    {
        this.question = question; this.area = area; this.panel = panel; this.removeUnit = removeUnit; this.removeLocation = removeLocation; this.guard = guard;
        this.background = background;
        area.SizeChanged += (_, _) => guard(UpdatePadding);
    }

    internal void Show(IReadOnlyList<(int Id, string Name)> units, IReadOnlyList<AdvisorLocation> locations)
    {
        panel.Children.Clear();
        foreach (var unit in units) Add(AdvisorBadge.Caption(unit.Id, unit.Name), () => removeUnit(unit.Id));
        foreach (var location in locations) Add(location.Label, () => removeLocation(location));
        area.Visibility = panel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePadding();
    }

    private void Add(string caption, Action remove)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = caption, FontSize = 12, MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", FontSize = 12, Width = 18, Height = 18,
            Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        close.Click += (_, e) => { e.Handled = true; guard(remove); };
        row.Children.Add(close);
        panel.Children.Add(new Border { Child = row, Height = 22, Background = background,
            BorderBrush = border, BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 2, 1),
            Margin = new Thickness(0, 0, 6, 2), CornerRadius = new CornerRadius(3) });
    }

    internal void SetBusy(bool busy) => area.IsEnabled = !busy;

    private void UpdatePadding() => question.Padding = area.Visibility == Visibility.Visible
        ? new Thickness(8, 8 + area.ActualHeight + 4, 8, 8) : new Thickness(8);
}
