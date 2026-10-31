using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Providers consume detached knowledge through this contract, independently of the game mode.
internal interface IAdvisorQuerySession
{
    string Id { get; }
    int RoundsUsed { get; }
    int MaxRounds { get; }
    AdvisorPrompts Prompts { get; }
    JObject InitialInput();
    JObject Execute(JObject request);
}

internal static class AdvisorQueryLimits
{
    internal const int DefaultMaxRounds = 15;
    internal const int MaximumRounds = 20;
    internal const int MaxQueriesPerRound = 8;
    internal const int MaxIdsPerQuery = 10;
    internal const int DefaultPageSize = 10;
    internal const int MaxPageSize = 20;
    internal const int MaxRequestBytes = 16_000;
    internal const int MaxResponseBytes = 48_000;
}

internal sealed class AdvisorQueryException : Exception
{
    internal string Code { get; }
    internal AdvisorQueryException(string code, string message) : base(message) { Code = code; }
}
