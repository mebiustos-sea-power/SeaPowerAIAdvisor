using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor.Translation;

// Capture displayed text and request-scoped destinations, not view-model fields or editable input values.
internal static class ScreenTextReader
{
    internal static ScreenTextSnapshot Capture(FrameworkElement root, string language, DependencyObject? exclude = null)
    {
        var targets = new List<ScreenTextTarget>(); var pending = new Stack<DependencyObject>(); var seen = new HashSet<DependencyObject>(); long characters = 0;
        void Add(TextBlock owner, FrameworkElement target, DependencyProperty property)
        {
            if (BindingOperations.GetBinding(target, property) is { Mode: BindingMode.TwoWay or BindingMode.OneWayToSource }) return;
            if (target.GetValue(property) is not string text || string.IsNullOrWhiteSpace(text)) return;
            targets.Add(new ScreenTextTarget(owner, target, property, text)); characters += text.Length;
            if (targets.Count > TranslationBatch.MaxBlocks || characters > TranslationBatch.MaxCharacters) throw new TranslationException("too_large");
        }
        void Inlines(TextBlock owner, InlineCollection inlines)
        {
            foreach (var inline in inlines)
                switch (inline)
                {
                    case Run run: Add(owner, run, Run.TextProperty); break;
                    case Span span: Inlines(owner, span.Inlines); break;
                    case InlineUIContainer container when container.Child != null: pending.Push(container.Child); break;
                }
        }
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop(); if (!seen.Add(node)) continue;
            if (seen.Count > 20_000) throw new TranslationException("too_large");
            if (ReferenceEquals(node, exclude) || node is PasswordBox or TextBox || node is UIElement ui && (!ui.IsVisible || ui.Opacity <= 0)) continue;
            if (node is TextBlock block)
            {
                // Text bindings stay on TextBlock; explicit runs retain their formatting, links and line breaks.
                if (block.Inlines.Count == 0 || BindingOperations.IsDataBound(block, TextBlock.TextProperty)) Add(block, block, TextBlock.TextProperty);
                else Inlines(block, block.Inlines);
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return new ScreenTextSnapshot(root, targets, language);
    }
}

internal sealed class ScreenTextTarget
{
    private readonly TextBlock owner;
    private readonly FrameworkElement target;
    private readonly DependencyProperty property;
    internal string Original { get; }
    internal TextBlock Owner => owner;
    internal string? Text => target.GetValue(property) as string;
    internal bool SameProperty(ScreenTextTarget other) => ReferenceEquals(owner, other.owner) && ReferenceEquals(target, other.target) && ReferenceEquals(property, other.property);
    internal ScreenTextTarget(TextBlock owner, FrameworkElement target, DependencyProperty property, string original)
    { this.owner = owner; this.target = target; this.property = property; Original = original; }
    internal string? ChangedReason(FrameworkElement scope) => InScopeReason(scope) ?? (Text == Original ? null : "text_changed");
    internal string? InScopeReason(FrameworkElement scope)
    {
        if (owner.View == null) return "owner_detached";
        if (!owner.IsVisible) return "owner_hidden";
        if (!ReferenceEquals(owner, scope) && !owner.IsDescendantOf(scope)) return "outside_selection";
        // Inlines belong to the text content, not the visual tree. Validate membership through their owning TextBlock.
        if (!ReferenceEquals(owner, target) && !Contains(owner.Inlines, target)) return "inline_detached";
        return null;
    }
    private static bool Contains(InlineCollection inlines, FrameworkElement target)
    {
        foreach (var inline in inlines)
            if (ReferenceEquals(inline, target) || inline is Span span && Contains(span.Inlines, target)) return true;
        return false;
    }
    internal void Set(string text)
    {
        // Preserve bindings/resources and keep this operation confined to the rendered value.
        target.SetCurrentValue(property, text);
    }
    internal void RollBack(string translated)
    { if (Equals(target.GetValue(property), translated)) target.SetCurrentValue(property, Original); }
}

// Request ownership ends on unload or completion; successful targets are also retained by the screen restoration registry.
internal sealed class ScreenTextSnapshot : IDisposable
{
    private FrameworkElement? scope;
    private List<ScreenTextTarget>? targets;
    internal TranslationBatch Batch { get; }
    internal bool Available => scope?.View != null;
    internal ScreenTextSnapshot(FrameworkElement scope, List<ScreenTextTarget> targets, string language)
    {
        Batch = new TranslationBatch(targets.Select(t => t.Original), language);
        this.scope = scope; this.targets = targets; scope.Unloaded += Unloaded;
    }
    private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
    internal void Apply(TranslationResult result, ScreenTextRestorations restorations)
    {
        var root = scope; var current = targets?.ToArray();
        if (root == null || current == null || root.View == null)
            throw new TranslationException("target_changed", "selection_detached");
        if (result.Texts.Count != current.Length)
            throw new TranslationException("target_changed", "text_count_changed");
        for (var i = 0; i < current.Length; i++)
            if (current[i].ChangedReason(root) is { } reason)
                throw new TranslationException("target_changed", "t" + (i + 1) + ": " + reason);
        var applied = 0;
        try { for (; applied < current.Length; applied++) current[applied].Set(result.Texts[applied]); }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            for (var i = Math.Min(applied, current.Length - 1); i >= 0; i--)
                try { current[i].RollBack(result.Texts[i]); } catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException("Translation application and rollback failed.", errors);
            throw;
        }
        restorations.Remember(current, result.Texts);
    }
    public void Dispose()
    {
        var previous = scope; scope = null; targets?.Clear(); targets = null;
        if (previous != null) previous.Unloaded -= Unloaded;
    }
}
