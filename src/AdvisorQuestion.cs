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
        "next_steps");
    internal static readonly AdvisorShortcut MoreDetail = new("preset.more_detail",
        "more_detail", true);
    internal static readonly AdvisorShortcut KeyRisks = new("preset.key_risks",
        "key_risks");

    internal string LabelKey { get; }
    private string PromptKey { get; }
    private bool RequiresHistory { get; }
    private AdvisorShortcut(string key, string prompt, bool history = false)
    { LabelKey = key; PromptKey = prompt; RequiresHistory = history; }

    internal bool IsAvailable(bool busy, int exchangeCount, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges) =>
        !busy && (!RequiresHistory || (exchangeCount > 0 && historyExchanges > 0));
    internal AdvisorQuestion Prepare(string caption, AdvisorPrompts? prompts = null) =>
        new((prompts ?? AdvisorPrompts.Current).Text("requests.json", PromptKey), caption, true);
}
