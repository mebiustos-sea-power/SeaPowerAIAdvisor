using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

internal enum CrewNotificationPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

// Chatter owns its assignment and preferences; the registered connections remain in the shared library.
internal sealed class CrewSettings
{
    internal const int DefaultIntervalMinutes = 3;
    internal const int DefaultExchangeChancePercent = 50;
    internal const CrewNotificationPosition DefaultNotificationPosition = CrewNotificationPosition.TopRight;
    internal string? ConnectionId { get; }
    internal int IntervalMinutes { get; }
    internal int ExchangeChancePercent { get; }
    internal int DisplaySeconds { get; }
    internal CrewNotificationPosition NotificationPosition { get; }
    internal bool ShowHelpTab { get; }
    internal bool ShowRanks { get; }
    internal bool AutoAppointments { get; }
    internal CrewInformation Information { get; }
    internal CrewSettings(string? connectionId = null, int intervalMinutes = DefaultIntervalMinutes, int displaySeconds = 10, CrewNotificationPosition notificationPosition = DefaultNotificationPosition, bool showHelpTab = true, int exchangeChancePercent = DefaultExchangeChancePercent, CrewInformation? information = null, bool showRanks = true, bool autoAppointments = false)
    {
        if (connectionId != null && (string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 80) ||
            intervalMinutes < 1 || intervalMinutes > 15 || displaySeconds < 1 || displaySeconds > 60 || exchangeChancePercent < 0 || exchangeChancePercent > 100)
            throw new CrewException("settings_invalid");
        _ = PositionCode(notificationPosition); NotificationPosition = notificationPosition;
        ConnectionId = connectionId; IntervalMinutes = intervalMinutes; DisplaySeconds = displaySeconds; ShowHelpTab = showHelpTab; ExchangeChancePercent = exchangeChancePercent;
        Information = information ?? CrewInformation.Default; ShowRanks = showRanks; AutoAppointments = autoAppointments;
    }
    internal JObject Json() => new() { ["schema_version"] = CrewSettingsMigration.CurrentVersion, ["connection_id"] = ConnectionId, ["interval_minutes"] = IntervalMinutes, ["display_seconds"] = DisplaySeconds, ["notification_position"] = PositionCode(NotificationPosition), ["show_help_tab"] = ShowHelpTab, ["show_ranks"] = ShowRanks, ["auto_appointments"] = AutoAppointments, ["exchange_chance_percent"] = ExchangeChancePercent, ["information_selection"] = Information.Json() };
    internal static string PositionCode(CrewNotificationPosition position) => position switch
    {
        CrewNotificationPosition.TopLeft => "top_left", CrewNotificationPosition.TopCenter => "top_center", CrewNotificationPosition.TopRight => "top_right",
        CrewNotificationPosition.BottomLeft => "bottom_left", CrewNotificationPosition.BottomCenter => "bottom_center", CrewNotificationPosition.BottomRight => "bottom_right",
        _ => throw new CrewException("settings_invalid")
    };
    private static CrewNotificationPosition ReadPosition(JToken? value)
    {
        if (value == null) return DefaultNotificationPosition;
        if (value.Type == JTokenType.String)
            foreach (CrewNotificationPosition position in Enum.GetValues(typeof(CrewNotificationPosition)))
                if ((string?)value == PositionCode(position)) return position;
        throw new CrewException("settings_invalid");
    }
    internal static CrewSettings Parse(string json) => Parse(json, out _);
    internal static CrewSettings Parse(string json, out bool migrated)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 5 };
            var original = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new CrewException("settings_invalid");
            var value = CrewSettingsMigration.Upgrade(original); migrated = !ReferenceEquals(original, value);
            foreach (var p in value.Properties())
                if (p.Name != "schema_version" && p.Name != "connection_id" && p.Name != "interval_minutes" && p.Name != "display_seconds" && p.Name != "notification_position" && p.Name != "show_help_tab" && p.Name != "show_ranks" && p.Name != "auto_appointments" && p.Name != "exchange_chance_percent" && p.Name != "information_selection")
                    throw new CrewException("settings_invalid");
            if (value["connection_id"] is JToken id && id.Type is not (JTokenType.Null or JTokenType.String)) throw new CrewException("settings_invalid");
            if (value["show_help_tab"] is JToken showHelp && showHelp.Type != JTokenType.Boolean) throw new CrewException("settings_invalid");
            if (value["show_ranks"] is JToken showRanks && showRanks.Type != JTokenType.Boolean) throw new CrewException("settings_invalid");
            if (value["auto_appointments"] is JToken autoAppointments && autoAppointments.Type != JTokenType.Boolean) throw new CrewException("settings_invalid");
            int Read(string key, int fallback) => value[key] == null ? fallback : value[key]!.Type == JTokenType.Integer
                ? value[key]!.Value<int>() : throw new CrewException("settings_invalid");
            return new CrewSettings((string?)value["connection_id"], Read("interval_minutes", DefaultIntervalMinutes), Read("display_seconds", 10), ReadPosition(value["notification_position"]), (bool?)value["show_help_tab"] ?? true, Read("exchange_chance_percent", DefaultExchangeChancePercent), CrewInformation.Parse(value["information_selection"]), (bool?)value["show_ranks"] ?? true, (bool?)value["auto_appointments"] ?? false);
        }
        catch (Exception e) when (e is JsonException || e is OverflowException) { throw new CrewException("settings_invalid"); }
    }
}

internal sealed class CrewSettingsStore
{
    private readonly string path;
    internal CrewSettings Current { get; private set; }
    internal CrewSettingsStore(string path) { this.path = path; Current = Read(); }
    private CrewSettings Read()
    {
        byte[]? bytes;
        try { bytes = ConfigFile.Backup(path); }
        catch (IOException) { throw new CrewException("settings_read_failed"); }
        catch (UnauthorizedAccessException) { throw new CrewException("settings_read_failed"); }
        if (bytes == null) return new CrewSettings();
        CrewSettings value; bool migrated;
        try { value = CrewSettings.Parse(ConfigFile.Decode(bytes), out migrated); }
        catch (DecoderFallbackException) { throw new CrewException("settings_invalid"); }
        try
        {
            if (migrated) ConfigFile.WriteMigration(path, bytes, Encoding.UTF8.GetBytes(value.Json().ToString(Formatting.Indented) + Environment.NewLine));
            return value;
        }
        catch (IOException) { throw new CrewException("settings_write_failed"); }
        catch (UnauthorizedAccessException) { throw new CrewException("settings_write_failed"); }
    }
    internal void Save(CrewSettings value)
    {
        try { ConfigFile.Write(path, Encoding.UTF8.GetBytes(value.Json().ToString(Formatting.Indented) + Environment.NewLine)); Current = value; }
        catch (IOException) { throw new CrewException("settings_write_failed"); }
        catch (UnauthorizedAccessException) { throw new CrewException("settings_write_failed"); }
    }
}

internal sealed class CrewException : Exception
{
    internal string Code { get; }
    internal CrewException(string code, Exception? inner = null) : base(code, inner) => Code = code;
}
