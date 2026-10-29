using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor.Translation;

// One record per live text property, not a history. Unloaded owners release their records and native wrappers.
internal sealed class ScreenTextRestorations : IDisposable
{
    private readonly List<(ScreenTextTarget Target, TranslationTextChange Text)> entries = new();
    private readonly HashSet<TextBlock> owners = new();
    internal void Remember(IReadOnlyList<ScreenTextTarget> targets, IReadOnlyList<string> translated)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var index = entries.FindIndex(e => e.Target.SameProperty(target));
            if (index >= 0)
            {
                var change = entries[index].Text; change.Update(target.Original, translated[i]);
                entries[index] = (target, change);
            }
            else if (target.Original != translated[i])
            {
                if (owners.Add(target.Owner)) target.Owner.Unloaded += Unloaded;
                entries.Add((target, new TranslationTextChange(target.Original, translated[i])));
            }
        }
        Prune();
    }
    internal bool CanRestore(FrameworkElement root)
    {
        Prune();
        return entries.Any(e => e.Target.InScopeReason(root) == null);
    }
    internal bool Restore(FrameworkElement root)
    {
        Prune();
        var errors = new List<Exception>(); var restored = false;
        foreach (var entry in entries.ToArray())
        {
            if (entry.Target.InScopeReason(root) != null) continue;
            try
            {
                if (entry.Text.OriginalFor(entry.Target.Text) is { } original) { entry.Target.Set(original); restored = true; }
                entries.Remove(entry);
            }
            catch (Exception error) { errors.Add(error); }
        }
        ReleaseOwners();
        if (errors.Count > 0) throw new AggregateException("Restoring original screen text failed.", errors);
        return restored;
    }
    private void Prune()
    {
        entries.RemoveAll(e => e.Target.InScopeReason(e.Target.Owner) != null || e.Text.OriginalFor(e.Target.Text) == null);
        ReleaseOwners();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        var owner = (TextBlock)sender;
        entries.RemoveAll(e => ReferenceEquals(e.Target.Owner, owner));
        if (owners.Remove(owner)) owner.Unloaded -= Unloaded;
    }
    private void ReleaseOwners()
    {
        var retained = new HashSet<TextBlock>(entries.Select(e => e.Target.Owner));
        foreach (var owner in owners.Where(owner => !retained.Contains(owner)).ToArray())
        { owner.Unloaded -= Unloaded; owners.Remove(owner); }
    }
    public void Dispose()
    {
        foreach (var owner in owners) owner.Unloaded -= Unloaded;
        owners.Clear(); entries.Clear();
    }
}
