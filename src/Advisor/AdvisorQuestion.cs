using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Draft text, visible captions and actual questions remain distinct throughout the request.
internal sealed class AdvisorQuestion
{
    internal string Text { get; }
    internal string DisplayText { get; }
    internal IReadOnlyList<(int Id, string Name)> Units { get; }
    internal IReadOnlyList<AdvisorLocation> Locations { get; }
    internal string DisplayBody => Units.Count == 0 && Locations.Count == 0 ? DisplayText : draftText;
    internal bool IsShortcut { get; }
    internal bool AllowOperations { get; }
    internal AdvisorSituationDelivery? SituationDelivery { get; }
    private readonly JObject? context;
    private readonly string draftText;
    internal bool HasContext => context != null;
    internal JObject? Context => context == null ? null : (JObject)context.DeepClone();
    internal AdvisorQuestion(string text, bool allowOperations = true) : this(text, text, false, allowOperations: allowOperations) { }
    internal AdvisorQuestion(string text, string caption, bool shortcut, JObject? context = null, string? draftText = null,
        IEnumerable<(int Id, string Name)>? units = null, bool allowOperations = true, AdvisorSituationDelivery? situationDelivery = null, IEnumerable<AdvisorLocation>? locations = null)
    {
        Text = text; DisplayText = caption; IsShortcut = shortcut; AllowOperations = allowOperations;
        if (situationDelivery.HasValue) _ = situationDelivery.Value.Code();
        SituationDelivery = situationDelivery;
        this.context = context == null ? null : (JObject)context.DeepClone(); this.draftText = draftText ?? text;
        Units = Array.AsReadOnly((units ?? Array.Empty<(int, string)>()).ToArray());
        Locations = Array.AsReadOnly((locations ?? Array.Empty<AdvisorLocation>()).ToArray());
    }

    internal AdvisorQuestion WithOperations(bool allowed) => allowed == AllowOperations ? this :
        new(Text, DisplayText, IsShortcut, context, draftText, Units, allowed, SituationDelivery, Locations);

    internal AdvisorQuestion WithSituationDelivery(AdvisorSituationDelivery? mode) => mode == SituationDelivery ? this :
        new(Text, DisplayText, IsShortcut, context, draftText, Units, AllowOperations, mode, Locations);

    internal bool ConsumesDraft(string draft) => !IsShortcut && draft == draftText;
}

internal sealed class AdvisorShortcut
{
    internal static readonly AdvisorShortcut NextSteps = new("preset.next_steps",
        "next_steps", situationDelivery: AdvisorSituationDelivery.Upfront);
    internal static readonly AdvisorShortcut MoreDetail = new("preset.more_detail",
        "more_detail", true, situationDelivery: AdvisorSituationDelivery.Upfront);
    internal static readonly AdvisorShortcut KeyRisks = new("preset.key_risks",
        "key_risks", situationDelivery: AdvisorSituationDelivery.Upfront);
    internal static readonly AdvisorShortcut RadarOperations = new("preset.radar_operations",
        "radar_operations", situationDelivery: AdvisorSituationDelivery.Upfront);
    internal static readonly AdvisorShortcut OwnUnits = new("preset.own_units",
        "own_units", situationDelivery: AdvisorSituationDelivery.Upfront);
    internal static readonly AdvisorShortcut Contacts = new("preset.contacts",
        "contacts", situationDelivery: AdvisorSituationDelivery.Upfront);

    internal string LabelKey { get; }
    internal string Id { get; }
    private bool RequiresHistory { get; }
    internal bool AllowOperations { get; }
    internal AdvisorSituationDelivery? SituationDelivery { get; }
    internal AdvisorShortcut(string key, string prompt, bool history = false, bool allowOperations = false, AdvisorSituationDelivery? situationDelivery = null)
    {
        if (situationDelivery.HasValue) _ = situationDelivery.Value.Code();
        LabelKey = key; Id = prompt; RequiresHistory = history; AllowOperations = allowOperations; SituationDelivery = situationDelivery;
    }

    internal static readonly IReadOnlyList<AdvisorShortcut> All = Array.AsReadOnly(new[] { NextSteps, KeyRisks, RadarOperations, OwnUnits, Contacts, MoreDetail });
    internal static AdvisorShortcut? Find(string id) => All.FirstOrDefault(shortcut => shortcut.Id == id);

    internal bool IsAvailable(bool busy, int exchangeCount, int historyExchanges = AdvisorOptions.DefaultHistoryExchanges) =>
        !busy && (!RequiresHistory || (exchangeCount > 0 && historyExchanges > 0));
    internal AdvisorQuestion Prepare(string caption, AdvisorPrompts? prompts = null) =>
        new((prompts ?? AdvisorPrompts.Current).Text("requests.json", Id), caption, true,
            allowOperations: AllowOperations, situationDelivery: SituationDelivery);
}
