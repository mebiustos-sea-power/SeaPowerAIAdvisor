using System;

namespace SeaPowerAIAdvisor.Shared;

// A load stages detached data; feature restoration waits until native units are ready.
internal sealed class SaveDataSession
{
    private readonly Action<object> reset;
    private readonly Func<Func<byte[]?>?> prepareCapture;
    private readonly Action<byte[]?> restore;
    private object? owner;
    private byte[]? pending;
    private bool staged, ready;
    internal SaveDataSession(Action<object> reset, Func<Func<byte[]?>?> prepareCapture, Action<byte[]?> restore)
    { this.reset = reset; this.prepareCapture = prepareCapture; this.restore = restore; }

    internal void Begin(object mission)
    {
        owner = null; pending = null; staged = false; ready = false;
        reset(mission); owner = mission;
    }
    internal void Stage(object mission, string? payload)
    {
        if (!ReferenceEquals(owner, mission)) throw new InvalidOperationException("The save data belongs to another load.");
        pending = SaveDataSection.Decode(payload); staged = true;
    }
    internal void Complete(object mission, bool unitsReady)
    {
        if (!ReferenceEquals(owner, mission) || !unitsReady || !staged) return;
        staged = false;
        restore(pending); pending = null; ready = true;
    }
    internal string? Capture()
    {
        // The owner selects work without reading save state. Selected work waits for restoration.
        var capture = prepareCapture();
        if (capture == null) return null;
        if (!ready) throw new InvalidOperationException("MOD save data has not been restored for the current mission.");
        return SaveDataSection.Encode(capture());
    }
}
