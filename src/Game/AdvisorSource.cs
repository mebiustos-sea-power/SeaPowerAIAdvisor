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
    IAdvisorOperationSession? CreateOperations(string owner, AdvisorPrompts prompts);
    AdvisorOperations? Operations { get; }
}

internal sealed class BattleAdvisorSource : IAdvisorSource, IAdvisorOperationSource
{
    private readonly Func<BattleSnapshot> capture;
    private readonly Func<AdvisorOperationGateway?>? operations;
    internal BattleAdvisorSource(Func<BattleSnapshot> capture, Func<AdvisorOperationGateway?>? operations = null)
    { this.capture = capture; this.operations = operations; }
    public IAdvisorOperationSession? CreateOperations(string owner, AdvisorPrompts prompts) => operations?.Invoke()?.CreateSession(owner, prompts);
    public AdvisorOperations? Operations => operations?.Invoke()?.Operations;
    public IAdvisorQuerySession CreateSession(string language, string? question, int maxRounds = AdvisorQueryLimits.DefaultMaxRounds, AdvisorInformation? information = null, JObject? context = null) => new BattleQuerySession(capture(), language, question, maxRounds, information, context: context);
    public string Preview(AdvisorText text) => text["battle.diagnostic"] + "\n\n" + capture().Copy().ToString(Newtonsoft.Json.Formatting.Indented);
}
