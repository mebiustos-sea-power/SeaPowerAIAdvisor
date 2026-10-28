using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorSettings
{
    internal const int MaxDocumentBytes = 65_536;
    internal const string DefaultModel = "gemini-3.5-flash-lite";
    internal string ApiKey { get; }
    internal string FallbackApiKey { get; }
    internal bool IsFallbackKey { get; }
    internal string RequestApiKey => IsFallbackKey ? FallbackApiKey : ApiKey;
    internal string Model { get; }
    internal string FallbackModel { get; }
    internal string RequestModel => IsFallbackKey ? FallbackModel : Model;
    private readonly int requestTimeoutSeconds, fallbackRequestTimeoutSeconds;
    internal int RequestTimeoutSeconds => IsFallbackKey ? fallbackRequestTimeoutSeconds : requestTimeoutSeconds;
    internal int MaxOutputTokens { get; }
    internal bool LogAiJson { get; }
    internal AiConnectionSettings ToConnectionSettings() => new(RequestModel, RequestApiKey, RequestTimeoutSeconds, MaxOutputTokens, new JObject(), IsFallbackKey);
    internal static int Revision { get; private set; }
    internal static string PathIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "api-settings.json");

    // Rename once before normal startup reads. Move never overwrites an existing destination.
    internal static string MigrateFileName(string gameDataPath)
    {
        var path = PathIn(gameDataPath);
        static bool Exists(string candidate)
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.Directory) != 0) throw new IOException();
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        try
        {
            if (Exists(path)) return path;
            var legacy = Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "gemini.json");
            if (Exists(legacy)) File.Move(legacy, path);
            return path;
        }
        catch (IOException) { throw new AiConnectionException("settings_migration_failed"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_migration_failed"); }
    }

    private AdvisorSettings(string key, string model, int requestTimeout, int outputTokens, bool logAiJson,
        string fallbackKey = "", string fallbackModel = "", bool isFallbackKey = false, int? fallbackRequestTimeout = null)
    { ApiKey = key; FallbackApiKey = fallbackKey; IsFallbackKey = isFallbackKey; Model = model; FallbackModel = fallbackModel; requestTimeoutSeconds = requestTimeout;
        fallbackRequestTimeoutSeconds = fallbackRequestTimeout ?? requestTimeout; MaxOutputTokens = outputTokens; LogAiJson = logAiJson; }

    internal AdvisorSettings ForFallback()
    {
        if (FallbackApiKey.Length == 0) throw new AiConnectionException("fallback_key_missing");
        return new AdvisorSettings(ApiKey, Model, requestTimeoutSeconds, MaxOutputTokens, LogAiJson, FallbackApiKey, FallbackModel, true, fallbackRequestTimeoutSeconds);
    }

    internal static AdvisorSettings Load(string path) => Parse(ReadDocument(path));
    internal static bool LoadLogging(string path) => File.Exists(path) && Logging(ReadDocument(path));

    internal static AdvisorSettingsDraft Edit(string path, string namePattern = "API setting {0}") =>
        new(File.Exists(path) ? ReadDocument(path) : new JObject(), namePattern);

    internal static void SaveLogging(string path, bool enabled)
    {
        var value = File.Exists(path) ? ReadDocument(path) : new JObject { ["api_key"] = "" };
        value["log_ai_json"] = enabled;
        WriteBytes(path, Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine));
    }

    internal static byte[]? Backup(string path)
    {
        try { return ConfigFile.Backup(path); }
        catch (InvalidDataException) { throw new AiConnectionException("settings_invalid"); }
        catch (IOException) { throw new AiConnectionException("settings_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_unreadable"); }
    }
    internal static void Restore(string path, byte[]? before)
    {
        try { ConfigFile.Restore(path, before); Revision++; }
        catch (IOException) { throw new AiConnectionException("settings_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_write_failed"); }
    }
    internal static void WriteBytes(string path, byte[] bytes)
    {
        try { ConfigFile.Write(path, bytes); Revision++; }
        catch (IOException) { throw new AiConnectionException("settings_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_write_failed"); }
    }

    internal static void ValidateDraft(JObject value)
    {
        var library = AdvisorApiLibrary.Read(value);
        foreach (var entry in library.Configurations)
        {
            var service = AdvisorServiceCatalog.BuiltIn.Find(entry.Provider);
            if (service == null) continue;
            AdvisorServices.ValidateDelivery(service, entry);
            service.Validate(entry.Settings);
        }
        ValidateParameters(value);
    }

    private static void ValidateParameters(JObject value)
    {
        _ = new AdvisorConnectionOptions(value);
    }

    private static JObject ReadDocument(string path)
    {
        if (!File.Exists(path)) throw new AiConnectionException("settings_missing");
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxDocumentBytes) throw new AiConnectionException("settings_invalid");
            text = File.ReadAllText(path);
        }
        catch (IOException) { throw new AiConnectionException("settings_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_unreadable"); }
        return ParseDocument(text);
    }

    internal static AdvisorSettings Parse(string json) => Parse(ParseDocument(json));

    private static JObject ParseDocument(string json)
    {
        JObject value;
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
            value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException();
        }
        catch (JsonException) { throw new AiConnectionException("settings_invalid"); }
        if (value.Properties().Any(p => !new[] { "api_key", "fallback_api_key", "model", "fallback_model", "request_timeout_seconds", "fallback_request_timeout_seconds", "consultation_timeout_seconds", "max_output_tokens", "log_ai_json", "schema_version", "api_configurations", "main_api_id", "sub_api_id" }.Contains(p.Name)))
            throw new AiConnectionException("settings_invalid");
        _ = Logging(value);
        return value;
    }

    private static bool Logging(JObject value)
    {
        if (value["log_ai_json"] != null && value["log_ai_json"]!.Type != JTokenType.Boolean)
            throw new AiConnectionException("settings_invalid");
        return (bool?)value["log_ai_json"] ?? false;
    }

    private static AdvisorSettings Parse(JObject value, bool requireKey = true)
    {
        if (value["schema_version"] == null)
        {
            if (new[] { "api_configurations", "main_api_id", "sub_api_id" }.Any(k => value[k] != null))
                throw new AiConnectionException("settings_invalid");
            return ParseLegacy(value, requireKey);
        }
        var library = AdvisorApiLibrary.Read(value);
        var main = library.Find(library.MainId) ?? throw new AiConnectionException("api_main_missing");
        var sub = library.Find(library.SubId);
        if (main.Provider != "gemini" || (sub != null && sub.Provider != "gemini"))
            throw new AiConnectionException("provider_unavailable");
        if (sub != null && sub.ApiKey.Length == 0) throw new AiConnectionException("fallback_key_missing");
        var resolved = ParseLegacy(AdvisorApiLibrary.Connection(value, main, sub), requireKey);
        foreach (var configuration in library.Configurations.Where(c => c.Id != library.MainId && c.Id != library.SubId && c.Provider == "gemini"))
            _ = ParseLegacy(AdvisorApiLibrary.Connection(value, configuration), true);
        return resolved;
    }

    private static AdvisorSettings ParseLegacy(JObject value, bool requireKey)
    {
        if (value["api_key"]?.Type != JTokenType.String) throw new AiConnectionException("api_key_missing");
        var key = ((string)value["api_key"]!).Trim();
        if (requireKey && key.Length == 0) throw new AiConnectionException("api_key_missing");
        if (key.Length != 0 && !Regex.IsMatch(key, @"\A[!-~]{1,512}\z")) throw new AiConnectionException("settings_invalid");
        if (value["fallback_api_key"] != null && value["fallback_api_key"]!.Type != JTokenType.String)
            throw new AiConnectionException("settings_invalid");
        var fallbackKey = ((string?)value["fallback_api_key"] ?? "").Trim();
        if (fallbackKey.Length != 0 && !Regex.IsMatch(fallbackKey, @"\A[!-~]{1,512}\z")) throw new AiConnectionException("settings_invalid");
        var model = value["model"] == null ? DefaultModel : value["model"]?.Type == JTokenType.String ? (string)value["model"]! : "";
        if (!Regex.IsMatch(model, @"\Agemini-[a-z0-9][a-z0-9._-]*\z") || model.Length > 100)
            throw new AiConnectionException("model_invalid");
        // Legacy configurations used one model for both keys; preserve that choice until explicitly edited.
        var fallbackModel = value["fallback_model"] == null ? model : value["fallback_model"]!.Type == JTokenType.String ? (string)value["fallback_model"]! : null;
        if (fallbackModel == null || (fallbackModel.Length > 0 &&
            (!Regex.IsMatch(fallbackModel, @"\Agemini-[a-z0-9][a-z0-9._-]*\z") || fallbackModel.Length > 100)) ||
            (fallbackKey.Length > 0 && fallbackModel.Length == 0))
            throw new AiConnectionException("fallback_model_invalid");
        var request = AiRequestTimeout.Read(value);
        var fallbackTimeout = value["fallback_request_timeout_seconds"] == null ? request :
            AiRequestTimeout.Parse(value["fallback_request_timeout_seconds"]!.Type == JTokenType.Integer ? value["fallback_request_timeout_seconds"]!.ToString() : "");
        return new AdvisorSettings(key, model, request, AiOutputTokens.Read(value), Logging(value), fallbackKey, fallbackModel, fallbackRequestTimeout: fallbackTimeout);
    }

}
