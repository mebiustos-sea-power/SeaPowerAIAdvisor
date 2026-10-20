using System;
using System.Collections.Generic;
using System.Globalization;
using Noesis;

namespace SeaPowerAIAdvisor;

// Overlay native controls and reserve only the editor's inner padding; its frame never changes size.
internal sealed class AdvisorUnitBadgeView
{
    private readonly TextBox question;
    private readonly ScrollViewer area;
    private readonly WrapPanel panel;
    private readonly Action<int> remove;
    private readonly Action<Action> guard;
    private readonly Brush background;
    private readonly Brush border = new SolidColorBrush(Color.FromArgb(255, 96, 112, 128));

    internal AdvisorUnitBadgeView(TextBox question, ScrollViewer area, WrapPanel panel, Brush background, Action<int> remove, Action<Action> guard)
    {
        this.question = question; this.area = area; this.panel = panel; this.remove = remove; this.guard = guard;
        this.background = background;
        area.SizeChanged += (_, _) => guard(UpdatePadding);
    }

    internal void Show(IReadOnlyList<(int Id, string Name)> units)
    {
        panel.Children.Clear();
        foreach (var unit in units)
        {
            var number = unit.Id.ToString(CultureInfo.InvariantCulture);
            var caption = unit.Name == "Contact " + number ? number : unit.Name;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = caption, FontSize = 12, MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var close = new Button { Content = "×", FontSize = 12, Width = 18, Height = 18,
                Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
            close.Click += (_, e) => { e.Handled = true; guard(() => remove(unit.Id)); };
            row.Children.Add(close);
            panel.Children.Add(new Border { Child = row, Height = 22, Background = background,
                BorderBrush = border, BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 2, 1),
                Margin = new Thickness(0, 0, 6, 2), CornerRadius = new CornerRadius(3) });
        }
        area.Visibility = units.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePadding();
    }

    internal void SetBusy(bool busy) => area.IsEnabled = !busy;

    private void UpdatePadding() => question.Padding = area.Visibility == Visibility.Visible
        ? new Thickness(8, 8 + area.ActualHeight + 4, 8, 8) : new Thickness(8);
}
