using System;

namespace SeaPowerAIAdvisor.Shared;

// Coordinates tab navigation only. Each form owns its dirty check, discard and persistence.
internal sealed class SettingsTabNavigation
{
    private readonly Action<int> select;
    private readonly Action entered;
    private int? requested;
    internal int Current { get; private set; }
    internal bool HasPending => requested.HasValue;
    internal SettingsTabNavigation(Action<int> select, Action entered)
    { this.select = select; this.entered = entered; }
    internal void Reset(int index = 0)
    {
        requested = null; Current = index; select(index); entered();
    }
    internal void Request(int index) => requested = index >= 0 && index != Current ? index : null;
    // Content can update after SelectionChanged returns. Restore selection from the UI tick, never inside that event.
    internal void Pump(bool dirty, Func<bool> leave, Action<Action> confirm)
    {
        if (!requested.HasValue) return;
        var index = requested.Value; requested = null;
        select(Current);
        void Go() { if (leave()) Reset(index); }
        if (dirty) confirm(Go); else Go();
    }
}
