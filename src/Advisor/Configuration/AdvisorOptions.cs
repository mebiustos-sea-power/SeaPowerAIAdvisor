using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Immutable, provider-independent behavior and presentation settings. Publish only after saving succeeds.
internal sealed class AdvisorOptions
{
    internal const int MaxCommonPromptLength = 2000;
    internal const int MaxUserNameLength = 80;
    internal const int DefaultHistoryExchanges = 3;
    internal const int MaximumHistoryExchanges = AdvisorConversation.MaxExchanges;
    internal string CommonPrompt { get; }
    internal string? UserName { get; }
    internal int MaxQueryRounds { get; }
    internal int HistoryExchanges { get; }
    internal bool CampaignEnabled { get; }
    internal bool CrewEnabled { get; }
    internal bool SummarizeMissionText { get; }
    internal bool DeveloperApiEnabled { get; }
    internal IReadOnlyList<string> DisabledCommands { get; }
    internal bool IsCommandEnabled(string id) => !DisabledCommands.Contains(id, StringComparer.Ordinal);
    internal AdvisorLocalConnections LocalConnections { get; }
    internal AdvisorInformation Information { get; }
    internal AdvisorTextSize TextSize { get; }
    internal bool LinkOwnUnits { get; }
    internal bool LinkContacts { get; }
    internal bool UnitMentionCompletion { get; }
    internal AdvisorTemplateOptions Templates { get; }
    internal AdvisorOptions(int maxQueryRounds = AdvisorQueryLimits.DefaultMaxRounds, bool campaignEnabled = false, AdvisorInformation? information = null, string commonPrompt = "", AdvisorTextSize textSize = AdvisorTypography.DefaultSize, int historyExchanges = DefaultHistoryExchanges, string? userName = null, bool developerApiEnabled = false, AdvisorLocalConnections? localConnections = null, bool linkOwnUnits = true, bool linkContacts = true, IEnumerable<string>? disabledCommands = null, AdvisorTemplateOptions? templates = null, bool crewEnabled = false, bool unitMentionCompletion = false, bool summarizeMissionText = false)
    {
        if (maxQueryRounds < 1 || maxQueryRounds > AdvisorQueryLimits.MaximumRounds)
            throw new AdvisorOptionsException("query_rounds_invalid");
        var disabled = (disabledCommands ?? Array.Empty<string>()).ToArray();
        if (disabled.Any(id => string.IsNullOrEmpty(id) || id.Length > 80 ||
                id.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))) ||
            disabled.Distinct(StringComparer.Ordinal).Count() != disabled.Length)
            throw new AdvisorOptionsException("options_invalid");
        DisabledCommands = Array.AsReadOnly(disabled.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        CrewEnabled = crewEnabled; DeveloperApiEnabled = developerApiEnabled; LocalConnections = localConnections ?? AdvisorLocalConnections.Empty;
        MaxQueryRounds = maxQueryRounds; CampaignEnabled = campaignEnabled; Information = information ?? AdvisorInformation.Default;
        if (!Enum.IsDefined(typeof(AdvisorTextSize), textSize)) throw new AdvisorOptionsException("options_invalid");
        UnitMentionCompletion = unitMentionCompletion; SummarizeMissionText = summarizeMissionText;
        TextSize = textSize; LinkOwnUnits = linkOwnUnits; LinkContacts = linkContacts; Templates = templates ?? new AdvisorTemplateOptions();
        CommonPrompt = ValidateCommonPrompt(commonPrompt);
        UserName = userName == null ? null : ValidateUserName(userName);
        HistoryExchanges = ValidateHistoryExchanges(historyExchanges);
    }
    internal static int ValidateHistoryExchanges(int value)
    {
        if (value < 0 || value > MaximumHistoryExchanges) throw new AdvisorOptionsException("history_exchanges_invalid");
        return value;
    }
    internal static string ValidateUserName(string value)
    {
        value = value.Trim();
        if (value.Length > MaxUserNameLength || value.Any(char.IsControl))
            throw new AdvisorOptionsException("user_name_invalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new AdvisorOptionsException("user_name_invalid"); }
        return value;
    }
    internal static string ValidateCommonPrompt(string value)
    {
        value = value.Trim();
        if (value.Length > MaxCommonPromptLength || value.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new AdvisorOptionsException("common_prompt_invalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new AdvisorOptionsException("common_prompt_invalid"); }
        return value;
    }
    internal byte[] Data()
    {
        var value = new JObject
        {
            ["schema_version"] = AdvisorOptionsMigration.CurrentVersion,
            ["crew_enabled"] = CrewEnabled, ["max_query_rounds"] = MaxQueryRounds, ["campaign_advisor_enabled"] = CampaignEnabled,
            ["information_selection"] = Information.Data(), ["common_prompt"] = CommonPrompt,
            ["conversation_text_size"] = TextSize.ToString().ToLowerInvariant(),
            ["link_own_units"] = LinkOwnUnits, ["link_contacts"] = LinkContacts,
            ["unit_mention_completion"] = UnitMentionCompletion, ["summarize_mission_text"] = SummarizeMissionText,
            ["developer_local_api_enabled"] = DeveloperApiEnabled,
            ["disabled_commands"] = new JArray(DisabledCommands),
            ["developer_local_api_connections"] = LocalConnections.Json(),
            ["history_exchanges"] = HistoryExchanges, ["template_options"] = Templates.Json()
        };
        if (UserName != null) value["user_name"] = UserName;
        return Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
    }

    internal static AdvisorOptions Parse(string json) => Parse(json, out _);
    internal static AdvisorOptions Parse(string json, out bool migrated)
    {
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 5 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new AdvisorOptionsException("options_invalid");
            var upgraded = AdvisorOptionsMigration.Upgrade(value);
            migrated = !ReferenceEquals(value, upgraded); value = upgraded;
            if (value.Properties().Any(p => p.Name != "schema_version" && p.Name != "summarize_mission_text" && p.Name != "unit_mention_completion" && p.Name != "crew_enabled" && p.Name != "max_query_rounds" && p.Name != "campaign_advisor_enabled" && p.Name != "information_selection" && p.Name != "common_prompt" && p.Name != "conversation_text_size" && p.Name != "history_exchanges" && p.Name != "user_name" && p.Name != "developer_local_api_enabled" && p.Name != "developer_local_api_connections" && p.Name != "link_own_units" && p.Name != "link_contacts" && p.Name != "disabled_commands" && p.Name != "template_options"))
                throw new AdvisorOptionsException("options_invalid");
            var rounds = AdvisorQueryLimits.DefaultMaxRounds;
            if (value["max_query_rounds"] != null && (value["max_query_rounds"]!.Type != JTokenType.Integer ||
                !int.TryParse(value["max_query_rounds"]!.ToString(), out rounds)))
                throw new AdvisorOptionsException("query_rounds_invalid");
            var historyExchanges = DefaultHistoryExchanges;
            if (value["history_exchanges"] != null && (value["history_exchanges"]!.Type != JTokenType.Integer ||
                !int.TryParse(value["history_exchanges"]!.ToString(), out historyExchanges)))
                throw new AdvisorOptionsException("history_exchanges_invalid");
            if (value["campaign_advisor_enabled"] != null && value["campaign_advisor_enabled"]!.Type != JTokenType.Boolean)
                throw new AdvisorOptionsException("options_invalid");
            if (value["developer_local_api_enabled"] != null && value["developer_local_api_enabled"]!.Type != JTokenType.Boolean)
                throw new AdvisorOptionsException("options_invalid");
            foreach (var key in new[] { "link_own_units", "link_contacts", "crew_enabled", "unit_mention_completion", "summarize_mission_text" })
                if (value[key] != null && value[key]!.Type != JTokenType.Boolean)
                    throw new AdvisorOptionsException("options_invalid");
            if (value["common_prompt"] != null && value["common_prompt"]!.Type != JTokenType.String)
                throw new AdvisorOptionsException("common_prompt_invalid");
            if (value["user_name"] != null && value["user_name"]!.Type != JTokenType.String)
                throw new AdvisorOptionsException("user_name_invalid");
            var information = AdvisorInformation.ParseSelections(value["information_selection"]);
            return new AdvisorOptions(rounds, (bool?)value["campaign_advisor_enabled"] ?? false, information, (string?)value["common_prompt"] ?? "", ParseTextSize(value["conversation_text_size"]), historyExchanges, (string?)value["user_name"], (bool?)value["developer_local_api_enabled"] ?? false, AdvisorLocalConnections.Parse(value["developer_local_api_connections"]), (bool?)value["link_own_units"] ?? true, (bool?)value["link_contacts"] ?? true, ReadDisabledCommands(value["disabled_commands"]), AdvisorTemplateOptions.Parse(value["template_options"]), (bool?)value["crew_enabled"] ?? false, (bool?)value["unit_mention_completion"] ?? false, (bool?)value["summarize_mission_text"] ?? false);
        }
        catch (JsonException) { throw new AdvisorOptionsException("options_invalid"); }
    }
    private static IEnumerable<string> ReadDisabledCommands(JToken? value)
    {
        if (value == null) return Array.Empty<string>();
        if (value is not JArray ids || ids.Any(id => id.Type != JTokenType.String))
            throw new AdvisorOptionsException("options_invalid");
        return ids.Select(id => (string)id!);
    }
    private static AdvisorTextSize ParseTextSize(JToken? value)
    {
        if (value == null) return AdvisorTypography.DefaultSize;
        if (value.Type != JTokenType.String) throw new AdvisorOptionsException("options_invalid");
        return (string?)value switch
        {
            "small" => AdvisorTextSize.Small, "standard" => AdvisorTextSize.Standard, "large" => AdvisorTextSize.Large,
            _ => throw new AdvisorOptionsException("options_invalid")
        };
    }
}

internal sealed class AdvisorOptionsStore
{
    internal string Path { get; }
    internal AdvisorOptions Current { get; private set; }
    internal static string PathIn(string directory) => System.IO.Path.Combine(directory, "SeaPowerAIAdvisor", "options.json");
    internal AdvisorOptionsStore(string path) { Path = path; Current = Read(); }
    internal AdvisorOptions Read()
    {
        if (Directory.Exists(Path)) throw new AdvisorOptionsException("options_unreadable");
        var data = Backup();
        if (data == null) return new AdvisorOptions();
        AdvisorOptions value; bool migrated;
        try { value = AdvisorOptions.Parse(ConfigFile.Decode(data), out migrated); }
        catch (DecoderFallbackException) { throw new AdvisorOptionsException("options_invalid"); }
        if (migrated)
        {
            try { ConfigFile.WriteMigration(Path, data, value.Data()); }
            catch (IOException) { throw new AdvisorOptionsException("options_write_failed"); }
            catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_write_failed"); }
        }
        return value;
    }
    internal byte[]? Backup()
    {
        try { return ConfigFile.Backup(Path); }
        catch (IOException) { throw new AdvisorOptionsException("options_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_unreadable"); }
    }
    internal void Write(AdvisorOptions value)
    {
        try { ConfigFile.Write(Path, value.Data()); }
        catch (IOException) { throw new AdvisorOptionsException("options_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_write_failed"); }
    }
    internal void EnsureUserName(string initialName)
    {
        if (Current.UserName != null) return;
        var saved = Read();
        if (saved.UserName != null) { Apply(saved); return; }
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, initialName, saved.DeveloperApiEnabled, saved.LocalConnections, saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, saved.CrewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveGeneral(AdvisorTextSize size, string userName, bool? linkOwnUnits = null, bool? linkContacts = null, bool? unitMentionCompletion = null)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt, size, saved.HistoryExchanges, userName, saved.DeveloperApiEnabled, saved.LocalConnections, linkOwnUnits ?? saved.LinkOwnUnits, linkContacts ?? saved.LinkContacts, saved.DisabledCommands, saved.Templates, saved.CrewEnabled, unitMentionCompletion ?? saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveCrew(bool enabled)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections,
            saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, enabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveWip(bool campaignEnabled, bool crewEnabled)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, campaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections,
            saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, crewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveConsultation(int rounds, int history, string prompt)
    {
        var saved = Read();
        Save(new AdvisorOptions(rounds, saved.CampaignEnabled, saved.Information, prompt, saved.TextSize, history, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections, saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, saved.CrewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveInformation(AdvisorInformation information, bool? summarizeMissionText = null)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, information, saved.CommonPrompt, saved.TextSize, saved.HistoryExchanges, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections, saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, saved.CrewEnabled, saved.UnitMentionCompletion, summarizeMissionText ?? saved.SummarizeMissionText));
    }
    internal void SaveDeveloper(bool enabled, AdvisorLocalConnections? connections = null)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, saved.UserName, enabled, connections ?? saved.LocalConnections, saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, saved.Templates, saved.CrewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveCommands(IEnumerable<string> disabledCommands)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections,
            saved.LinkOwnUnits, saved.LinkContacts, disabledCommands, saved.Templates, saved.CrewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    internal void SaveTemplates(AdvisorTemplateOptions templates)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, saved.UserName, saved.DeveloperApiEnabled, saved.LocalConnections,
            saved.LinkOwnUnits, saved.LinkContacts, saved.DisabledCommands, templates, saved.CrewEnabled, saved.UnitMentionCompletion, saved.SummarizeMissionText));
    }
    private void Save(AdvisorOptions value) { Write(value); Apply(value); }
    internal void Apply(AdvisorOptions value) { Current = value; }
}

internal sealed class AdvisorOptionsException : Exception
{
    internal string Code { get; }
    internal AdvisorOptionsException(string code) : base(code) { Code = code; }
}
