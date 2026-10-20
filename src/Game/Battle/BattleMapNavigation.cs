using System;
using System.Diagnostics;
using MapControl;
using Noesis;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed class BattleMapNavigation : IAdvisorMapNavigation, IDisposable
{
    private readonly Func<SceneMapViewModel?> map;
    private readonly Stopwatch highlightAge = new();
    private SceneMapViewModel? highlightMap;
    private MapPanel? highlight;

    internal BattleMapNavigation(Func<SceneMapViewModel?> map) { this.map = map; }
    public void Focus(double latitude, double longitude)
    {
        ClearHighlight();
        var current = map();
        if (current == null) return;
        var location = new GeoPosition(latitude, longitude, 0);
        current.DisplayMap.Center = location;
        var visual = GUI.ParseXaml(HighlightMarkup) as MapPanel
            ?? throw new InvalidOperationException("The coordinate highlight is not a map panel.");
        var marker = visual.FindName("CoordinatePoint") as FrameworkElement
            ?? throw new InvalidOperationException("The coordinate highlight marker is missing.");
        MapPanel.SetPosition(marker, location);
        highlightMap = current; highlight = visual;
        // This overlay is not part of DrawingLayer, so it cannot be edited or saved as a user drawing.
        current.DisplayMap.ForegroundElements.Add(visual);
        highlightAge.Restart();
    }

    internal void Tick()
    {
        if (highlight != null && (highlightAge.Elapsed.TotalSeconds >= 3 || !ReferenceEquals(highlightMap, map())))
            ClearHighlight();
    }

    public void Dispose() => ClearHighlight();

    private void ClearHighlight()
    {
        var previous = highlight; var owner = highlightMap;
        highlight = null; highlightMap = null; highlightAge.Reset();
        if (previous == null) return;
        try { owner?.DisplayMap.ForegroundElements.Remove(previous); }
        finally
        {
            BindingOperations.ClearAllBindings(previous);
            MapPanel.SetParentMap(previous, null!);
        }
    }

    private const string HighlightMarkup = @"<map:MapPanel
        xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        xmlns:map=""clr-namespace:MapControl;assembly=Seapower-Scripts""
        map:MapPanel.ParentMap=""{Binding RelativeSource={RelativeSource Mode=FindAncestor, AncestorType=map:MapBase}}""
        IsHitTestVisible=""False"">
        <Grid x:Name=""CoordinatePoint"" Width=""34"" Height=""34"" HorizontalAlignment=""Center"" VerticalAlignment=""Center"">
            <Ellipse Width=""24"" Height=""24"" Stroke=""#CC000000"" StrokeThickness=""5"" />
            <Ellipse Width=""24"" Height=""24"" Stroke=""#FFE060"" StrokeThickness=""3"" />
            <Path Data=""M 17,0 L 17,10 M 17,24 L 17,34 M 0,17 L 10,17 M 24,17 L 34,17"" Stroke=""#CC000000"" StrokeThickness=""5"" />
            <Path Data=""M 17,0 L 17,10 M 17,24 L 17,34 M 0,17 L 10,17 M 24,17 L 34,17"" Stroke=""#FFE060"" StrokeThickness=""2"" />
            <Ellipse Width=""4"" Height=""4"" Fill=""#FFE060"" />
        </Grid>
    </map:MapPanel>";
}
