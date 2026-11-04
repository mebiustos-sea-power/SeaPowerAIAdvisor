using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorSettings
{
    internal const int MaxDocumentBytes = 65_536;
    internal const string DefaultModel = AiProviderDefinition.GeminiDefaultModel;
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
        new(File.Exists(path) ? ReadDocument(path, namePattern) : new JObject(), namePattern);

    internal static void SaveLogging(string path, bool enabled)
    {
        var value = File.Exists(path) ? ReadDocument(path) : new AdvisorApiLibrary(Array.Empty<AdvisorApiConfiguration>(), null, null).Document(new JObject(), false);
        value["log_ai_json"] = enabled;
        WriteBytes(path, Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine));
    }

    internal static byte[]? Backup(string path)
    {
        try { return ConfigFile.Backup(path); }
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

    private static JObject ReadDocument(string path, string namePattern = "API setting {0}")
    {
        if (!File.Exists(path)) throw new AiConnectionException("settings_missing");
        byte[] before;
        try
        {
            if (new FileInfo(path).Length > MaxDocumentBytes) throw new AiConnectionException("settings_invalid");
            before = File.ReadAllBytes(path);
        }
        catch (IOException) { throw new AiConnectionException("settings_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_unreadable"); }
        JObject value;
        bool migrated;
        try { value = ParseDocument(ConfigFile.Decode(before), out migrated, namePattern); }
        catch (DecoderFallbackException) { throw new AiConnectionException("settings_invalid"); }
        if (migrated)
        {
            ValidateDraft(value);
            var after = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
            if (after.Length > MaxDocumentBytes) throw new AiConnectionException("settings_invalid");
            try { ConfigFile.WriteMigration(path, before, after); Revision++; }
            catch (IOException) { throw new AiConnectionException("settings_write_failed"); }
            catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_write_failed"); }
        }
        return value;
    }

    internal static AdvisorSettings Parse(string json) => Parse(ParseDocument(json, out _));

    private static JObject ParseDocument(string json, out bool migrated, string namePattern = "API setting {0}")
    {
        JObject value;
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
            var original = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException();
            value = AdvisorApiSettingsMigration.Upgrade(original, namePattern); migrated = !ReferenceEquals(original, value);
        }
        catch (JsonException) { throw new AiConnectionException("settings_invalid"); }
        if (value.Properties().Any(p => p.Name is not ("request_timeout_seconds" or "log_ai_json" or
            "schema_version" or "api_configurations" or "main_api_id" or "sub_api_id" or "translation_api_id")))
            throw new AiConnectionException("settings_invalid");
        _ = AdvisorApiLibrary.Read(value); _ = Logging(value); ValidateParameters(value);
        return value;
    }

    private static bool Logging(JObject value)
    {
        if (value["log_ai_json"] != null && value["log_ai_json"]!.Type != JTokenType.Boolean)
            throw new AiConnectionException("settings_invalid");
        return (bool?)value["log_ai_json"] ?? false;
    }

    private static AdvisorSettings Parse(JObject value)
    {
        var library = AdvisorApiLibrary.Read(value);
        var main = library.Find(library.MainId) ?? throw new AiConnectionException("api_main_missing");
        var sub = library.Find(library.SubId);
        var resolved = FromConnections(value, main, sub);
        foreach (var configuration in library.Configurations.Where(c => c.Id != library.MainId && c.Id != library.SubId && c.Provider == "gemini"))
            _ = FromConnections(value, configuration);
        return resolved;
    }

    internal static AdvisorSettings FromConnections(JObject source, AdvisorApiConfiguration main, AdvisorApiConfiguration? sub = null)
    {
        if (main.Provider != "gemini" || sub != null && sub.Provider != "gemini") throw new AiConnectionException("provider_unavailable");
        var definition = AiProviderDefinition.Require("gemini");
        definition.Validate(main.Settings);
        if (sub != null)
        {
            try { definition.Validate(sub.Settings); }
            catch (AiConnectionException error) when (error.Code == "api_key_missing") { throw new AiConnectionException("fallback_key_missing"); }
            catch (AiConnectionException error) when (error.Code == "model_invalid") { throw new AiConnectionException("fallback_model_invalid"); }
        }
        return new AdvisorSettings(main.ApiKey.Trim(), main.Model, main.RequestTimeoutSeconds, main.MaxOutputTokens, Logging(source),
            sub?.ApiKey.Trim() ?? "", sub?.Model ?? "", fallbackRequestTimeout: sub?.RequestTimeoutSeconds ?? main.RequestTimeoutSeconds);
    }
}
