using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

internal enum CrewNotificationPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

// Reports owns its assignment and preferences; the registered connections remain in the shared library.
internal sealed class CrewSettings
{
    internal const CrewNotificationPosition DefaultNotificationPosition = CrewNotificationPosition.TopCenter;
    internal string? ConnectionId { get; }
    internal int MinimumMinutes { get; }
    internal int MaximumMinutes { get; }
    internal int DisplaySeconds { get; }
    internal CrewNotificationPosition NotificationPosition { get; }
    internal CrewSettings(string? connectionId = null, int minimumMinutes = 1, int maximumMinutes = 5, int displaySeconds = 10, CrewNotificationPosition notificationPosition = DefaultNotificationPosition)
    {
        if (connectionId != null && (string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 80) ||
            minimumMinutes < 1 || maximumMinutes < minimumMinutes || maximumMinutes > 60 || displaySeconds < 1 || displaySeconds > 60)
            throw new CrewException("settings_invalid");
        _ = PositionCode(notificationPosition); NotificationPosition = notificationPosition;
        ConnectionId = connectionId; MinimumMinutes = minimumMinutes; MaximumMinutes = maximumMinutes; DisplaySeconds = displaySeconds;
    }
    internal JObject Json() => new() { ["connection_id"] = ConnectionId, ["minimum_minutes"] = MinimumMinutes,
        ["maximum_minutes"] = MaximumMinutes, ["display_seconds"] = DisplaySeconds, ["notification_position"] = PositionCode(NotificationPosition) };
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
    internal static CrewSettings Parse(string json)
    {
        try
        {
            var value = JObject.Parse(json);
            foreach (var p in value.Properties())
                if (p.Name != "connection_id" && p.Name != "minimum_minutes" && p.Name != "maximum_minutes" && p.Name != "display_seconds" && p.Name != "notification_position")
                    throw new CrewException("settings_invalid");
            if (value["connection_id"] is JToken id && id.Type is not (JTokenType.Null or JTokenType.String)) throw new CrewException("settings_invalid");
            int Read(string key, int fallback) => value[key] == null ? fallback : value[key]!.Type == JTokenType.Integer
                ? value[key]!.Value<int>() : throw new CrewException("settings_invalid");
            return new CrewSettings((string?)value["connection_id"], Read("minimum_minutes", 1), Read("maximum_minutes", 5), Read("display_seconds", 10), ReadPosition(value["notification_position"]));
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
        try { var bytes = ConfigFile.Backup(path); return bytes == null ? new CrewSettings() : CrewSettings.Parse(Encoding.UTF8.GetString(bytes)); }
        catch (IOException) { throw new CrewException("settings_read_failed"); }
        catch (UnauthorizedAccessException) { throw new CrewException("settings_read_failed"); }
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
    internal CrewException(string code) : base(code) => Code = code;
}
