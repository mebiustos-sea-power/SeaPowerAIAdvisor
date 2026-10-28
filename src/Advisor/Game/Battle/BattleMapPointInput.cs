using System;
using MapControl;
using Noesis;
using SeapowerUI;
using SeapowerUI.ViewModels;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Listen on the existing battle view before its map receives the gesture; no global input patch or picking mode.
internal sealed class BattleMapPointInput : IDisposable
{
    private static readonly Type defaultState = typeof(InteractionStateBroker).Assembly.GetType("SeapowerUI.ViewModels.DefaultInteractionState", true)!;
    private static readonly Type selectedState = typeof(InteractionStateBroker).Assembly.GetType("SeapowerUI.ViewModels.UnitSelectedState", true)!;
    private readonly FrameworkElement view;
    private readonly Func<MapViewModel?> expectedMap;
    private readonly Func<IInteractionState?> state;
    private readonly Func<bool> enabled;
    private readonly Action<AdvisorMapCoordinate> insert;
    private readonly Action<Action> guard;

    internal BattleMapPointInput(FrameworkElement view, Func<MapViewModel?> expectedMap, Func<IInteractionState?> state,
        Func<bool> enabled, Action<AdvisorMapCoordinate> insert, Action<Action> guard)
    {
        this.view = view; this.expectedMap = expectedMap; this.state = state;
        this.enabled = enabled; this.insert = insert; this.guard = guard;
        view.PreviewMouseLeftButtonDown += MouseDown;
    }
    private void MouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.Handled || args.ButtonState != MouseButtonState.Pressed || view.Keyboard.Modifiers != ModifierKeys.Control) return;
        guard(() =>
        {
            if (!enabled()) return;
            var currentState = state()?.GetType();
            if (currentState != defaultState && currentState != selectedState) return;
            var expected = expectedMap();
            for (var node = args.Source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not MapBase map || !ReferenceEquals(map.DataContext, expected)) continue;
                var point = args.GetPosition(map);
                if (!IsBackground(map, point, expected!)) return;
                var coordinate = map.ViewToLocation(point).GeoPosition;
                // Consume the gesture before requesting editor focus; never also start a native drag or selection.
                args.Handled = true;
                insert(new AdvisorMapCoordinate(coordinate._latitude, coordinate._longitude));
                return;
            }
        });
    }

    private static bool IsBackground(MapBase map, Point point, MapViewModel expected)
    {
        var background = false;
        VisualTreeHelper.HitTest(map, visual => visual is UIElement element && (!element.IsVisible || !element.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            hit =>
            {
                for (var node = hit.VisualHit; node != null; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement element && element.DataContext != null)
                    {
                        background = ReferenceEquals(element.DataContext, expected);
                        return HitTestResultBehavior.Stop;
                    }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(point));
        return background;
    }
    public void Dispose() => view.PreviewMouseLeftButtonDown -= MouseDown;
}
