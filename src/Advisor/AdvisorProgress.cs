using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

internal enum AdvisorStage { Waiting, Retrieving, FinalAnswer, SwitchingKey }

// Immutable, bounded progress metadata; never contains model text, reasoning or entity identifiers.
internal sealed class AdvisorProgress
{
    internal AdvisorStage Stage { get; }
    internal int ApiRequest { get; }
    internal bool UsingFallback { get; }
    internal string Model { get; }
    internal string ConnectionName { get; }
    internal int Rounds { get; }
    internal int MaxRounds { get; }
    internal IReadOnlyList<string> Queries { get; }
    internal AdvisorProgress(AdvisorStage stage, int apiRequest, int rounds, int maxRounds, IEnumerable<string>? queries = null, bool usingFallback = false, string model = "", string connectionName = "")
    {
        Stage = stage; ApiRequest = apiRequest; UsingFallback = usingFallback; Model = model; ConnectionName = connectionName; Rounds = rounds; MaxRounds = maxRounds;
        Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).Take(AdvisorQueryLimits.MaxQueriesPerRound).ToArray());
    }
    internal static string Format(AdvisorProgress? progress, AdvisorText text)
    {
        if (progress == null) return text["transcript.waiting"];
        var key = progress.Stage switch { AdvisorStage.Retrieving => "progress.retrieving", AdvisorStage.FinalAnswer => "progress.final", AdvisorStage.SwitchingKey => "progress.switching_key", _ => "progress.waiting" };
        var value = text.Format(key, progress.ApiRequest, progress.Rounds, progress.MaxRounds);
        if (progress.UsingFallback && progress.Stage != AdvisorStage.SwitchingKey) value += "\n" + text["progress.fallback_active"];
        if (progress.ConnectionName.Length > 0) value += "\n" + FormatConnection(progress.ConnectionName, progress.Model, text);
        var topics = progress.Queries.Select(Topic).Distinct().Select(k => text["progress.topic." + k]).ToArray();
        if (topics.Length == 0 || progress.Stage == AdvisorStage.SwitchingKey) return value;
        var topicKey = progress.Stage == AdvisorStage.Retrieving ? "progress.topics_checking" : "progress.topics_considering";
        return value + "\n" + text.Format(topicKey, string.Join(text["progress.separator"], topics));
    }
    internal static string FormatConnection(string connectionName, string model, AdvisorText text) =>
        connectionName.Length == 0 ? model : model.Length == 0 ? connectionName : text.Format("progress.connection", connectionName, model);

    private static string Topic(string query) => query switch
    {
        "catalog" => "catalog", "list_units" or "get_units" => "units", "get_unit_capabilities" => "capabilities",
        "get_movement_plans" => "movement",
        AdvisorOperationGateway.ToolName => "operations",
        "list_contacts" or "list_identification_candidates" => "contacts", "get_contact_threats" => "threats", "get_briefing" => "briefing",
        "list_reports" or "get_reports" => "reports", "list_forces" or "get_forces" => "forces",
        "list_ships" or "get_ships" => "ships", "list_air_units" or "get_air_units" or "list_air_flights" or "list_air_tasks" => "aviation",
        "list_bases" or "get_bases" => "bases", "list_ground_units" or "get_ground_units" => "ground", "get_base_observations" => "observations", "list_orders" or "get_orders" => "orders", _ => "other"
    };
}
