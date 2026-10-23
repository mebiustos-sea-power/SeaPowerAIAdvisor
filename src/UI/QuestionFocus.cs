namespace SeaPowerAIAdvisor;

// Leave the requesting event and retain at most one automatic focus restoration.
internal sealed class QuestionFocus
{
    private int pendingFrame = -1;
    internal bool Pending => pendingFrame >= 0;
    internal void Request(int frame) => pendingFrame = frame;
    internal void Cancel() => pendingFrame = -1;

    internal bool TakeFocus(int frame, bool eligible, bool inputReady)
    {
        if (!Pending) return false;
        if (!eligible) { Cancel(); return false; }
        if (frame <= pendingFrame || !inputReady) return false;
        Cancel();
        return true;
    }
}
