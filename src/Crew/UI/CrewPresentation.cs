using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Noesis;
using UnityEngine;
using Image = Noesis.Image;
using Grid = Noesis.Grid;

namespace SeaPowerAIAdvisor.Crew;

// One battle owns the portrait texture and click-through notification; neither retains game units.
internal sealed class CrewPresentation : IDisposable
{
    private readonly Texture2D texture;
    internal TextureSource Portrait { get; }
    private readonly Grid layer;
    private readonly Grid host;
    private readonly Border notice;
    private readonly TextBlock heading, body;
    private double until;
    private CrewNotificationPosition? position;
    internal CrewPresentation(Grid host)
    {
        this.host = host;
        using var image = typeof(CrewPresentation).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Crew.png") ?? throw new FileNotFoundException("The crew portrait is missing.");
        using var bytes = new MemoryStream(); image.CopyTo(bytes);
        texture = new Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, bytes.ToArray(), true)) throw new InvalidOperationException("The crew portrait is invalid.");
            texture.name = "AI Crew portrait"; texture.filterMode = FilterMode.Bilinear; texture.wrapMode = TextureWrapMode.Clamp;
            Portrait = new TextureSource(texture);
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
        try
        {
            layer = new Grid { IsHitTestVisible = false };
            Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count));
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new Image { Source = Portrait, Width = 48, Height = 48, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
            var words = new StackPanel(); heading = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
            body = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 5, 0, 0) };
            words.Children.Add(heading); words.Children.Add(body); Grid.SetColumn(words, 1); content.Children.Add(words);
            notice = new Border { Child = content, Width = 440, Background = new SolidColorBrush(Noesis.Color.FromArgb(235, 24, 35, 45)),
                BorderBrush = new SolidColorBrush(Noesis.Color.FromArgb(255, 110, 129, 143)), BorderThickness = new Thickness(1), Padding = new Thickness(12),
                Visibility = Visibility.Collapsed };
            layer.Children.Add(notice);
            host.Children.Add(layer);
        }
        catch { Portrait.Dispose(); UnityEngine.Object.Destroy(texture); throw; }
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
    internal void Tick(CrewReports reports, double now, int seconds, CrewNotificationPosition notificationPosition, bool hudVisible)
    {
        Place(notificationPosition);
        layer.Visibility = hudVisible ? Visibility.Visible : Visibility.Collapsed;
        notice.MaxWidth = Math.Max(1, host.ActualWidth - 36);
        if (reports.Paused || !reports.HasEnabledTargets) { Clear(); return; }
        if (now < until) return;
        notice.Visibility = Visibility.Collapsed;
        if (!hudVisible) return;
        var report = reports.NextNotification(now); if (report == null) return;
        heading.Text = report.Target.Name + " / " + report.Submission.Person.Name; body.Text = report.Submission.Text;
        notice.Visibility = Visibility.Visible; until = now + seconds;
    }
    internal JObject Status()
    {
        var bounds = notice.TransformToVisual(host).TransformBounds(new Noesis.Rect(0, 0, notice.ActualWidth, notice.ActualHeight));
        return new JObject { ["visible"] = layer.IsVisible && notice.IsVisible,
            ["heading"] = heading.Text, ["text"] = body.Text, ["position"] = position.HasValue ? CrewSettings.PositionCode(position.Value) : null,
            ["bounds"] = new JObject { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height } };
    }
    internal void Clear() { until = 0; notice.Visibility = Visibility.Collapsed; }
    public void Dispose() { host.Children.Remove(layer); Portrait.Dispose(); UnityEngine.Object.Destroy(texture); }
}
