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

internal sealed class BattleAdvisorSource : IAdvisorSource
{
    private readonly Func<BattleSnapshot> capture;
    internal BattleAdvisorSource(Func<BattleSnapshot> capture) { this.capture = capture; }
    public IAdvisorQuerySession CreateSession(string language, string? question, int maxRounds = AdvisorQueryLimits.DefaultMaxRounds, AdvisorInformation? information = null, JObject? context = null) => new BattleQuerySession(capture(), language, question, maxRounds, information, context: context);
    public string Preview(AdvisorText text) => text["battle.diagnostic"] + "\n\n" + capture().Copy().ToString(Newtonsoft.Json.Formatting.Indented);
}
