using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using MapControl;
using Noesis;
using SeaPower;
using SeapowerUI.ViewModels;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed class BattleMapNavigation : IAdvisorMapNavigation, IDisposable
{
    private readonly Func<SceneMapViewModel?> map;
    private readonly List<Overlay> overlays = new();
    private readonly Stopwatch highlightAge = new();
    private IDisposable? highlight;
    private bool disposed;
    private readonly int thread = System.Environment.CurrentManagedThreadId;

    internal BattleMapNavigation(Func<SceneMapViewModel?> map) { this.map = map; }
    public void Center(AdvisorMapCoordinate location)
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(BattleMapNavigation));
        var current = map();
        if (current != null) current.DisplayMap.Center = Position(location);
    }
    public void Focus(double latitude, double longitude)
    {
        ClearHighlight();
        if (map() == null) return;
        var location = new AdvisorMapCoordinate(latitude, longitude);
        Center(location);
        highlight = Draw(new[] { AdvisorMapDrawing.Point(location) });
        highlightAge.Restart();
    }
    public IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings)
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(BattleMapNavigation));
        var current = map() ?? throw new InvalidOperationException("The battle map is unavailable for drawing.");
        var visual = GUI.ParseXaml(OverlayMarkup) as MapPanel
            ?? throw new InvalidOperationException("The advisor map overlay is not a map panel.");
        var overlay = new Overlay(this, current, visual);
        overlays.Add(overlay);
        try
        {
            foreach (var drawing in drawings) visual.Children.Add(Element(drawing));
            // Foreground overlays are separate from DrawingLayer and cannot be edited or saved as user drawings.
            current.DisplayMap.ForegroundElements.Add(visual);
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }
    internal void Tick()
    {
        CheckThread();
        if (highlight != null && highlightAge.Elapsed.TotalSeconds >= 3) ClearHighlight();
        var current = map();
        foreach (var overlay in overlays.Where(item => !ReferenceEquals(item.Map, current)).ToArray()) overlay.Dispose();
    }
    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        disposed = true;
        ClearHighlight();
        foreach (var overlay in overlays.ToArray()) overlay.Dispose();
    }
    private void ClearHighlight()
    {
        var previous = highlight; highlight = null; highlightAge.Reset(); previous?.Dispose();
    }
    private void CheckThread()
    {
        if (thread != System.Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Map operations must run on their owning game thread.");
    }
    private static GeoPosition Position(AdvisorMapCoordinate value) => new(value.Latitude, value.Longitude, 0);
    private static FrameworkElement Element(AdvisorMapDrawing drawing)
    {
        if (drawing.Kind == AdvisorMapDrawingKind.Point)
        {
            var markup = PointMarkup.Replace("__COLOR__", "#" + drawing.Color.ToString("X8", CultureInfo.InvariantCulture))
                .Replace("__WIDTH__", drawing.Width.ToString(CultureInfo.InvariantCulture))
                .Replace("__RINGWIDTH__", (drawing.Width + 1).ToString(CultureInfo.InvariantCulture));
            var visual = GUI.ParseXaml(markup) as Grid
                ?? throw new InvalidOperationException("The advisor map point is not a grid.");
            var label = visual.FindName("PointLabel") as TextBlock
                ?? throw new InvalidOperationException("The advisor map point label is missing.");
            label.Text = drawing.Label;
            (visual.FindName("PointCaption") as Border
                ?? throw new InvalidOperationException("The advisor map point caption is missing.")).Visibility =
                drawing.Label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            MapPanel.SetPosition(visual, Position(drawing.Coordinates[0]));
            return visual;
        }
        IEnumerable<GeoPosition> locations;
        if (drawing.Kind == AdvisorMapDrawingKind.Line) locations = drawing.Coordinates.Select(Position).ToArray();
        else if (drawing.Kind == AdvisorMapDrawingKind.Circle)
        {
            var center = drawing.Coordinates[0]; var origin = new Location(center.Latitude, center.Longitude);
            // Native MapEllipse axes are degrees. Use the game's spherical calculation for a radius in nautical miles.
            locations = Enumerable.Range(0, 73).Select(index => origin.GetLocation(index * 5, drawing.RadiusNauticalMiles * 1852).GeoPosition).ToArray();
        }
        else throw new InvalidOperationException("Unknown advisor map drawing kind.");
        var color = drawing.Color;
        var line = GUI.ParseXaml(LineMarkup) as MapPolyline
            ?? throw new InvalidOperationException("The advisor map line is not a map polyline.");
        line.Locations = locations; line.StrokeThickness = drawing.Width;
        line.Stroke = new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        return line;
    }

    private sealed class Overlay : IDisposable
    {
        private readonly BattleMapNavigation owner;
        internal SceneMapViewModel Map { get; }
        private MapPanel? visual;
        internal Overlay(BattleMapNavigation owner, SceneMapViewModel map, MapPanel visual)
        { this.owner = owner; Map = map; this.visual = visual; }
        public void Dispose()
        {
            owner.CheckThread();
            var previous = visual; visual = null;
            if (previous == null) return;
            owner.overlays.Remove(this);
            try { Map.DisplayMap.ForegroundElements.Remove(previous); }
            finally
            {
                try { BindingOperations.ClearAllBindings(previous); }
                finally
                {
                    MapPanel.SetParentMap(previous, null!);
                    for (var index = 0; index < previous.Children.Count; index++)
                    {
                        if (previous.Children[index] is FrameworkElement element)
                        {
                            BindingOperations.ClearAllBindings(element);
                            MapPanel.SetParentMap(element, null!);
                        }
                    }
                    previous.Children.Clear();
                }
            }
        }
    }

    private const string OverlayMarkup = @"<map:MapPanel
        xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:map=""clr-namespace:MapControl;assembly=Seapower-Scripts""
        map:MapPanel.ParentMap=""{Binding RelativeSource={RelativeSource Mode=FindAncestor, AncestorType=map:MapBase}}""
        IsHitTestVisible=""False"" />";

    private const string LineMarkup = @"<map:MapPolyline
        xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:map=""clr-namespace:MapControl;assembly=Seapower-Scripts""
        map:MapPanel.ParentMap=""{Binding RelativeSource={RelativeSource Mode=FindAncestor, AncestorType=map:MapBase}}""
        IsHitTestVisible=""False"" />";

    private const string PointMarkup = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Width=""34"" Height=""34"" HorizontalAlignment=""Center"" VerticalAlignment=""Center"" IsHitTestVisible=""False"">
        <Ellipse Width=""24"" Height=""24"" Stroke=""#CC000000"" StrokeThickness=""5"" />
        <Ellipse Width=""24"" Height=""24"" Stroke=""__COLOR__"" StrokeThickness=""__RINGWIDTH__"" />
        <Path Data=""M 17,0 L 17,10 M 17,24 L 17,34 M 0,17 L 10,17 M 24,17 L 34,17"" Stroke=""#CC000000"" StrokeThickness=""5"" />
        <Path Data=""M 17,0 L 17,10 M 17,24 L 17,34 M 0,17 L 10,17 M 24,17 L 34,17"" Stroke=""__COLOR__"" StrokeThickness=""__WIDTH__"" />
        <Ellipse Width=""4"" Height=""4"" Fill=""__COLOR__"" />
        <Border x:Name=""PointCaption"" Background=""#CC101820"" Padding=""3,1"" Margin=""30,-6,-220,0""
            HorizontalAlignment=""Left"" VerticalAlignment=""Top"">
            <TextBlock x:Name=""PointLabel"" FontSize=""12"" Foreground=""#FFFFFF"" />
        </Border>
    </Grid>";
}
