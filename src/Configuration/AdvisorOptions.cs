using System;
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
    internal AdvisorInformation Information { get; }
    internal AdvisorTextSize TextSize { get; }
    internal AdvisorOptions(int maxQueryRounds = AdvisorQueryLimits.DefaultMaxRounds, bool campaignEnabled = false, AdvisorInformation? information = null, string commonPrompt = "", AdvisorTextSize textSize = AdvisorTypography.DefaultSize, int historyExchanges = DefaultHistoryExchanges, string? userName = null)
    {
        if (maxQueryRounds < 1 || maxQueryRounds > AdvisorQueryLimits.MaximumRounds)
            throw new AdvisorOptionsException("query_rounds_invalid");
        MaxQueryRounds = maxQueryRounds; CampaignEnabled = campaignEnabled; Information = information ?? AdvisorInformation.Default;
        if (!Enum.IsDefined(typeof(AdvisorTextSize), textSize)) throw new AdvisorOptionsException("options_invalid");
        TextSize = textSize;
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
            ["max_query_rounds"] = MaxQueryRounds, ["campaign_advisor_enabled"] = CampaignEnabled,
            ["information_selection"] = Information.Data(), ["common_prompt"] = CommonPrompt,
            ["conversation_text_size"] = TextSize.ToString().ToLowerInvariant(),
            ["history_exchanges"] = HistoryExchanges
        };
        if (UserName != null) value["user_name"] = UserName;
        return Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
    }

    internal static AdvisorOptions Parse(string json)
    {
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 5 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value.Properties().Any(p => p.Name != "max_query_rounds" && p.Name != "campaign_advisor_enabled" && p.Name != "disabled_information" && p.Name != "information_selection" && p.Name != "common_prompt" && p.Name != "conversation_text_size" && p.Name != "history_exchanges" && p.Name != "user_name"))
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
            if (value["common_prompt"] != null && value["common_prompt"]!.Type != JTokenType.String)
                throw new AdvisorOptionsException("common_prompt_invalid");
            if (value["user_name"] != null && value["user_name"]!.Type != JTokenType.String)
                throw new AdvisorOptionsException("user_name_invalid");
            if (value["disabled_information"] != null && value["information_selection"] != null)
                throw new AdvisorOptionsException("information_invalid");
            var information = value["information_selection"] == null ? AdvisorInformation.Parse(value["disabled_information"])
                : AdvisorInformation.ParseSelections(value["information_selection"]);
            return new AdvisorOptions(rounds, (bool?)value["campaign_advisor_enabled"] ?? false, information, (string?)value["common_prompt"] ?? "", ParseTextSize(value["conversation_text_size"]), historyExchanges, (string?)value["user_name"]);
        }
        catch (JsonException) { throw new AdvisorOptionsException("options_invalid"); }
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
        return data == null ? new AdvisorOptions() : AdvisorOptions.Parse(Encoding.UTF8.GetString(data));
    }
    internal byte[]? Backup()
    {
        try { return AdvisorConfigFile.Backup(Path); }
        catch (InvalidDataException) { throw new AdvisorOptionsException("options_invalid"); }
        catch (IOException) { throw new AdvisorOptionsException("options_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_unreadable"); }
    }
    internal void Write(AdvisorOptions value)
    {
        try { AdvisorConfigFile.Write(Path, value.Data()); }
        catch (IOException) { throw new AdvisorOptionsException("options_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_write_failed"); }
    }
    internal void Restore(byte[]? before)
    {
        try { AdvisorConfigFile.Restore(Path, before); }
        catch (IOException) { throw new AdvisorOptionsException("options_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorOptionsException("options_write_failed"); }
    }
    internal void EnsureUserName(string initialName)
    {
        if (Current.UserName != null) return;
        var saved = Read();
        if (saved.UserName != null) { Apply(saved); return; }
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, saved.Information, saved.CommonPrompt,
            saved.TextSize, saved.HistoryExchanges, initialName));
    }
    internal void SaveGeneral(AdvisorTextSize size, bool campaign, string userName)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, campaign, saved.Information, saved.CommonPrompt, size, saved.HistoryExchanges, userName));
    }
    internal void SaveConsultation(int rounds, int history, string prompt)
    {
        var saved = Read();
        Save(new AdvisorOptions(rounds, saved.CampaignEnabled, saved.Information, prompt, saved.TextSize, history, saved.UserName));
    }
    internal void SaveInformation(AdvisorInformation information)
    {
        var saved = Read();
        Save(new AdvisorOptions(saved.MaxQueryRounds, saved.CampaignEnabled, information, saved.CommonPrompt, saved.TextSize, saved.HistoryExchanges, saved.UserName));
    }
    private void Save(AdvisorOptions value) { Write(value); Apply(value); }
    internal void Apply(AdvisorOptions value) { Current = value; }
}

internal sealed class AdvisorOptionsException : Exception
{
    internal string Code { get; }
    internal AdvisorOptionsException(string code) : base(code) { Code = code; }
}
