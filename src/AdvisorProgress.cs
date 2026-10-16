using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

internal enum AdvisorStage { Waiting, Retrieving, FinalAnswer }

// Immutable, bounded progress metadata; never contains model text, reasoning or entity identifiers.
internal sealed class AdvisorProgress
{
    internal AdvisorStage Stage { get; }
    internal int ApiRequest { get; }
    internal int Rounds { get; }
    internal int MaxRounds { get; }
    internal IReadOnlyList<string> Queries { get; }
    internal AdvisorProgress(AdvisorStage stage, int apiRequest, int rounds, int maxRounds, IEnumerable<string>? queries = null)
    {
        Stage = stage; ApiRequest = apiRequest; Rounds = rounds; MaxRounds = maxRounds;
        Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).Take(AdvisorQueryLimits.MaxQueriesPerRound).ToArray());
    }
    internal static string Format(AdvisorProgress? progress, AdvisorText text)
    {
        if (progress == null) return text["transcript.waiting"];
        var key = progress.Stage switch { AdvisorStage.Retrieving => "progress.retrieving", AdvisorStage.FinalAnswer => "progress.final", _ => "progress.waiting" };
        var value = text.Format(key, progress.ApiRequest, progress.Rounds, progress.MaxRounds);
        var topics = progress.Queries.Select(Topic).Distinct().Select(k => text["progress.topic." + k]).ToArray();
        return topics.Length == 0 ? value : value + "\n" + text.Format("progress.topics", string.Join(text["progress.separator"], topics));
    }
    private static string Topic(string query) => query switch
    {
        "catalog" => "catalog", "list_units" or "get_units" => "units", "get_unit_capabilities" => "capabilities",
        "list_contacts" => "contacts", "get_contact_threats" => "threats", "get_briefing" => "briefing",
        "list_reports" or "get_reports" => "reports", "list_forces" or "get_forces" => "forces",
        "list_ships" or "get_ships" => "ships", "list_air_units" or "get_air_units" or "list_air_flights" => "aviation",
        "list_bases" => "bases", "get_base_observations" => "observations", "list_orders" or "get_orders" => "orders", _ => "other"
    };
}
