using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noesis;
using SeaPowerAIAdvisor.Shared;
using UnityEngine;
using Grid = Noesis.Grid;
using GUI = Noesis.GUI;
using Rect = Noesis.Rect;

namespace SeaPowerAIAdvisor.Translation;

// One screen owns selection and its transient visuals. No screen names, mission models or game commands enter this UI.
internal sealed class TranslationOverlay : IDisposable
{
    private readonly FrameworkElement view;
    private readonly Grid layer;
    private readonly Border shield, outline, help;
    private readonly Thumb launcher;
    private readonly TextBlock helpText;
    private readonly Func<string, string> text;
    private readonly Func<Exception, string> message;
    private readonly Func<TranslationBatch, CancellationToken, Task<TranslationResult>> translate;
    private readonly Action<Exception> report;
    private readonly Action<Point> moved, savePosition;
    private readonly List<FrameworkElement> candidates = new();
    private FrameworkElement? leaf;
    private int index, releaseFrame;
    private bool selecting, toggle, dragging, disposed;
    private float dragDistance, width, height;
    private ScreenTextSnapshot? afterRelease;
    private ScreenTextSnapshot? snapshot;
    private float clearStatusAt;
    private CancellationTokenSource? cancellation;
    private Task<TranslationResult>? task;
    internal Visual Layer => layer;
    internal TranslationOverlay(FrameworkElement view, Func<string, string> text, Func<Exception, string> message,
        Func<TranslationBatch, CancellationToken, Task<TranslationResult>> translate, Action<Exception> report, Point? position, Action<Point> moved, Action<Point> savePosition)
    {
        this.view = view; this.text = text; this.message = message; this.translate = translate; this.report = report; this.moved = moved; this.savePosition = savePosition;
        using var source = typeof(TranslationOverlay).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.TranslationOverlay.xaml")
            ?? throw new FileNotFoundException("The translation overlay layout is missing.");
        using var reader = new StreamReader(source);
        layer = GUI.ParseXaml(reader.ReadToEnd()) as Grid ?? throw new InvalidDataException("Invalid translation overlay.");
        T Find<T>(string name) where T : class => layer.FindName(name) as T ?? throw new InvalidDataException("Missing translation control: " + name);
        shield = Find<Border>("SelectionShield"); outline = Find<Border>("SelectionOutline"); help = Find<Border>("SelectionHelp"); launcher = Find<Thumb>("TranslationLauncher");
        helpText = Find<TextBlock>("SelectionHelpText"); launcher.Tag = text("translation.button");
        launcher.ToolTip = text("translation.button_hint");
        launcher.DragStarted += (_, _) => { dragging = true; dragDistance = 0; };
        launcher.DragDelta += (_, args) =>
        {
            dragDistance += Math.Abs(args.HorizontalChange) + Math.Abs(args.VerticalChange);
            Position(new Point(Canvas.GetLeft(launcher) + args.HorizontalChange, Canvas.GetTop(launcher) + args.VerticalChange));
        };
        launcher.DragCompleted += (_, args) =>
        {
            dragging = false;
            if (args.Canceled) return;
            if (dragDistance < 5) { toggle = true; releaseFrame = Time.frameCount; }
            else Guard(() => savePosition(new Point(Canvas.GetLeft(launcher), Canvas.GetTop(launcher))));
        };
        shield.PreviewMouseMove += (_, args) => { args.Handled = true; Guard(() => Pick(args.GetPosition(view))); };
        shield.PreviewMouseWheel += (_, args) => { args.Handled = true; Guard(() => { if (candidates.Count == 0) return; index = Math.Max(0, Math.Min(candidates.Count - 1, index + (args.Delta > 0 ? 1 : -1))); Highlight(); }); };
        shield.PreviewMouseDown += (_, args) =>
        {
            args.Handled = true;
            if (args.ChangedButton != MouseButton.Left || candidates.Count == 0) return;
            Guard(() =>
            {
                var captured = ScreenTextReader.Capture(candidates[index], GameLanguage.Read(), layer);
                afterRelease?.Dispose(); afterRelease = captured;
            });
        };
        shield.PreviewMouseUp += (_, args) => { args.Handled = true; if (args.ChangedButton == MouseButton.Left) releaseFrame = Time.frameCount; };
        view.AddLayer(layer);
        try { Resize(); Position(position ?? new Point(Math.Max(12, width - 124), 52)); }
        catch { view.RemoveLayer(layer); throw; }
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { report(error); afterRelease?.Dispose(); afterRelease = null; Status(message(error)); }
    }
    internal void Tick()
    {
        Resize();
        // Keep the shield through mouse-up and one complete native input update. Never use typingActive as a picking-mode lock.
        if (Time.frameCount > releaseFrame + 1 && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
        {
            if (toggle)
            {
                toggle = false;
                Guard(() => { if (task != null) { CancelRequest(); return; } if (!selecting) view.Keyboard.ClearFocus(); SetSelecting(!selecting); });
            }
            if (afterRelease != null) { var captured = afterRelease; afterRelease = null; Guard(() => Start(captured)); }
        }
        if (selecting && !dragging) Guard(() => Pick(Mouse.GetPosition(view)));
        if (task != null && snapshot?.Available == false) CancelRequest();
        if (task?.IsCompleted == true)
        {
            var finished = task; task = null;
            try
            {
                var result = finished.GetAwaiter().GetResult();
                if (cancellation?.IsCancellationRequested == true) throw new OperationCanceledException();
                if (snapshot == null) throw new TranslationException("target_changed");
                snapshot.Apply(result); Status(text("translation.completed"), 3);
            }
            catch (OperationCanceledException) { Status(text("translation.cancelled"), 3); }
            catch (Exception error) { report(error); Status(message(error)); }
            finally { cancellation?.Dispose(); cancellation = null; snapshot?.Dispose(); snapshot = null; launcher.Tag = text("translation.button"); }
        }
        if (!selecting && task == null && clearStatusAt > 0 && Time.realtimeSinceStartup >= clearStatusAt)
        { help.Visibility = Visibility.Collapsed; clearStatusAt = 0;
        }
    }
    private void Start(ScreenTextSnapshot captured)
    {
        if (!captured.Available) { captured.Dispose(); throw new TranslationException("target_changed"); }
        SetSelecting(false); snapshot = captured; cancellation = new CancellationTokenSource(); launcher.Tag = text("translation.cancel");
        try { Status(text("translation.working")); task = translate(captured.Batch, cancellation.Token); }
        catch { cancellation.Dispose(); cancellation = null; snapshot?.Dispose(); snapshot = null; launcher.Tag = text("translation.button"); throw; }
    }
    private void Status(string value, float seconds = 0)
    {
        helpText.Text = value; help.Visibility = Visibility.Visible;
        clearStatusAt = seconds > 0 ? Time.realtimeSinceStartup + seconds : 0;
    }
    private void CancelRequest()
    {
        cancellation?.Cancel(); snapshot?.Dispose(); snapshot = null;
    }
    private void SetSelecting(bool value)
    {
        selecting = value; leaf = null; candidates.Clear(); afterRelease?.Dispose(); afterRelease = null; clearStatusAt = 0;
        helpText.Text = text("translation.selection_hint");
        shield.Visibility = help.Visibility = value ? Visibility.Visible : Visibility.Collapsed; outline.Visibility = Visibility.Collapsed;
        launcher.Tag = text(value ? "translation.cancel" : "translation.button");
    }
    private void Pick(Point point)
    {
        if (!selecting || dragging) return;
        FrameworkElement? hitElement = null;
        VisualTreeHelper.HitTest(view, node => ReferenceEquals(node, layer) || node is PasswordBox || node is UIElement element && (!element.IsVisible || element.Opacity <= 0)
            ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            hit =>
            {
                for (var node = hit.VisualHit; node != null; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement element && element.ActualWidth > 2 && element.ActualHeight > 2) { hitElement = element; break; }
                return HitTestResultBehavior.Stop;
            }, new PointHitTestParameters(point));
        if (!ReferenceEquals(leaf, hitElement))
        {
            leaf = hitElement; candidates.Clear(); index = 0; Rect? previous = null;
            for (DependencyObject? node = leaf; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not FrameworkElement element || element.ActualWidth <= 2 || element.ActualHeight <= 2) continue;
                var bounds = Bounds(element);
                if (previous == null || bounds != previous.Value) { candidates.Add(element); previous = bounds; }
                if (ReferenceEquals(node, view)) break;
            }
        }
        Highlight();
    }
    // Native layers do not inherit the content root's UI scaling. Draw in this layer's coordinates, not the content's.
    private Rect Bounds(FrameworkElement element) => element.TransformToVisual(layer).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private void Highlight()
    {
        if (candidates.Count == 0) { outline.Visibility = Visibility.Collapsed; return; }
        var bounds = Bounds(candidates[index]); bounds.Intersect(new Rect(0, 0, width, height));
        Canvas.SetLeft(outline, bounds.X); Canvas.SetTop(outline, bounds.Y); outline.Width = Math.Max(0, bounds.Width); outline.Height = Math.Max(0, bounds.Height);
        outline.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Position(Point point)
    {
        var position = new Point(Math.Max(0, Math.Min(width - launcher.Width, point.X)), Math.Max(0, Math.Min(height - launcher.Height, point.Y)));
        Canvas.SetLeft(launcher, position.X); Canvas.SetTop(launcher, position.Y); moved(position);
    }
    private void Resize()
    {
        var viewport = Bounds(view);
        var nextWidth = Math.Max(0, viewport.Right); var nextHeight = Math.Max(0, viewport.Bottom);
        if (nextWidth == width && nextHeight == height) return;
        width = nextWidth; height = nextHeight;
        layer.Measure(new Size(width, height)); layer.Arrange(new Rect(0, 0, width, height));
        if (!float.IsNaN(Canvas.GetLeft(launcher))) Position(new Point(Canvas.GetLeft(launcher), Canvas.GetTop(launcher)));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; afterRelease?.Dispose(); afterRelease = null; toggle = false; CancelRequest(); candidates.Clear(); leaf = null;
        if (task != null) _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        cancellation?.Dispose(); cancellation = null; view.RemoveLayer(layer);
    }
}
