using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Capture is invoked on the UI thread. Sessions passed to the provider contain copied values only.
internal interface IAdvisorSource
{
    IAdvisorQuerySession CreateSession(string language, string? question, int maxRounds = AdvisorQueryLimits.DefaultMaxRounds, AdvisorInformation? information = null, JObject? context = null);
    string Preview(AdvisorText text);
}

internal sealed class CampaignAdvisorSource : IAdvisorSource
{
    private readonly Func<SituationSnapshot> capture;
    internal CampaignAdvisorSource(Func<SituationSnapshot> capture) { this.capture = capture; }
    public IAdvisorQuerySession CreateSession(string language, string? question, int maxRounds = AdvisorQueryLimits.DefaultMaxRounds, AdvisorInformation? information = null, JObject? context = null)
    {
        if (context != null) throw new InvalidOperationException("Campaign consultations do not accept battle contact context.");
        return new AdvisorQuerySession(capture(), language, question, maxRounds, information);
    }
    public string Preview(AdvisorText text) => SituationFormatter.Format(capture(), text);
}

internal interface IAdvisorOperationSource
{
    IAdvisorOperationSession? CreateOperations(string owner, AdvisorPrompts prompts, AdvisorOperationReferences references);
    AdvisorOperations? Operations { get; }
}

internal sealed class BattleAdvisorSource : IAdvisorSource, IAdvisorOperationSource
{
    private readonly Func<AdvisorInformation?, BattleSnapshot> capture;
    private readonly Func<AdvisorOperationGateway?>? operations;
    private readonly AdvisorTextSummaryCache summaries;
    private readonly Func<bool> summarizeText;
    private readonly Action<string> warn;
    internal BattleAdvisorSource(Func<AdvisorInformation?, BattleSnapshot> capture, Func<AdvisorOperationGateway?>? operations = null,
        AdvisorTextSummaryCache? summaries = null, Func<bool>? summarizeText = null, Action<string>? warn = null)
    { this.capture = capture; this.operations = operations; this.summaries = summaries ?? new AdvisorTextSummaryCache();
        this.summarizeText = summarizeText ?? (() => false); this.warn = warn ?? (_ => { }); }
    public IAdvisorOperationSession? CreateOperations(string owner, AdvisorPrompts prompts, AdvisorOperationReferences references) =>
        operations?.Invoke()?.CreateSession(owner, prompts, references);
    public AdvisorOperations? Operations => operations?.Invoke()?.Operations;
    public IAdvisorQuerySession CreateSession(string language, string? question, int maxRounds = AdvisorQueryLimits.DefaultMaxRounds, AdvisorInformation? information = null, JObject? context = null) => new BattleQuerySession(capture(information), language, question, maxRounds, information, context: context, summaryCache: summaries, summarizeText: summarizeText(), summaryWarning: warn);
    public string Preview(AdvisorText text) => text["battle.diagnostic"] + "\n\n" + capture(null).Copy().ToString(Newtonsoft.Json.Formatting.Indented);
}
