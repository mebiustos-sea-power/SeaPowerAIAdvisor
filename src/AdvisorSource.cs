using System;

namespace SeaPowerAIAdvisor;

// Capture is invoked on the UI thread. Sessions passed to the provider contain copied values only.
internal interface IAdvisorSource
{
    IAdvisorQuerySession CreateSession(string language, string? question);
    string Preview(AdvisorText text);
}

internal sealed class CampaignAdvisorSource : IAdvisorSource
{
    private readonly Func<SituationSnapshot> capture;
    internal CampaignAdvisorSource(Func<SituationSnapshot> capture) { this.capture = capture; }
    public IAdvisorQuerySession CreateSession(string language, string? question) => new AdvisorQuerySession(capture(), language, question);
    public string Preview(AdvisorText text) => SituationFormatter.Format(capture(), text);
}

internal sealed class BattleAdvisorSource : IAdvisorSource
{
    private readonly Func<BattleSnapshot> capture;
    internal BattleAdvisorSource(Func<BattleSnapshot> capture) { this.capture = capture; }
    public IAdvisorQuerySession CreateSession(string language, string? question) => new BattleQuerySession(capture(), language, question);
    public string Preview(AdvisorText text) => text["battle.diagnostic"] + "\n\n" + capture().Copy().ToString(Newtonsoft.Json.Formatting.Indented);
}
