namespace SeaPowerAIAdvisor;

// The visible caption and actual question are distinct throughout the request.
internal sealed class AdvisorQuestion
{
    internal string Text { get; }
    internal string DisplayText { get; }
    internal bool IsShortcut { get; }
    internal AdvisorQuestion(string text) : this(text, text, false) { }
    internal AdvisorQuestion(string text, string caption, bool shortcut)
    { Text = text; DisplayText = caption; IsShortcut = shortcut; }

    internal string DraftAfterSend(string draft) =>
        !IsShortcut && draft == Text ? string.Empty : draft;
}

internal sealed class AdvisorShortcut
{
    internal static readonly AdvisorShortcut NextSteps = new("preset.next_steps",
        "What should I do next? Give concrete actions in priority order, tied to the victory objectives.");
    internal static readonly AdvisorShortcut MoreDetail = new("preset.more_detail",
        "Make your previous advice more specific: identify relevant units, locations and actions using current evidence. Recheck unsupported earlier claims.", true);
    internal static readonly AdvisorShortcut KeyRisks = new("preset.key_risks",
        "What are the main current risks to achieving victory, and what concrete actions could address them? Distinguish observed threats from uncertainty.");

    internal string LabelKey { get; }
    private string Prompt { get; }
    private bool RequiresHistory { get; }
    private AdvisorShortcut(string key, string prompt, bool history = false)
    { LabelKey = key; Prompt = prompt; RequiresHistory = history; }

    internal bool IsAvailable(bool busy, int exchangeCount) => !busy && (!RequiresHistory || exchangeCount > 0);
    internal AdvisorQuestion Prepare(string caption) => new(Prompt, caption, true);
}
