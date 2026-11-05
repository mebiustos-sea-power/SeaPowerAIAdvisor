using System;
using Newtonsoft.Json.Linq;
using Noesis;
using Image = Noesis.Image;
using Grid = Noesis.Grid;

namespace SeaPowerAIAdvisor.Crew;

// Displays click-through notifications using the battle-owned portraits; never retains game units.
internal sealed class CrewPresentation : IDisposable
{
    private readonly CrewPortraits portraits;
    private readonly Image portrait;
    internal string? CurrentPortraitFile => current?.Submission.Person.PortraitFile;
    private readonly Grid layer;
    private readonly Grid host;
    private readonly Border notice;
    private readonly TextBlock heading, body;
    private double until;
    private CrewRemark? current;
    private bool showRanks, rankBeforeName;
    private CrewNotificationPosition? position;
    internal CrewPresentation(Grid host, CrewPortraits portraits)
    {
        this.host = host; this.portraits = portraits;
        try
        {
            layer = new Grid { IsHitTestVisible = false };
            Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count));
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            portrait = new Image { Source = portraits.Common, Width = 48, Height = 48, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            content.Children.Add(portrait);
            var words = new StackPanel(); heading = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
            body = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 5, 0, 0) };
            words.Children.Add(heading); words.Children.Add(body); Grid.SetColumn(words, 1); content.Children.Add(words);
            notice = new Border { Child = content, Width = 440, Background = new SolidColorBrush(Noesis.Color.FromArgb(235, 24, 35, 45)),
                BorderBrush = new SolidColorBrush(Noesis.Color.FromArgb(255, 110, 129, 143)), BorderThickness = new Thickness(1), Padding = new Thickness(12),
                Visibility = Visibility.Collapsed };
            layer.Children.Add(notice);
            host.Children.Add(layer);
        }
        catch { if (layer != null) host.Children.Remove(layer); throw; }
    }

    private void Place(CrewNotificationPosition value)
    {
        if (position == value) return;
        notice.HorizontalAlignment = value switch
        {
            CrewNotificationPosition.TopLeft or CrewNotificationPosition.BottomLeft => HorizontalAlignment.Left,
            CrewNotificationPosition.TopCenter or CrewNotificationPosition.BottomCenter => HorizontalAlignment.Center,
            CrewNotificationPosition.TopRight or CrewNotificationPosition.BottomRight => HorizontalAlignment.Right,
            _ => throw new CrewException("settings_invalid")
        };
        var top = value is CrewNotificationPosition.TopLeft or CrewNotificationPosition.TopCenter or CrewNotificationPosition.TopRight;
        notice.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        notice.Margin = top ? new Thickness(18, 60, 18, 0) : new Thickness(18, 0, 18, 90);
        position = value;
    }
    internal void Tick(CrewChatter chatter, double now, int seconds, CrewNotificationPosition notificationPosition, bool showRanks, bool hudVisible)
    {
        Place(notificationPosition);
        layer.Visibility = hudVisible ? Visibility.Visible : Visibility.Collapsed;
        notice.MaxWidth = Math.Max(1, host.ActualWidth - 36);
        if (chatter.Paused || !chatter.HasEnabledTargets) { Clear(); return; }
        if (current != null && !chatter.IsCurrent(current)) Clear();
        if (current != null && (this.showRanks != showRanks || rankBeforeName != chatter.RankBeforeName)) SetHeading(current, showRanks, chatter.RankBeforeName);
        if (now < until) return;
        notice.Visibility = Visibility.Collapsed; current = null; portrait.Source = portraits.Common;
        if (!hudVisible) return;
        var remark = chatter.NextNotification(now); if (remark == null) return;
        current = remark; portrait.Source = portraits.Get(remark.Submission.Person); SetHeading(remark, showRanks, chatter.RankBeforeName); body.Text = remark.Submission.Text;
        notice.Visibility = Visibility.Visible; until = now + seconds;
    }
    private void SetHeading(CrewRemark remark, bool showRanks, bool rankBeforeName)
    {
        this.showRanks = showRanks; this.rankBeforeName = rankBeforeName;
        heading.Text = remark.Target.Name + " / " + remark.Submission.Person.DisplayName(showRanks, rankBeforeName);
    }
    internal JObject Status()
    {
        var bounds = notice.TransformToVisual(host).TransformBounds(new Noesis.Rect(0, 0, notice.ActualWidth, notice.ActualHeight));
        return new JObject { ["visible"] = layer.IsVisible && notice.IsVisible,
            ["heading"] = heading.Text, ["text"] = body.Text, ["position"] = position.HasValue ? CrewSettings.PositionCode(position.Value) : null,
            ["bounds"] = new JObject { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height } };
    }
    internal void Clear() { until = 0; current = null; portrait.Source = portraits.Common; notice.Visibility = Visibility.Collapsed; }
    public void Dispose() { Clear(); host.Children.Remove(layer); }
}
