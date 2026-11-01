using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

// Automatic-engagement policy only; changing it does not cancel attacks or reservations.
public interface IAdvisorRulesOfEngagementSupport
{
    IReadOnlyList<RulesOfEngagementUnitInfo> GetRulesOfEngagementUnits();
    void SetRulesOfEngagement(string target, RulesOfEngagement mode);
}

public enum RulesOfEngagement { Free, Tight, Hold }

public sealed class RulesOfEngagementUnitInfo
{
    public GameUnitInfo Unit { get; }
    public RulesOfEngagement Mode { get; }
    public RulesOfEngagementUnitInfo(GameUnitInfo unit, RulesOfEngagement mode)
    { Unit = unit; Mode = mode; }
}
