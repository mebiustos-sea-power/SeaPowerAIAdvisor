using System;
using System.Collections.Generic;
using System.Threading;

namespace SeaPowerAIAdvisor;

// Process-local state only. Replacing a reference leaves consultations already holding it unchanged.
internal sealed class AdvisorPromptOverrides
{
    private static readonly Lazy<AdvisorPromptOverrides> session = new(() => new(AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory)));
    internal static AdvisorPromptOverrides Session => session.Value;
    internal static AdvisorPrompts? LoadedCurrent => session.IsValueCreated ? session.Value.Current : null;
    private readonly AdvisorPrompts original;
    private AdvisorPrompts current;
    internal AdvisorPrompts Current => Volatile.Read(ref current);
    internal bool HasOverrides => !ReferenceEquals(Current, original);
    internal AdvisorPromptOverrides(AdvisorPrompts original) { this.original = original; current = original; }
    internal void Apply(IReadOnlyDictionary<string, string> values)
    { var next = original.WithText(values); Volatile.Write(ref current, next); }
    internal void Reset() => Volatile.Write(ref current, original);
}
