using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Draft text, visible captions and actual questions remain distinct throughout the request.
internal sealed class AdvisorQuestion
{
    internal string Text { get; }
    internal string DisplayText { get; }
    internal bool IsShortcut { get; }
    private readonly JObject? context;
    private readonly string draftText;
    internal bool HasContext => context != null;
    internal JObject? Context => context == null ? null : (JObject)context.DeepClone();
    internal AdvisorQuestion(string text) : this(text, text, false) { }
    internal AdvisorQuestion(string text, string caption, bool shortcut, JObject? context = null, string? draftText = null)
    { Text = text; DisplayText = caption; IsShortcut = shortcut; this.context = context == null ? null : (JObject)context.DeepClone(); this.draftText = draftText ?? text; }

    internal bool ConsumesDraft(string draft) => !IsShortcut && draft == draftText;
}

internal sealed class AdvisorShortcut
{
    internal static readonly AdvisorShortcut NextSteps = new("preset.next_steps",
        "next_steps");
    internal static readonly AdvisorShortcut MoreDetail = new("preset.more_detail",
        "more_detail", true);
    internal static readonly AdvisorShortcut KeyRisks = new("preset.key_risks",
        "key_risks");
    internal static readonly AdvisorShortcut RadarOperations = new("preset.radar_operations",
        "radar_operations");
    internal static readonly AdvisorShortcut OwnUnits = new("preset.own_units",
        "own_units");
    internal static readonly AdvisorShortcut Contacts = new("preset.contacts",
        "contacts");

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
