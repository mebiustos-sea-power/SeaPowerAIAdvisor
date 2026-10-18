namespace SeaPowerAIAdvisor;

internal enum QuestionKeyAction { None, InsertNewline }

// Owns a physical Enter press until release. IME and busy presses cannot become
// submissions later when composition or the request finishes.
internal sealed class QuestionInput
{
    private bool held;
    private bool composing;
    private int pendingFrame = -1;

    internal QuestionKeyAction Enter(int frame, bool shift, bool repeat, bool busy, bool ime)
    {
        var ignore = held || repeat || busy || ime || composing;
        held = true;
        if (ime) composing = true;
        if (ignore) return QuestionKeyAction.None;
        if (shift) return QuestionKeyAction.InsertNewline;
        pendingFrame = frame;
        return QuestionKeyAction.None;
    }

    internal void TextEntered(string text)
    {
        // Finalized IME text arriving after KeyDown invalidates its queued send.
        if (!string.IsNullOrEmpty(text)) pendingFrame = -1;
    }

    internal bool TakeSubmission(int frame, bool focused, bool ime, bool busy, bool enterDown)
    {
        if (!focused) { Reset(enterDown); return false; }
        if (ime) { composing = true; pendingFrame = -1; }
        if (busy)
        {
            pendingFrame = -1;
            if (enterDown) held = true;
        }
        if (!enterDown)
        {
            held = false;
            if (!ime) composing = false;
        }
        // Leave the native keyboard callback before starting network work, and
        // wait for release so a held confirmation key can never repeat-send.
        if (pendingFrame < 0 || frame <= pendingFrame || enterDown || busy || ime) return false;
        pendingFrame = -1;
        return true;
    }

    internal void Reset(bool enterDown)
    {
        held = enterDown;
        composing = false;
        pendingFrame = -1;
    }
}
